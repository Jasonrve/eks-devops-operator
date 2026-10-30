using Amazon;
using Amazon.EKS;
using Amazon.IdentityManagement;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using EksDevopsOperator.Core;

namespace EksDevopsOperator.Aws;

public sealed class AwsOptions
{
    /// Overrides the endpoint for every AWS client (Floci locally). Empty in production.
    public string? ServiceUrl { get; set; }
    public string Region { get; set; } = "us-east-1";
    /// Test seam (fault injection): custom HTTP client factory applied to every client.
    public HttpClientFactory? HttpClientFactory { get; set; }
}

/// The AWS clients, created with the SDK default credential chain: EKS Pod Identity in a cluster, environment or profile locally.
/// No credentials are ever configured or stored by the operator.
public sealed class AwsClients : IAwsAccountResolver
{
    string? _account;
    readonly string _region;
    readonly Microsoft.Extensions.Logging.ILogger? _log;

    public AwsClients(AwsOptions o, Microsoft.Extensions.Logging.ILogger<AwsClients>? log = null)
    {
        _region = o.Region;
        _log = log;
        Iam = new AmazonIdentityManagementServiceClient(Configure(new AmazonIdentityManagementServiceConfig(), o));
        Eks = new AmazonEKSClient(Configure(new AmazonEKSConfig(), o));
        Sts = new AmazonSecurityTokenServiceClient(Configure(new AmazonSecurityTokenServiceConfig(), o));
    }

    public IAmazonIdentityManagementService Iam { get; }
    public IAmazonEKS Eks { get; }
    public IAmazonSecurityTokenService Sts { get; }

    static T Configure<T>(T cfg, AwsOptions o) where T : ClientConfig
    {
        if (!string.IsNullOrEmpty(o.ServiceUrl)) cfg.ServiceURL = o.ServiceUrl;
        else cfg.RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region);
        cfg.AuthenticationRegion = o.Region;
        if (o.HttpClientFactory is not null) cfg.HttpClientFactory = o.HttpClientFactory;
        return cfg;
    }

    public async Task<string> GetClusterAccountIdAsync(CancellationToken ct)
    {
        if (_account is not null) return _account;
        _account = (await Telemetry.AwsCall("sts", "GetCallerIdentity", () => Sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct))).Account;
        if (_log is not null) Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(_log, "running in AWS account {Account} (region {Region})", _account, _region);
        return _account;
    }
}
