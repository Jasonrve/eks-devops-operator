using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using EksDevopsOperator.Core;

namespace EksDevopsOperator.Kubernetes;

/// Reads PodIdentityPolicy objects from the operator's own namespace only (never from team namespaces).
public sealed class PolicyCrSource(IKubernetesClient client, OperatorOptions opt, TimeProvider time, ILogger<PolicyCrSource> log) : IPolicySource
{
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    (long At, IReadOnlyList<PolicyRule> Rules)? _cache;
    readonly SemaphoreSlim _gate = new(1, 1);
    string? _lastNames;

    public async Task<IReadOnlyList<PolicyRule>> ListAsync(CancellationToken ct)
    {
        if (_cache is { } c && time.GetElapsedTime(c.At) < Ttl) return c.Rules;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } c2 && time.GetElapsedTime(c2.At) < Ttl) return c2.Rules;
            var items = await client.ListAsync<V1Alpha1PodIdentityPolicy>(opt.PolicyNamespace, null, null, ct);
            var rules = items
                .Where(p => p.Namespace() == opt.PolicyNamespace && p.Metadata.DeletionTimestamp is null) // defense in depth: ignore anything from another namespace
                .Select(ToRule).ToList();
            _cache = (time.GetTimestamp(), rules);
            var names = string.Join(", ", rules.Select(r => r.Name).Order());
            if (names != _lastNames) // only when the set of policies changes, so the log is not repeated every few seconds
            {
                _lastNames = names;
                if (rules.Count == 0) log.LogWarning("no PodIdentityPolicy objects in namespace {Namespace}: every request is rejected until one is created there", opt.PolicyNamespace);
                else log.LogInformation("loaded {Count} PodIdentityPolicy object(s) from namespace {Namespace}: {Names}", rules.Count, opt.PolicyNamespace, names);
            }
            return rules;
        }
        finally { _gate.Release(); }
    }

    public static PolicyRule ToRule(V1Alpha1PodIdentityPolicy p) => new()
    {
        Name = p.Name(), Namespaces = p.Spec.Namespaces, AllowedMechanisms = p.Spec.AllowedMechanisms, AllowedServices = p.Spec.AllowedServices,
        AllowedActions = p.Spec.AllowedActions, DeniedActions = p.Spec.DeniedActions, AllowedResourceArnPatterns = p.Spec.AllowedResourceArnPatterns,
        AllowedManagedPolicyArns = p.Spec.AllowedManagedPolicyArns, RequirePermissionsBoundary = p.Spec.RequirePermissionsBoundary,
        AllowWildcardActions = p.Spec.AllowWildcardActions, AllowWildcardResources = p.Spec.AllowWildcardResources,
        AllowPrivilegeEscalationActions = p.Spec.AllowPrivilegeEscalationActions,
        AllowedDeletionPolicies = p.Spec.AllowedDeletionPolicies.Select(x => Enum.TryParse<DeletionPolicy>(x, true, out var d) ? d : DeletionPolicy.Delete).Distinct().ToList(),
    };
}
