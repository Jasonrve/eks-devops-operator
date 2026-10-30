using Amazon.IdentityManagement.Model;
using EksDevopsOperator.Core;
using Microsoft.Extensions.Logging;

namespace EksDevopsOperator.Aws;

/// Finds the cluster's OIDC issuer (configured, or read from the cluster with eks:DescribeCluster) and checks that the IAM OIDC provider
/// for it exists. The provider is a one-time, per-cluster setup that the platform team does; the operator never creates it.
public sealed class OidcResolver(AwsClients aws, OperatorOptions opt, ILogger<OidcResolver> log) : IOidcResolver
{
    string? _issuer;

    public async Task<OidcInfo> ResolveAsync(ClusterContext c, CancellationToken ct)
    {
        _issuer ??= Normalize(opt.OidcIssuer) ?? await DiscoverAsync(c, ct);
        var arn = $"arn:{c.Partition}:iam::{c.AccountId}:oidc-provider/{_issuer}";
        try
        {
            await Telemetry.AwsCall("iam", "GetOpenIDConnectProvider", () => aws.Iam.GetOpenIDConnectProviderAsync(new GetOpenIDConnectProviderRequest { OpenIDConnectProviderArn = arn }, ct));
        }
        catch (NoSuchEntityException)
        {
            throw new PodIdentityException(ErrorKind.Permanent, "OidcProviderMissing",
                $"the IAM OIDC provider {arn} does not exist. Create it once per cluster, for example `eksctl utils associate-iam-oidc-provider --cluster {c.ClusterName} --approve` or the Terraform resource aws_iam_openid_connect_provider");
        }
        return new OidcInfo(_issuer, arn);
    }

    async Task<string> DiscoverAsync(ClusterContext c, CancellationToken ct)
    {
        var cluster = (await Telemetry.AwsCall("eks", "DescribeCluster", () => aws.Eks.DescribeClusterAsync(new Amazon.EKS.Model.DescribeClusterRequest { Name = c.ClusterName }, ct))).Cluster;
        var issuer = Normalize(cluster?.Identity?.Oidc?.Issuer)
            ?? throw new PodIdentityException(ErrorKind.Permanent, "OidcIssuerUnknown",
                $"cluster {c.ClusterName} reports no OIDC issuer. Enable it, or set the issuer explicitly (Helm value irsa.oidcIssuer)");
        log.LogInformation("discovered the cluster OIDC issuer {Issuer}", issuer);
        return issuer;
    }

    static string? Normalize(string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer)) return null;
        issuer = issuer.Trim();
        foreach (var scheme in new[] { "https://", "http://" }) if (issuer.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) issuer = issuer[scheme.Length..];
        return issuer.TrimEnd('/');
    }
}
