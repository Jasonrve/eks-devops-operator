using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace EksDevopsOperator.Core;

/// Metric and trace names. Labels are deliberately low-cardinality (kind, outcome, operation, service, error kind).
public static class Telemetry
{
    public const string Name = "EksDevopsOperator";
    public static readonly Meter Meter = new(Name);
    public static readonly ActivitySource Activity = new(Name);

    public static readonly Counter<long> ReconcileTotal = Meter.CreateCounter<long>("pod_identity_reconcile_total", description: "Reconciles by outcome (Ready, Failed, Invalid, Conflict).");
    public static readonly Histogram<double> ReconcileDuration = Meter.CreateHistogram<double>("pod_identity_reconcile_duration_seconds", "s", "Time taken by one reconcile.");
    public static readonly Counter<long> ReconcileErrors = Meter.CreateCounter<long>("pod_identity_reconcile_errors_total", description: "Failed reconciles by error kind (Transient, Permanent, Authorization, Conflict).");
    public static readonly Counter<long> ValidationFailures = Meter.CreateCounter<long>("pod_identity_policy_validation_failures_total", description: "Requests rejected by the guardrails, by validation code.");
    public static readonly Counter<long> AwsRequests = Meter.CreateCounter<long>("pod_identity_aws_api_requests_total", description: "AWS API calls by service and operation.");
    public static readonly Counter<long> AwsErrors = Meter.CreateCounter<long>("pod_identity_aws_api_errors_total", description: "AWS API errors by service, operation and AWS error code (does-not-exist answers are not counted).");
    public static readonly Counter<long> DriftDetected = Meter.CreateCounter<long>("pod_identity_drift_detected_total", description: "Times AWS state differed from the desired state and was repaired.");
    public static readonly Counter<long> CleanupFailures = Meter.CreateCounter<long>("pod_identity_cleanup_failures_total", description: "Failed cleanups when a PodIdentityAccess was deleted.");

    static long _roles, _associations;
    public static readonly ObservableGauge<long> ManagedRoles = Meter.CreateObservableGauge("pod_identity_managed_roles", () => Interlocked.Read(ref _roles), description: "IAM roles of resources that are Ready.");
    public static readonly ObservableGauge<long> ManagedAssociations = Meter.CreateObservableGauge("pod_identity_managed_associations", () => Interlocked.Read(ref _associations), description: "Pod Identity associations of resources that are Ready.");
    public static void SetManaged(long roles, long associations) { Interlocked.Exchange(ref _roles, roles); Interlocked.Exchange(ref _associations, associations); }

    static readonly HashSet<string> ProbeCodes = ["NoSuchEntity", "ResourceNotFoundException"];

    /// Maps an exception to a bounded label value; the AWS layer replaces this with the SDK error code.
    public static Func<Exception, string> ErrorLabel { get; set; } = e => e.GetType().Name;

    /// Counts and times one AWS API call; the error counter is labelled with the exception's error code only.
    /// Set at startup. AWS calls are logged at Debug (the engine logs the outcome that matters at Information or Warning).
    public static Microsoft.Extensions.Logging.ILogger? Logger { get; set; }

    public static async Task<T> AwsCall<T>(string service, string operation, Func<Task<T>> call)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        AwsRequests.Add(1, new KeyValuePair<string, object?>("service", service), new KeyValuePair<string, object?>("operation", operation));
        using var act = Activity.StartActivity($"aws.{service}.{operation}");
        try
        {
            var result = await call();
            Logger?.LogDebug("aws {Service}.{Operation} ok in {Ms} ms", service, operation, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (Exception e)
        {
            Logger?.LogDebug("aws {Service}.{Operation} failed in {Ms} ms: {Error}", service, operation, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, ErrorLabel(e));
            // "does not exist" answers are how the operator probes state; counting them would make normal operation look like errors
            if (ProbeCodes.Contains(ErrorLabel(e))) throw;
            AwsErrors.Add(1, new KeyValuePair<string, object?>("service", service), new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("code", ErrorLabel(e)));
            throw;
        }
    }
}
