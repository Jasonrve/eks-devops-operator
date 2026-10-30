using Amazon.EKS;
using Amazon.EKS.Model;
using Amazon.IdentityManagement;
using Amazon.Runtime;
using Amazon.SecurityToken;
using FluentAssertions;
using Xunit.Abstractions;

namespace EksDevopsOperator.IntegrationTests;

// Probes the pinned Floci with the real AWS SDK. Prints "CAP <name>: OK|FAIL(reason)" (recorded in
// docs/floci-capabilities.md). Required management-plane capabilities are asserted; optional ones are only reported.
public class FlociCapabilityTests(ITestOutputHelper output)
{
    static readonly string Endpoint = Environment.GetEnvironmentVariable("FLOCI_ENDPOINT") ?? "http://localhost:4566";
    const string Trust = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"pods.eks.amazonaws.com"},"Action":["sts:AssumeRole","sts:TagSession"]}]}""";
    const string Doc = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":"s3:GetObject","Resource":"*"}]}""";

    static (AmazonIdentityManagementServiceClient iam, AmazonSecurityTokenServiceClient sts, AmazonEKSClient eks) Clients(string acct)
    {
        var creds = new BasicAWSCredentials(acct, "test");
        return (new(creds, new AmazonIdentityManagementServiceConfig { ServiceURL = Endpoint, AuthenticationRegion = "us-east-1" }),
                new(creds, new AmazonSecurityTokenServiceConfig { ServiceURL = Endpoint, AuthenticationRegion = "us-east-1" }),
                new(creds, new AmazonEKSConfig { ServiceURL = Endpoint, AuthenticationRegion = "us-east-1" }));
    }

    async Task<bool> Probe(string name, Func<Task> act)
    {
        try { await act(); output.WriteLine($"CAP {name}: OK"); return true; }
        catch (Exception e) { output.WriteLine($"CAP {name}: FAIL({e.GetType().Name}: {e.Message.Split('\n')[0]})"); return false; }
    }

    [Fact]
    public async Task Probe_iam_sts_eks()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var (iam, sts, eks) = Clients("111111111111");
        var (iamB, _, _) = Clients("222222222222");
        var role = $"probe-{id}";
        var r = new Dictionary<string, bool>();

        r["sts:GetCallerIdentity"] = await Probe("sts:GetCallerIdentity", async () =>
            (await sts.GetCallerIdentityAsync(new())).Account.Should().Be("111111111111"));
        r["iam:CreateRole"] = await Probe("iam:CreateRole(path,tags)", async () =>
            await iam.CreateRoleAsync(new() { RoleName = role, Path = "/eks-devops-operator/", AssumeRolePolicyDocument = Trust,
                Tags = [new() { Key = "k", Value = "v" }] }));
        r["iam:GetRole"] = await Probe("iam:GetRole", async () =>
            (await iam.GetRoleAsync(new() { RoleName = role })).Role.Arn.Should().Be($"arn:aws:iam::111111111111:role/eks-devops-operator/{role}"));
        r["iam:UpdateAssumeRolePolicy"] = await Probe("iam:UpdateAssumeRolePolicy", () => iam.UpdateAssumeRolePolicyAsync(new() { RoleName = role, PolicyDocument = Trust }));
        r["iam:PutRolePolicy"] = await Probe("iam:PutRolePolicy/GetRolePolicy", async () =>
        { await iam.PutRolePolicyAsync(new() { RoleName = role, PolicyName = "p", PolicyDocument = Doc }); await iam.GetRolePolicyAsync(new() { RoleName = role, PolicyName = "p" }); });
        r["iam:ListRolePolicies"] = await Probe("iam:ListRolePolicies", async () =>
            (await iam.ListRolePoliciesAsync(new() { RoleName = role })).PolicyNames.Should().Contain("p"));
        string? polArn = null;
        r["iam:CreatePolicy"] = await Probe("iam:CreatePolicy", async () =>
            polArn = (await iam.CreatePolicyAsync(new() { PolicyName = $"pol-{id}", PolicyDocument = Doc })).Policy.Arn);
        r["iam:PolicyVersions"] = await Probe("iam:CreatePolicyVersion/ListPolicyVersions/DeletePolicyVersion", async () =>
        {
            await iam.CreatePolicyVersionAsync(new() { PolicyArn = polArn, PolicyDocument = Doc, SetAsDefault = true });
            var v = await iam.ListPolicyVersionsAsync(new() { PolicyArn = polArn });
            await iam.DeletePolicyVersionAsync(new() { PolicyArn = polArn, VersionId = v.Versions.First(x => x.IsDefaultVersion != true).VersionId });
        });
        r["iam:AttachRolePolicy"] = await Probe("iam:AttachRolePolicy/ListAttachedRolePolicies", async () =>
        { await iam.AttachRolePolicyAsync(new() { RoleName = role, PolicyArn = polArn }); (await iam.ListAttachedRolePoliciesAsync(new() { RoleName = role })).AttachedPolicies.Should().NotBeEmpty(); });
        r["iam:TagRole"] = await Probe("iam:TagRole/ListRoleTags", async () =>
        { await iam.TagRoleAsync(new() { RoleName = role, Tags = [new() { Key = "a", Value = "b" }] }); (await iam.ListRoleTagsAsync(new() { RoleName = role })).Tags.Should().HaveCountGreaterThan(1); });

        // Cross-account: target role in B trusting the source role in A; A assumes B.
        var srcArn = $"arn:aws:iam::111111111111:role/eks-devops-operator/{role}";
        r["iam:CreateRole(cross)"] = await Probe("iam:CreateRole in account B", () => iamB.CreateRoleAsync(new() { RoleName = role, AssumeRolePolicyDocument =
            $$"""{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"AWS":"{{srcArn}}"},"Action":["sts:AssumeRole","sts:TagSession"]}]}""" }));
        r["sts:AssumeRole"] = await Probe("sts:AssumeRole A->B + GetCallerIdentity on session", async () =>
        {
            var a = await sts.AssumeRoleAsync(new() { RoleArn = $"arn:aws:iam::222222222222:role/{role}", RoleSessionName = "probe" });
            var c = new SessionAWSCredentials(a.Credentials.AccessKeyId, a.Credentials.SecretAccessKey, a.Credentials.SessionToken);
            var s2 = new AmazonSecurityTokenServiceClient(c, new AmazonSecurityTokenServiceConfig { ServiceURL = Endpoint, AuthenticationRegion = "us-east-1" });
            (await s2.GetCallerIdentityAsync(new())).Account.Should().Be("222222222222");
        });
        await Probe("(optional) sts:AssumeRole denied for untrusted caller", async () =>
        {
            var (_, stsC, _) = Clients("333333333333");
            var act = async () => await stsC.AssumeRoleAsync(new() { RoleArn = $"arn:aws:iam::222222222222:role/{role}", RoleSessionName = "xx" });
            await act.Should().ThrowAsync<Exception>();
        });

        var cluster = $"c-{id}";
        var ec2 = new Amazon.EC2.AmazonEC2Client(new BasicAWSCredentials("111111111111", "test"), new Amazon.EC2.AmazonEC2Config { ServiceURL = Endpoint, AuthenticationRegion = "us-east-1" });
        var vpc = (await ec2.CreateVpcAsync(new() { CidrBlock = "10.0.0.0/16" })).Vpc.VpcId;
        var subnet = (await ec2.CreateSubnetAsync(new() { VpcId = vpc, CidrBlock = "10.0.1.0/24" })).Subnet.SubnetId;
        r["eks:CreateCluster"] = await Probe("eks:CreateCluster", () => eks.CreateClusterAsync(new() { Name = cluster, RoleArn = srcArn,
            ResourcesVpcConfig = new() { SubnetIds = [subnet] } }));
        string? assoc = null;
        r["eks:CreatePodIdentityAssociation"] = await Probe("eks:CreatePodIdentityAssociation", async () =>
            assoc = (await eks.CreatePodIdentityAssociationAsync(new() { ClusterName = cluster, Namespace = "ns", ServiceAccount = "sa", RoleArn = srcArn })).Association.AssociationId);
        r["eks:Describe"] = await Probe("eks:DescribePodIdentityAssociation", async () =>
            (await eks.DescribePodIdentityAssociationAsync(new() { ClusterName = cluster, AssociationId = assoc })).Association.RoleArn.Should().Be(srcArn));
        r["eks:List"] = await Probe("eks:ListPodIdentityAssociations(filter)", async () =>
            (await eks.ListPodIdentityAssociationsAsync(new() { ClusterName = cluster, Namespace = "ns", ServiceAccount = "sa" })).Associations.Should().HaveCount(1));
        await Probe("(optional) eks:Create duplicate => ResourceInUse", async () =>
        {
            var act = async () => await eks.CreatePodIdentityAssociationAsync(new() { ClusterName = cluster, Namespace = "ns", ServiceAccount = "sa", RoleArn = srcArn });
            await act.Should().ThrowAsync<ResourceInUseException>();
        });
        var tgt = $"arn:aws:iam::222222222222:role/{role}";
        await Probe("(optional) eks:UpdatePodIdentityAssociation(targetRoleArn)", async () =>
        {
            await eks.UpdatePodIdentityAssociationAsync(new() { ClusterName = cluster, AssociationId = assoc, TargetRoleArn = tgt });
            (await eks.DescribePodIdentityAssociationAsync(new() { ClusterName = cluster, AssociationId = assoc })).Association.TargetRoleArn.Should().Be(tgt);
        });
        await Probe("(optional) eks:CreatePodIdentityAssociation(targetRoleArn)", async () =>
        {
            var c = await eks.CreatePodIdentityAssociationAsync(new() { ClusterName = cluster, Namespace = "ns2", ServiceAccount = "sa", RoleArn = srcArn, TargetRoleArn = tgt });
            (await eks.DescribePodIdentityAssociationAsync(new() { ClusterName = cluster, AssociationId = c.Association.AssociationId })).Association.TargetRoleArn.Should().Be(tgt);
        });
        // IRSA prerequisites: reported only (never asserted), so a run shows what the emulator can and cannot do for IRSA
        await Probe("(optional) iam:CreateOpenIDConnectProvider + GetOpenIDConnectProvider", async () =>
        {
            var arn = (await iam.CreateOpenIDConnectProviderAsync(new() { Url = $"https://oidc.eks.us-east-1.amazonaws.com/id/{id.ToUpperInvariant()}", ClientIDList = ["sts.amazonaws.com"], ThumbprintList = ["9e99a48a9960b14926bb7f3b02e22da2b0ab7280"] })).OpenIDConnectProviderArn;
            (await iam.GetOpenIDConnectProviderAsync(new() { OpenIDConnectProviderArn = arn })).Url.Should().NotBeNullOrEmpty();
        });
        await Probe("(optional) eks:DescribeCluster reports identity.oidc.issuer", async () =>
            (await eks.DescribeClusterAsync(new() { Name = cluster })).Cluster.Identity.Oidc.Issuer.Should().NotBeNullOrEmpty());
        await Probe("(optional) eks:TagResource on a Pod Identity association", () => eks.TagResourceAsync(new() { ResourceArn = $"arn:aws:eks:us-east-1:111111111111:podidentityassociation/{cluster}/{assoc}", Tags = new() { ["a"] = "b" } }));
        r["eks:Delete"] = await Probe("eks:DeletePodIdentityAssociation", () => eks.DeletePodIdentityAssociationAsync(new() { ClusterName = cluster, AssociationId = assoc }));
        r["iam:Delete"] = await Probe("iam:DeleteRolePolicy/DetachRolePolicy/DeleteRole", async () =>
        {
            await iam.DeleteRolePolicyAsync(new() { RoleName = role, PolicyName = "p" });
            await iam.DetachRolePolicyAsync(new() { RoleName = role, PolicyArn = polArn });
            await iam.DeleteRoleAsync(new() { RoleName = role });
        });

        r.Where(x => !x.Value).Select(x => x.Key).Should().BeEmpty("these are required by the operator's management plane");
    }
}
