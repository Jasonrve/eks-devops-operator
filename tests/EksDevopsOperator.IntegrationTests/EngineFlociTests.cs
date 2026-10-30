using Amazon.IdentityManagement.Model;
using FluentAssertions;
using EksDevopsOperator.Core;

namespace EksDevopsOperator.IntegrationTests;

// Engine + real AWS SDK managers against Floci (no Kubernetes). Every AWS call goes to the emulator; nothing is mocked.
public class EngineFlociTests(EngineHarness h) : IClassFixture<EngineHarness>
{
    static string[] Reasons(ReconcileResult r) => (r.Changes ?? []).Select(c => c.Reason).ToArray();
    static string RoleName(string arn) => arn[(arn.LastIndexOf('/') + 1)..];

    [Fact]
    public async Task Creates_role_trust_policy_boundary_and_association()
    {
        var ns = h.NewNamespace();
        var r = await h.Reconcile(h.Request(ns));
        r.Phase.Should().Be(Phase.Ready, r.Message);
        r.Conditions.Should().OnlyContain(c => c.Status);

        var role = await h.Role(r.RoleArn!);
        role.Path.Should().Be("/eks-devops-operator/");
        role.Tags[Tags.Namespace].Should().Be(ns);
        role.Tags[Tags.Cluster].Should().Be(h.Cluster);
        role.PermissionsBoundaryArn.Should().EndWith("policy/eks-devops-operator/standard-workload");
        PolicyJson.Normalize(role.TrustPolicy).Should().Contain("pods.eks.amazonaws.com").And.Contain("sts:TagSession").And.Contain(h.Cluster).And.Contain(ns).And.NotContain(":root");
        PolicyJson.Normalize(role.InlinePolicies["pio-permissions"]).Should().Contain($"arn:aws:s3:::bucket-{ns}/*").And.NotContain("\"*\"");
        (await h.Association(ns, "api")).RoleArn.Should().Be(r.RoleArn);
    }

    [Fact]
    public async Task Reconcile_is_idempotent_and_creates_no_duplicates()
    {
        var ns = h.NewNamespace();
        var req = h.Request(ns);
        var r1 = await h.Reconcile(req);
        var r2 = await h.Reconcile(req, EngineHarness.Prior(r1));
        r2.Phase.Should().Be(Phase.Ready);
        (r2.RoleArn, r2.AssociationId).Should().Be((r1.RoleArn, r1.AssociationId));
        Reasons(r2).Should().BeEmpty();
        r2.Drift.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Deleted_inline_policy_is_detected_and_restored()
    {
        var ns = h.NewNamespace();
        var req = h.Request(ns);
        var r1 = await h.Reconcile(req);
        await h.Iam().DeleteRolePolicyAsync(new DeleteRolePolicyRequest { RoleName = RoleName(r1.RoleArn!), PolicyName = "pio-permissions" });
        var r2 = await h.Reconcile(req, EngineHarness.Prior(r1));
        r2.Phase.Should().Be(Phase.Ready);
        (await h.Role(r1.RoleArn!)).InlinePolicies.Should().ContainKey("pio-permissions");
        r2.Changes!.Should().ContainSingle(c => c.Reason == "PolicyRepaired" && c.Drift);
    }

    [Fact]
    public async Task Tampered_trust_policy_is_restored()
    {
        var ns = h.NewNamespace();
        var req = h.Request(ns);
        var r1 = await h.Reconcile(req);
        await h.Iam().UpdateAssumeRolePolicyAsync(new UpdateAssumeRolePolicyRequest { RoleName = RoleName(r1.RoleArn!),
            PolicyDocument = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"AWS":"*"},"Action":"sts:AssumeRole"}]}""" });
        var r2 = await h.Reconcile(req, EngineHarness.Prior(r1));
        r2.Phase.Should().Be(Phase.Ready);
        PolicyJson.Normalize((await h.Role(r1.RoleArn!)).TrustPolicy).Should().NotContain("\"*\"").And.Contain("pods.eks.amazonaws.com");
    }

    [Fact]
    public async Task Deleted_association_and_role_are_recreated()
    {
        var ns = h.NewNamespace();
        var req = h.Request(ns);
        var r1 = await h.Reconcile(req);
        await h.Eks().DeletePodIdentityAssociationAsync(new() { ClusterName = h.Cluster, AssociationId = r1.AssociationId });
        var iam = h.Iam();
        await iam.DeleteRolePolicyAsync(new DeleteRolePolicyRequest { RoleName = RoleName(r1.RoleArn!), PolicyName = "pio-permissions" });
        await iam.DeleteRoleAsync(new DeleteRoleRequest { RoleName = RoleName(r1.RoleArn!) });
        var r2 = await h.Reconcile(req, EngineHarness.Prior(r1));
        r2.Phase.Should().Be(Phase.Ready, r2.Message);
        (await h.Association(ns, "api")).Exists.Should().BeTrue();
        (await h.Role(r1.RoleArn!)).Exists.Should().BeTrue();
    }

    [Fact]
    public async Task Policy_update_changes_permissions_without_new_roles()
    {
        var ns = h.NewNamespace();
        var r1 = await h.Reconcile(h.Request(ns) with { Generation = 1 });
        var updated = h.Request(ns, "api", new PolicyStatement("Write", "Allow", ["s3:PutObject"], [$"arn:aws:s3:::bucket-{ns}/*"])) with { Generation = 2 };
        var r2 = await h.Reconcile(updated, EngineHarness.Prior(r1, 1));
        r2.Phase.Should().Be(Phase.Ready, r2.Message);
        r2.RoleArn.Should().Be(r1.RoleArn);
        PolicyJson.Normalize((await h.Role(r2.RoleArn!)).InlinePolicies["pio-permissions"]).Should().Contain("s3:PutObject").And.NotContain("s3:GetObject");
        Reasons(r2).Should().Contain("PolicyUpdated").And.NotContain(r => r.EndsWith("Repaired"));
        var roles = await h.Iam().ListRolesAsync(new ListRolesRequest { PathPrefix = "/eks-devops-operator/" });
        roles.Roles.Count(x => x.RoleName.Contains(Naming.Sanitize(ns))).Should().Be(1);
    }

    [Fact]
    public async Task Renaming_the_role_moves_the_association_and_removes_the_old_role()
    {
        var ns = h.NewNamespace();
        var r1 = await h.Reconcile(h.Request(ns) with { Generation = 1 });
        var r2 = await h.Reconcile(h.Request(ns) with { Generation = 2, RequestedRoleName = "renamed" }, EngineHarness.Prior(r1, 1));
        r2.Phase.Should().Be(Phase.Ready, r2.Message);
        r2.RoleArn.Should().NotBe(r1.RoleArn).And.Contain("renamed-");
        (await h.Association(ns, "api")).RoleArn.Should().Be(r2.RoleArn);
        (await h.Role(r1.RoleArn!)).Exists.Should().BeFalse("the old role is removed after the association moved");
        Reasons(r2).Should().Contain(["RoleCreated", "AssociationUpdated", "PreviousRoleDeleted"]);
    }

    [Theory]
    [InlineData(DeletionPolicy.Delete)]
    [InlineData(DeletionPolicy.Retain)]
    public async Task Deletion_policy_is_honoured(DeletionPolicy policy)
    {
        var ns = h.NewNamespace();
        var req = h.Request(ns);
        var r = await h.Reconcile(req);
        var c = await h.Engine.CleanupAsync(new CleanupInput(ns, "api", req.Uid, null, policy, EngineHarness.Prior(r)), default);
        c.Done.Should().BeTrue(c.Message);
        (await h.Association(ns, "api")).Exists.Should().BeFalse("the association is always removed");
        var role = await h.Role(r.RoleArn!);
        if (policy == DeletionPolicy.Delete) { role.Exists.Should().BeFalse(); c.Retained.Should().BeEmpty(); }
        else { role.Exists.Should().BeTrue(); role.Tags.Should().ContainKey(Tags.Operator); c.Retained.Should().ContainSingle(); }
    }

    [Fact]
    public async Task Iam_wildcard_request_is_rejected_before_any_aws_mutation()
    {
        var ns = h.NewNamespace();
        var r = await h.Reconcile(h.Request(ns, "api", new PolicyStatement("Bad", "Allow", ["iam:*"], ["*"])));
        r.Phase.Should().Be(Phase.Invalid);
        r.Message.Should().Contain("PrivilegeEscalation");
        (await h.Association(ns, "api")).Exists.Should().BeFalse();
        var roles = await h.Iam().ListRolesAsync(new ListRolesRequest { PathPrefix = "/eks-devops-operator/" });
        roles.Roles.Should().NotContain(x => x.RoleName.Contains(Naming.Sanitize(ns)));
    }

    [Fact]
    public async Task Namespace_not_governed_by_any_policy_is_denied_with_no_changes()
    {
        var ns = h.NewNamespace();
        var d = new EngineHarness();
        await d.InitializeAsync();
        d.Rules = [EngineHarness.DefaultRule() with { Namespaces = ["some-other-namespace"] }];
        var r = await d.Reconcile(d.Request(ns));
        r.Phase.Should().Be(Phase.Invalid);
        r.Message.Should().Contain("NamespaceNotAuthorized");
        (await d.Association(ns, "api")).Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Existing_unowned_role_is_not_modified()
    {
        var ns = h.NewNamespace();
        var req = h.Request(ns);
        var name = Naming.RoleName(h.Options, new ClusterContext(h.Cluster, EngineHarness.A, "aws", "us-east-1", "dev"), req);
        var victim = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"lambda.amazonaws.com"},"Action":"sts:AssumeRole"}]}""";
        await h.Iam().CreateRoleAsync(new CreateRoleRequest { RoleName = name, Path = "/eks-devops-operator/", AssumeRolePolicyDocument = victim });
        var r = await h.Reconcile(req);
        r.Phase.Should().Be(Phase.Failed);
        r.Conditions.Last().Reason.Should().Be("RoleNotOwned");
        var after = await h.Role("x/" + name);
        PolicyJson.Normalize(after.TrustPolicy).Should().Contain("lambda.amazonaws.com");
        after.InlinePolicies.Should().BeEmpty();
        (await h.Association(ns, "api")).Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Existing_association_not_owned_is_not_overwritten()
    {
        var ns = h.NewNamespace();
        await h.Iam().CreateRoleAsync(new CreateRoleRequest { RoleName = "someone-elses-" + ns, AssumeRolePolicyDocument = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"pods.eks.amazonaws.com"},"Action":["sts:AssumeRole","sts:TagSession"]}]}""" });
        await h.Eks().CreatePodIdentityAssociationAsync(new() { ClusterName = h.Cluster, Namespace = ns, ServiceAccount = "api", RoleArn = $"arn:aws:iam::{EngineHarness.A}:role/someone-elses-{ns}" });
        var r = await h.Reconcile(h.Request(ns));
        r.Phase.Should().Be(Phase.Failed);
        r.Conditions.Last().Reason.Should().Be("AssociationNotOwned");
        (await h.Association(ns, "api")).RoleArn.Should().EndWith("someone-elses-" + ns);
    }

    [Fact]
    public async Task Conflicting_claims_stop_provisioning()
    {
        var ns = h.NewNamespace();
        var d = new EngineHarness();
        await d.InitializeAsync();
        d.Conflicts.Conflicts.Add("PodIdentityAccess/other");
        var r = await d.Reconcile(d.Request(ns));
        r.Phase.Should().Be(Phase.Conflict);
        (await d.Association(ns, "api")).Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Parallel_requests_are_isolated()
    {
        var namespaces = Enumerable.Range(0, 6).Select(_ => h.NewNamespace()).ToList();
        var results = await Task.WhenAll(namespaces.Select(ns => h.Reconcile(h.Request(ns))));
        results.Should().OnlyContain(r => r.Phase == Phase.Ready);
        results.Select(r => r.RoleArn).Distinct().Should().HaveCount(6);
        for (var i = 0; i < 6; i++) (await h.Role(results[i].RoleArn!)).Tags[Tags.Namespace].Should().Be(namespaces[i]);
    }

    [Fact]
    public async Task Every_aws_change_is_reported_for_kubernetes_events()
    {
        var ns = h.NewNamespace();
        var req = h.Request(ns) with { Generation = 1 };
        var r1 = await h.Reconcile(req);
        Reasons(r1).Should().Contain(["RoleCreated", "PolicyCreated", "AssociationCreated"]);

        Reasons(await h.Reconcile(req, EngineHarness.Prior(r1, 1))).Should().BeEmpty("a converged reconcile changes nothing, so it emits no events");

        var put = new PolicyStatement("W", "Allow", ["s3:PutObject"], [$"arn:aws:s3:::bucket-{ns}/*"]);
        var updated = await h.Reconcile(h.Request(ns, "api", put) with { Uid = req.Uid, Generation = 2 }, EngineHarness.Prior(r1, 1));
        updated.Changes!.Single(c => c.Reason == "PolicyUpdated").Drift.Should().BeFalse();

        await h.Iam().DeleteRolePolicyAsync(new DeleteRolePolicyRequest { RoleName = RoleName(r1.RoleArn!), PolicyName = "pio-permissions" });
        var repaired = await h.Reconcile(h.Request(ns, "api", put) with { Uid = req.Uid, Generation = 2 }, EngineHarness.Prior(r1, 2));
        repaired.Changes!.Should().ContainSingle(c => c.Reason == "PolicyRepaired" && c.Drift);
    }

    // ------------------------------------------------------------------ tags

    [Fact]
    public async Task Global_and_request_tags_are_applied_to_the_role_and_the_association()
    {
        var ns = h.NewNamespace();
        var o = h.OptionsWith(x => x.Tags = new() { ["CostCenter"] = "cc-42", ["Team"] = "platform" });
        var req = h.Request(ns) with { Tags = new Dictionary<string, string> { ["App"] = "payments" } };
        var r = await h.Reconcile(req, engine: h.Build(o));
        r.Phase.Should().Be(Phase.Ready, r.Message);

        var role = await h.Role(r.RoleArn!);
        role.Tags.Should().Contain(new KeyValuePair<string, string>("CostCenter", "cc-42")).And.Contain(new KeyValuePair<string, string>("Team", "platform"))
            .And.Contain(new KeyValuePair<string, string>("App", "payments")).And.ContainKey(Tags.Operator);
        var assoc = await h.Association(ns, "api");
        assoc.Tags.Should().Contain(new KeyValuePair<string, string>("CostCenter", "cc-42")).And.Contain(new KeyValuePair<string, string>("App", "payments")).And.ContainKey(Tags.Namespace);
    }

    [Fact]
    public async Task Changing_and_removing_tags_updates_both_resources_and_never_touches_foreign_tags()
    {
        var ns = h.NewNamespace();
        var o = h.OptionsWith(x => x.Tags = new() { ["CostCenter"] = "cc-42" });
        var engine = h.Build(o);
        var r1 = await h.Reconcile(h.Request(ns) with { Generation = 1, Tags = new Dictionary<string, string> { ["App"] = "payments", ["Env"] = "prod" } }, engine: engine);

        // someone else adds a tag in AWS: it must survive
        await h.Iam().TagRoleAsync(new TagRoleRequest { RoleName = RoleName(r1.RoleArn!), Tags = [new() { Key = "added-by-aws-config", Value = "yes" }] });

        var r2 = await h.Reconcile(h.Request(ns) with { Uid = "x", Generation = 2, Tags = new Dictionary<string, string> { ["App"] = "billing" } }, EngineHarness.Prior(r1, 1), engine);
        r2.Phase.Should().Be(Phase.Ready, r2.Message);
        Reasons(r2).Should().Contain(["RoleUpdated", "AssociationUpdated"]);

        foreach (var tags in new[] { (await h.Role(r2.RoleArn!)).Tags, (await h.Association(ns, "api")).Tags })
        {
            tags["App"].Should().Be("billing", "a changed value is updated");
            tags.Should().NotContainKey("Env", "a tag removed from the request is removed from AWS");
            tags["CostCenter"].Should().Be("cc-42");
        }
        (await h.Role(r2.RoleArn!)).Tags.Should().ContainKey("added-by-aws-config", "tags the operator never applied are left alone");
    }

    [Fact]
    public async Task Changing_the_global_tags_is_applied_to_existing_resources()
    {
        var ns = h.NewNamespace();
        var req = h.Request(ns) with { Generation = 1 };
        var r1 = await h.Reconcile(req, engine: h.Build(h.OptionsWith(x => x.Tags = new() { ["CostCenter"] = "old" })));
        var r2 = await h.Reconcile(req, EngineHarness.Prior(r1, 1), h.Build(h.OptionsWith(x => x.Tags = new() { ["CostCenter"] = "new", ["Owner"] = "platform" })));
        r2.Phase.Should().Be(Phase.Ready, r2.Message);
        (await h.Role(r2.RoleArn!)).Tags.Should().Contain(new KeyValuePair<string, string>("CostCenter", "new")).And.Contain(new KeyValuePair<string, string>("Owner", "platform"));
        (await h.Association(ns, "api")).Tags.Should().Contain(new KeyValuePair<string, string>("CostCenter", "new"));
    }

    [Fact]
    public async Task Request_tag_that_overrides_a_global_tag_is_rejected()
    {
        var ns = h.NewNamespace();
        var o = h.OptionsWith(x => x.Tags = new() { ["CostCenter"] = "cc-42" });
        var r = await h.Reconcile(h.Request(ns) with { Tags = new Dictionary<string, string> { ["CostCenter"] = "mine" } }, engine: h.Build(o));
        r.Phase.Should().Be(Phase.Invalid);
        r.Message.Should().Contain("TagConflict");
        (await h.Association(ns, "api")).Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Approved_managed_policy_is_attached_and_removed_when_dropped()
    {
        var ns = h.NewNamespace();
        var pol = await h.Iam().CreatePolicyAsync(new CreatePolicyRequest { PolicyName = "mp-" + ns, PolicyDocument = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":"s3:GetObject","Resource":"*"}]}""" });
        h.Rules = [EngineHarness.DefaultRule() with { AllowedManagedPolicyArns = [pol.Policy.Arn] }];
        try
        {
            var r1 = await h.Reconcile(h.Request(ns) with { Generation = 1, ManagedPolicyArns = [pol.Policy.Arn] });
            r1.Phase.Should().Be(Phase.Ready, r1.Message);
            (await h.Role(r1.RoleArn!)).AttachedPolicyArns.Should().Equal(pol.Policy.Arn);
            var r2 = await h.Reconcile(h.Request(ns) with { Generation = 2 }, EngineHarness.Prior(r1, 1));
            (await h.Role(r2.RoleArn!)).AttachedPolicyArns.Should().BeEmpty();
        }
        finally { h.Rules = [EngineHarness.DefaultRule()]; }
    }
}
