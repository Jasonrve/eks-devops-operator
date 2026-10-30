using Amazon.EC2;
using Amazon.EKS;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using EksDevopsOperator.Aws;
using EksDevopsOperator.Core;
using EksDevopsOperator.Policy;

namespace EksDevopsOperator.IntegrationTests;

public sealed class StaticPolicySource(Func<IReadOnlyList<PolicyRule>> rules) : IPolicySource
{
    public Task<IReadOnlyList<PolicyRule>> ListAsync(CancellationToken ct) => Task.FromResult(rules());
}

public sealed class StaticConflictDetector : IConflictDetector
{
    public List<string> Conflicts { get; } = [];
    public Task<IReadOnlyList<string>> FindConflictsAsync(AccessRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Conflicts.ToList());
}

/// The real engine and real AWS SDK managers against Floci (one account, one EKS cluster). No AWS mocks.
public sealed class EngineHarness : IAsyncLifetime
{
    public const string A = "111111111111";
    public static readonly string Endpoint = Environment.GetEnvironmentVariable("FLOCI_ENDPOINT") ?? "http://localhost:4566";
    public string Cluster { get; } = "it-" + Guid.NewGuid().ToString("N")[..8];
    public OperatorOptions Options { get; }
    public AwsClients Aws { get; private set; } = null!;
    public AccessEngine Engine { get; private set; } = null!;
    public StaticConflictDetector Conflicts { get; } = new();
    public List<PolicyRule> Rules { get; set; } = [DefaultRule()];

    public EngineHarness()
    {
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", A); // Floci: the key id selects the account; this is the default credential chain
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "test");
        Options = new OperatorOptions { ClusterName = Cluster, Region = "us-east-1", ConsistencyTimeout = TimeSpan.FromSeconds(10) };
    }

    public static PolicyRule DefaultRule() => new()
    {
        Name = "test", AllowedServices = ["s3", "dynamodb", "sqs", "secretsmanager"],
        AllowedDeletionPolicies = [DeletionPolicy.Delete, DeletionPolicy.Retain],
    };

    public AmazonIdentityManagementServiceClient Iam() =>
        new(new BasicAWSCredentials(A, "test"), new AmazonIdentityManagementServiceConfig { ServiceURL = Endpoint, AuthenticationRegion = "us-east-1" });
    public AmazonEKSClient Eks() =>
        new(new BasicAWSCredentials(A, "test"), new AmazonEKSConfig { ServiceURL = Endpoint, AuthenticationRegion = "us-east-1" });

    public async Task InitializeAsync()
    {
        Aws = new AwsClients(new AwsOptions { ServiceUrl = Endpoint, Region = "us-east-1" });
        AwsErrorClassifier.UseCodeLabels();
        Engine = Build(Options);

        try { await Iam().CreatePolicyAsync(new() { PolicyName = "standard-workload", Path = "/eks-devops-operator/", PolicyDocument = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":"*","Resource":"*"}]}""" }); }
        catch (EntityAlreadyExistsException) { }
        var ec2 = new AmazonEC2Client(new BasicAWSCredentials(A, "test"), new AmazonEC2Config { ServiceURL = Endpoint, AuthenticationRegion = "us-east-1" });
        var vpc = (await ec2.CreateVpcAsync(new() { CidrBlock = "10.0.0.0/16" })).Vpc.VpcId;
        var subnet = (await ec2.CreateSubnetAsync(new() { VpcId = vpc, CidrBlock = "10.0.1.0/24" })).Subnet.SubnetId;
        await Eks().CreateClusterAsync(new() { Name = Cluster, RoleArn = $"arn:aws:iam::{A}:role/eks", ResourcesVpcConfig = new() { SubnetIds = [subnet] } });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// A second engine with different options (for example different global tags), sharing the same AWS state.
    public AccessEngine Build(OperatorOptions o) => new(o, Aws, new StaticPolicySource(() => Rules), new PolicyValidator(o), Conflicts, new ResourceOwnershipService(o),
        new ReconciliationPlanner(), new IamRoleManager(Aws), new IamPolicyManager(Aws), new BindingResolver([new EksPodIdentityManager(Aws)]), new OidcResolver(Aws, o, NullLogger<OidcResolver>.Instance), new AwsErrorClassifier(), TimeProvider.System, NullLogger<AccessEngine>.Instance);

    public OperatorOptions OptionsWith(Action<OperatorOptions> change)
    {
        var o = new OperatorOptions { ClusterName = Options.ClusterName, Region = Options.Region, ConsistencyTimeout = Options.ConsistencyTimeout };
        change(o);
        return o;
    }

    public string NewNamespace() => "ns-" + Guid.NewGuid().ToString("N")[..8];

    public AccessRequest Request(string ns, string sa = "api", params PolicyStatement[] statements) => new()
    {
        Namespace = ns, ObjectName = "req", Uid = Guid.NewGuid().ToString(), ServiceAccount = sa,
        Statements = statements.Length > 0 ? statements : [S3Read("bucket-" + ns)],
    };

    public static PolicyStatement S3Read(string bucket) => new("Read", "Allow", ["s3:GetObject", "s3:ListBucket"], [$"arn:aws:s3:::{bucket}", $"arn:aws:s3:::{bucket}/*"]);

    public Task<ReconcileResult> Reconcile(AccessRequest r, PriorState? prior = null, AccessEngine? engine = null) =>
        (engine ?? Engine).ReconcileAsync(r, prior ?? new PriorState(null, null), null, CancellationToken.None);

    public static PriorState Prior(ReconcileResult r, long generation = 0) => new(r.RoleArn, r.AssociationId, generation);

    public Task<ObservedRole> Role(string arn) => new IamRoleManager(Aws).ReadAsync(arn[(arn.LastIndexOf('/') + 1)..], CancellationToken.None);

    public Task<ObservedAssociation> Association(string ns, string sa) => new EksPodIdentityManager(Aws).ReadAsync(Cluster, ns, sa, CancellationToken.None);
}
