using System.Collections.Concurrent;
using System.Text.Json;
using EksDevopsOperator.Core;
using k8s;
using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

namespace EksDevopsOperator.Kubernetes;

public static class Mapping
{
    public static AccessRequest ToRequest(V1Alpha1PodIdentityAccess e) => ToRequest(e.Metadata, e.Spec);
    public static AccessRequest ToRequest(V1Alpha1IrsaAccess e) => ToRequest(e.Metadata, e.Spec);

    static AccessRequest ToRequest(V1ObjectMeta m, AccessSpec s) => new()
    {
        Namespace = m.NamespaceProperty, ObjectName = m.Name, Uid = m.Uid, Generation = m.Generation ?? 0,
        ServiceAccount = s.ServiceAccountName, RequestedRoleName = s.RoleName, ManagedPolicyArns = s.ManagedPolicies, Tags = s.Tags,
        Statements = s.Statements.Select(x => new PolicyStatement(x.Sid, "Allow", x.Actions, x.Resources)).ToList(),
        DeletionPolicy = ParseDeletion(s.DeletionPolicy), Mechanism = s.BindingMechanism(),
    };

    // an unknown value (the CRD enum prevents it) falls back to the safest policy
    public static DeletionPolicy ParseDeletion(string? s) => Enum.TryParse<DeletionPolicy>(s, out var p) ? p : DeletionPolicy.Retain;
}

/// Gauges for every kind the operator reconciles.
static class Tracker
{
    static readonly ConcurrentDictionary<string, bool> Managed = new(); // uid -> Ready, feeds the gauges
    public static void Track(string uid, ReconcileResult r)
    {
        if (r.Succeeded) Managed[uid] = true; else Managed.TryRemove(uid, out _);
        Telemetry.SetManaged(Managed.Count, Managed.Count);
    }
    public static void Forget(string uid) { Managed.TryRemove(uid, out _); Telemetry.SetManaged(Managed.Count, Managed.Count); }
}

[EntityRbac(typeof(V1Alpha1PodIdentityAccess), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch | RbacVerb.Patch | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1IrsaAccess), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch | RbacVerb.Patch | RbacVerb.Update)]
[EntityRbac(typeof(V1ServiceAccount), Verbs = RbacVerb.Get | RbacVerb.Patch)] // IRSA only: sets or removes the eks.amazonaws.com/role-arn annotation
[EntityRbac(typeof(Corev1Event), Verbs = RbacVerb.Create | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
public sealed class AccessController<TEntity, TSpec>(AccessEngine engine, IKubernetesClient client, EventPublisher events, OperatorOptions opt, ILogger<AccessController<TEntity, TSpec>> log)
    : IEntityController<TEntity>
    where TEntity : CustomKubernetesEntity<TSpec, AccessStatus>, new()
    where TSpec : AccessSpec, new()
{
    static int _failures;
    static AccessRequest ToRequest(TEntity e) => e switch { V1Alpha1PodIdentityAccess p => Mapping.ToRequest(p), V1Alpha1IrsaAccess i => Mapping.ToRequest(i), _ => throw new NotSupportedException(e.Kind) };

    public async Task<ReconciliationResult<TEntity>> ReconcileAsync(TEntity entity, CancellationToken ct)
    {
        using var scope = log.BeginScope(new Dictionary<string, object> { ["uid"] = entity.Uid(), ["namespace"] = entity.Namespace()!, ["name"] = entity.Name() });
        var cur = await client.GetAsync<TEntity>(entity.Name(), entity.Namespace(), ct);
        if (cur is null || cur.Metadata.DeletionTimestamp is not null)
        {
            log.LogDebug("resource is gone or being deleted; nothing to reconcile (the finalizer handles deletion)");
            return ReconciliationResult<TEntity>.Success(entity);
        }
        if (cur.Metadata.Finalizers?.Contains(Api.FinalizerName) != true)
        {
            var op = cur.Metadata.Finalizers is null
                ? JsonSerializer.Serialize(new[] { new { op = "add", path = "/metadata/finalizers", value = (object)new[] { Api.FinalizerName } } })
                : JsonSerializer.Serialize(new[] { new { op = "add", path = "/metadata/finalizers/-", value = (object)Api.FinalizerName } });
            cur = await client.PatchAsync<TEntity>(new V1Patch(op, V1Patch.PatchType.JsonPatch), cur.Name(), cur.Namespace(), null, null, null, ct);
            log.LogInformation("added finalizer {Finalizer} so AWS resources are cleaned up on deletion", Api.FinalizerName);
        }

        if (cur.Status.Conditions.Count == 0 && cur.Status.ObservedGeneration == 0)
        {
            log.LogInformation("new resource: creating {Mechanism} access for ServiceAccount {ServiceAccount}", MechanismNames.Name(cur.Spec.BindingMechanism()), cur.Spec.ServiceAccountName);
            await Note(cur, "Reconciling", $"creating {MechanismNames.Name(cur.Spec.BindingMechanism())} access for ServiceAccount {cur.Spec.ServiceAccountName}", false, ct);
        }
        else if (cur.Status.ObservedGeneration > 0 && cur.Status.ObservedGeneration != (cur.Generation() ?? 0))
        {
            log.LogInformation("spec changed (generation {Old} -> {New}), reconciling", cur.Status.ObservedGeneration, cur.Generation());
            await Note(cur, "SpecChanged", $"spec changed (generation {cur.Status.ObservedGeneration} -> {cur.Generation()}), reconciling", false, ct);
        }

        var req = ToRequest(cur);
        var prior = new PriorState(cur.Status.RoleArn, cur.Status.AssociationId, cur.Status.ObservedGeneration);
        var result = await engine.ReconcileAsync(req, prior,
            intent => Write(cur.Name(), cur.Namespace()!, s => s.RoleArn = intent.RoleArn, ct), ct);

        var changed = cur.Status.Phase != result.Phase || (result.Phase != Phase.Ready && cur.Status.Message != result.Message);
        foreach (var c in result.Changes ?? []) await Note(cur, c.Reason, c.Message, c.Drift, ct); // one event per AWS or cluster change
        await Write(cur.Name(), cur.Namespace()!, s =>
        {
            s.Phase = result.Phase; if (result.Succeeded) s.ObservedGeneration = cur.Generation() ?? 0;
            s.Message = result.Phase == Phase.Ready ? null : result.Message;
            if (result.RoleArn is not null) { s.RoleArn = result.RoleArn; s.AssociationId = result.AssociationId; }
            s.Conditions = result.Conditions.Select(c => new ConditionStatus { Type = c.Type, Status = c.Status ? "True" : "False", Reason = c.Reason, Message = Redactor.Redact(c.Message) }).ToList();
        }, ct);
        if (changed)
        {
            log.LogInformation("phase {Old} -> {New}", string.IsNullOrEmpty(cur.Status.Phase) ? "(new)" : cur.Status.Phase, result.Phase);
            await Emit(cur, req, result, ct);
        }
        Tracker.Track(cur.Uid(), result);

        if (result.Phase == Phase.Ready)
        {
            Interlocked.Exchange(ref _failures, 0);
            log.LogDebug("next drift check in {Delay}", opt.DriftInterval);
            return ReconciliationResult<TEntity>.Success(cur, opt.DriftInterval);
        }
        var delay = result.Phase == Phase.Conflict ? TimeSpan.FromSeconds(60) : Backoff.Delay(result.Error ?? ErrorKind.Transient, Interlocked.Increment(ref _failures));
        log.LogDebug("retrying in {Delay} ({Kind})", delay, result.Error);
        return ReconciliationResult<TEntity>.Success(cur, delay); // never hot-loop on permanent errors
    }

    public Task<ReconciliationResult<TEntity>> DeletedAsync(TEntity entity, CancellationToken ct)
    {
        log.LogDebug("resource {Namespace}/{Name} was removed", entity.Namespace(), entity.Name());
        return Task.FromResult(ReconciliationResult<TEntity>.Success(entity));
    }

    async Task Note(TEntity o, string reason, string message, bool warning, CancellationToken ct)
    {
        try { await events(o, reason, Redactor.Redact(message), warning ? EventType.Warning : EventType.Normal, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogDebug("event {Reason} was not recorded: {Error}", reason, e.Message); } // best effort
    }

    async Task Emit(TEntity o, AccessRequest req, ReconcileResult r, CancellationToken ct)
    {
        var (reason, warn) = r.Phase switch { Phase.Ready => ("Reconciled", false), Phase.Invalid => ("ValidationFailed", true), Phase.Conflict => ("OwnershipConflict", true), _ => ("ReconcileFailed", true) };
        var binding = req.Mechanism == Mechanism.Irsa ? $"the IRSA annotation on ServiceAccount {req.Namespace}/{req.ServiceAccount}" : $"association {r.AssociationId}";
        await Note(o, reason, r.Succeeded ? $"role {r.RoleArn} and {binding} are ready" : r.Message, warn, ct);
    }

    async Task Write(string name, string ns, Action<AccessStatus> mutate, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var fresh = await client.GetAsync<TEntity>(name, ns, ct);
            if (fresh is null) return;
            var before = JsonSerializer.Serialize(fresh.Status);
            mutate(fresh.Status);
            if (JsonSerializer.Serialize(fresh.Status) == before) return; // unchanged: no write, no watch event, no loop
            try { await client.UpdateStatusAsync(fresh, ct); log.LogDebug("status updated"); return; }
            catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict && attempt < 3)
            { log.LogDebug("status update conflicted, retrying ({Attempt})", attempt + 1); }
        }
    }
}

public sealed class AccessFinalizer<TEntity, TSpec>(AccessEngine engine, EventPublisher events, ILogger<AccessFinalizer<TEntity, TSpec>> log) : IEntityFinalizer<TEntity>
    where TEntity : CustomKubernetesEntity<TSpec, AccessStatus>, new()
    where TSpec : AccessSpec, new()
{
    public async Task<ReconciliationResult<TEntity>> FinalizeAsync(TEntity e, CancellationToken ct)
    {
        using var scope = log.BeginScope(new Dictionary<string, object> { ["uid"] = e.Uid(), ["namespace"] = e.Namespace()!, ["name"] = e.Name() });
        var policy = Mapping.ParseDeletion(e.Spec.DeletionPolicy);
        log.LogInformation("resource deleted: cleaning up (deletionPolicy {Policy})", policy);
        await Note(e, "Deleting", $"resource deleted; cleaning up with deletionPolicy {policy}", EventType.Normal, ct);
        var res = await engine.CleanupAsync(new CleanupInput(e.Namespace()!, e.Spec.ServiceAccountName, e.Uid(), e.Spec.RoleName, policy,
            new PriorState(e.Status.RoleArn, e.Status.AssociationId, 0), e.Spec.BindingMechanism()), ct);

        foreach (var r in res.Removed)
            await Note(e, r.StartsWith("association", StringComparison.Ordinal) ? "AssociationDeleted" : r.StartsWith("serviceaccount-annotation", StringComparison.Ordinal) ? "ServiceAccountAnnotationRemoved" : "RoleDeleted", $"deleted {r}", EventType.Normal, ct);
        foreach (var r in res.Retained) await Note(e, "ResourceRetained", $"left in place: {r}", EventType.Normal, ct);
        if (!res.Done)
        {
            await Note(e, "CleanupFailed", res.Message, EventType.Warning, ct);
            var delay = Backoff.Delay(res.Error ?? ErrorKind.Transient, 1);
            log.LogWarning("cleanup did not finish; the finalizer stays and cleanup is retried in {Delay}", delay);
            return ReconciliationResult<TEntity>.Failure(e, res.Message, null, delay);
        }
        await Note(e, "Deleted", $"cleanup complete: {res.Removed.Count} removed, {res.Retained.Count} retained", EventType.Normal, ct);
        Tracker.Forget(e.Uid());
        log.LogInformation("cleanup complete: finalizer released");
        return ReconciliationResult<TEntity>.Success(e);
    }

    async Task Note(TEntity o, string reason, string message, EventType type, CancellationToken ct)
    {
        try { await events(o, reason, Redactor.Redact(message), type, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug("event {Reason} was not recorded: {Error}", reason, ex.Message); } // best effort: the namespace may be terminating
    }
}

/// Oldest request for a (namespace, ServiceAccount) wins, across both kinds (they would share one role name); later ones are reported as conflicts (never merged).
public sealed class CrConflictDetector(IKubernetesClient client) : IConflictDetector
{
    public async Task<IReadOnlyList<string>> FindConflictsAsync(AccessRequest r, CancellationToken ct)
    {
        var all = (await client.ListAsync<V1Alpha1PodIdentityAccess>(r.Namespace, null, null, ct)).Where(c => c.Spec.ServiceAccountName == r.ServiceAccount)
            .Select(c => (c.Metadata.CreationTimestamp, Uid: c.Uid(), Label: $"PodIdentityAccess/{c.Name()}"))
            .Concat((await client.ListAsync<V1Alpha1IrsaAccess>(r.Namespace, null, null, ct)).Where(c => c.Spec.ServiceAccountName == r.ServiceAccount)
                .Select(c => (c.Metadata.CreationTimestamp, Uid: c.Uid(), Label: $"IrsaAccess/{c.Name()}"))).ToList();
        var mine = all.FirstOrDefault(c => c.Uid == r.Uid);
        if (mine.Uid is null) return [];
        return all.Where(c => c.Uid != r.Uid && (c.CreationTimestamp, c.Uid).CompareTo((mine.CreationTimestamp, mine.Uid)) < 0).Select(c => c.Label).ToList();
    }
}
