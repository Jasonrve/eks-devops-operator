using System.Runtime.CompilerServices;
using EksDevopsOperator.Aws;
using EksDevopsOperator.Core;
using EksDevopsOperator.Http;
using EksDevopsOperator.Kubernetes;
using EksDevopsOperator.Policy;
using k8s.Models;
using KubeOps.Abstractions.Builder;
using KubeOps.Abstractions.Events;
using KubeOps.KubernetesClient;
using KubeOps.Operator;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// `EksDevopsOperator generate-crds <dir>` renders the CRDs from the entity classes (used by CI drift checks and when the API changes).
if (args is ["generate-crds", var crdDir])
{
    Directory.CreateDirectory(crdDir);
    var types = new[] { typeof(V1Alpha1PodIdentityAccess), typeof(V1Alpha1IrsaAccess), typeof(V1Alpha1PodIdentityPolicy) };
    foreach (var crd in new KubeOps.Operator.Crds.KubeOpsCrdResourceFactory().CreateCustomResourceDefinitions(types))
        File.WriteAllText(Path.Combine(crdDir, $"{crd.Spec.Group}_{crd.Spec.Names.Plural}.yaml"), k8s.KubernetesYaml.Serialize(crd));
    return;
}

// A plain generic host: no web framework. Probes and metrics are served by ProbeServer (a small HttpListener).
// The content root is the app's own folder, so appsettings.json is found no matter which directory the process starts in.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });

// ---- configuration: "Operator" (behaviour), "Aws" (endpoint/region). Cluster identity comes from trusted operator config only.
// The Helm chart mounts operator.json (global tags: their keys may not be valid env var names).
builder.Configuration.AddJsonFile(Environment.GetEnvironmentVariable("OPERATOR_CONFIG_FILE") ?? "/config/operator.json", optional: true, reloadOnChange: false);
var opt = builder.Configuration.GetSection("Operator").Get<OperatorOptions>() ?? new OperatorOptions();
if (string.IsNullOrWhiteSpace(opt.ClusterName)) throw new InvalidOperationException("Operator:ClusterName (env Operator__ClusterName) is required");
// PodIdentityPolicy objects are read only from this namespace: the operator's own (POD_NAMESPACE via the downward API, or the mounted service account namespace).
if (string.IsNullOrWhiteSpace(opt.PolicyNamespace))
    opt.PolicyNamespace = Environment.GetEnvironmentVariable("POD_NAMESPACE")
        ?? (File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/namespace") ? File.ReadAllText("/var/run/secrets/kubernetes.io/serviceaccount/namespace").Trim() : "");
if (string.IsNullOrWhiteSpace(opt.PolicyNamespace)) throw new InvalidOperationException("Operator:PolicyNamespace (env Operator__PolicyNamespace or POD_NAMESPACE) is required");
// global tags are validated at startup so a bad configuration fails fast instead of failing every reconcile
var tagErrors = TagRules.Validate(opt.Tags, new Dictionary<string, string>(), isGlobal: true);
if (tagErrors.Count > 0) throw new InvalidOperationException("Invalid global tags (Operator:Tags): " + string.Join("; ", tagErrors.Select(e => e.Message)));
var awsOpt = builder.Configuration.GetSection("Aws").Get<AwsOptions>() ?? new AwsOptions();
if (string.IsNullOrWhiteSpace(awsOpt.Region)) awsOpt.Region = opt.Region;
awsOpt.Region = string.IsNullOrWhiteSpace(builder.Configuration["Aws:Region"]) ? opt.Region : awsOpt.Region;
opt.Region = awsOpt.Region;
var leaderElection = builder.Configuration.GetValue("Operator:LeaderElection", true);
var maxParallel = builder.Configuration.GetValue("Operator:MaxConcurrentReconciles", 4);
var watchNamespace = builder.Configuration["Operator:WatchNamespace"];

// ---- logging: structured JSON, level from Logging:LogLevel (secrets are redacted at the points where messages are produced)
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });
builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(30)); // graceful shutdown: finish in-flight reconciles

// ---- metrics: created before anything can record, so no early measurement is lost
RuntimeHelpers.RunClassConstructor(typeof(Telemetry).TypeHandle); // creates the instruments (gauges included) even before the first reconcile
var metrics = new MetricsRegistry(Telemetry.Name, new Dictionary<string, double[]>
{
    ["pod_identity_reconcile_duration_seconds"] = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60],
});

// ---- DI: one engine, one controller
AwsErrorClassifier.UseCodeLabels();
builder.Services.AddSingleton(opt);
builder.Services.AddSingleton(awsOpt);
builder.Services.AddSingleton(metrics);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AwsClients>();
builder.Services.AddSingleton<IAwsAccountResolver>(sp => sp.GetRequiredService<AwsClients>());
builder.Services.AddSingleton<IIamRoleManager, IamRoleManager>();
builder.Services.AddSingleton<IIamPolicyManager, IamPolicyManager>();
builder.Services.AddSingleton<IBindingManager, EksPodIdentityManager>(); // Pod Identity associations
builder.Services.AddSingleton<IBindingManager, IrsaBindingManager>();      // IRSA: the role-arn annotation on the ServiceAccount
builder.Services.AddSingleton<IBindingResolver, BindingResolver>();
builder.Services.AddSingleton<IOidcResolver, OidcResolver>();
builder.Services.AddSingleton<IErrorClassifier, AwsErrorClassifier>();
builder.Services.AddSingleton<IPolicyValidator, PolicyValidator>();
builder.Services.AddSingleton<IResourceOwnershipService, ResourceOwnershipService>();
builder.Services.AddSingleton<IReconciliationPlanner, ReconciliationPlanner>();
builder.Services.AddSingleton<IPolicySource, PolicyCrSource>();
builder.Services.AddSingleton<IConflictDetector, CrConflictDetector>();
builder.Services.AddSingleton<AccessEngine>();
if (opt.ServiceAccountAnnotations)
    builder.Services.AddHostedService(sp => new ServiceAccountSync(sp.GetRequiredService<IKubernetesClient>(), sp.GetRequiredService<EventPublisher>(), opt, watchNamespace, sp.GetRequiredService<ILogger<ServiceAccountSync>>()));
builder.Services.AddHostedService<ProbeServer>(); // registered first, so probes answer while the controller is still starting

builder.Services.AddKubernetesOperator(s =>
    {
        s.Name = "eks-devops-operator";
        s.AutoAttachFinalizers = false; // the controller attaches its finalizer itself
        s.ReconcileStrategy = ReconcileStrategy.ByGeneration;
        s.LeaderElectionType = leaderElection ? LeaderElectionType.Single : LeaderElectionType.None;
        s.Namespace = string.IsNullOrWhiteSpace(watchNamespace) ? null : watchNamespace;
        s.ParallelReconciliation = new ParallelReconciliationSettingsBuilder { MaxParallelReconciliations = maxParallel };
    })
    .AddController<AccessController<V1Alpha1PodIdentityAccess, PodIdentityAccessSpec>, V1Alpha1PodIdentityAccess>()
    .AddFinalizer<AccessFinalizer<V1Alpha1PodIdentityAccess, PodIdentityAccessSpec>, V1Alpha1PodIdentityAccess>(Api.FinalizerName)
    .AddController<AccessController<V1Alpha1IrsaAccess, IrsaAccessSpec>, V1Alpha1IrsaAccess>()
    .AddFinalizer<AccessFinalizer<V1Alpha1IrsaAccess, IrsaAccessSpec>, V1Alpha1IrsaAccess>(Api.FinalizerName);

// ---- traces: OpenTelemetry, exported over OTLP only when an endpoint is configured
var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("eks-devops-operator").AddAttributes([new("k8s.cluster.name", opt.ClusterName)]))
    .WithTracing(t => { t.AddSource(Telemetry.Name); if (!string.IsNullOrEmpty(otlp)) t.AddOtlpExporter(); });

var host = builder.Build();
var loggers = host.Services.GetRequiredService<ILoggerFactory>();
Telemetry.Logger = loggers.CreateLogger("EksDevopsOperator.Aws");
var startup = loggers.CreateLogger("EksDevopsOperator");
startup.LogInformation(
    "starting {Version}: cluster {Cluster} ({Partition}/{Region}), environment {Environment}, policy namespace {PolicyNamespace}, leader election {LeaderElection}, " +
    "up to {Parallel} parallel reconcile(s), drift check every {Drift}, {TagCount} global tag(s), IRSA {Irsa}",
    typeof(AccessEngine).Assembly.GetName().Version, opt.ClusterName, opt.Partition, opt.Region, opt.Environment, opt.PolicyNamespace,
    leaderElection ? "on" : "off", maxParallel, opt.DriftInterval, opt.Tags.Count, opt.IrsaEnabled ? "enabled" : "disabled");
if (opt.Tags.Count > 0) startup.LogInformation("global tag keys: {Keys}", string.Join(", ", opt.Tags.Keys.Order()));
if (!string.IsNullOrEmpty(awsOpt.ServiceUrl)) startup.LogWarning("AWS endpoint overridden to {Url}: use this only with an emulator, never in production", awsOpt.ServiceUrl);
await host.RunAsync();
startup.LogInformation("stopped");
