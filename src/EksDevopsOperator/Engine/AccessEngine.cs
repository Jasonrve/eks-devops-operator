using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EksDevopsOperator.Core;

public static class Phase
{
    public const string Ready = "Ready", Pending = "Pending", Failed = "Failed", Invalid = "Invalid", Conflict = "Conflict";
}

public sealed record Condition(string Type, bool Status, string Reason, string Message);

/// One AWS or cluster change the operator made. Becomes a Kubernetes Event (Warning when it repaired drift).
public sealed record Change(string Reason, string Message, bool Drift = false);

public sealed record ReconcileResult(
    string Phase, IReadOnlyList<Condition> Conditions, string? RoleArn = null, string? AssociationId = null,
    ErrorKind? Error = null, string Message = "", IReadOnlyList<string>? Drift = null, IReadOnlyList<string>? Conflicts = null,
    IReadOnlyList<Change>? Changes = null)
{
    public bool Succeeded => Phase == Core.Phase.Ready;
}

public sealed record CleanupResult(bool Done, IReadOnlyList<string> Retained, IReadOnlyList<string> Removed, ErrorKind? Error = null, string Message = "");

public sealed record CleanupInput(string Namespace, string ServiceAccount, string Uid, string? RequestedRoleName, DeletionPolicy Policy, PriorState Prior,
    Mechanism Mechanism = Mechanism.PodIdentity);

/// The reconciliation pipeline: validate -> detect conflicts -> desired state -> observe AWS -> plan -> execute -> verify by reading back.
/// Every step is idempotent, so a crash at any point simply re-plans from real AWS state on the next reconcile.
///
/// Logging: Information for every change made and for outcomes worth a human's attention, Warning for rejections, conflicts, refusals
/// and failures, Debug for everything routine (reads, plans, "already in sync"). Each line carries uid, namespace, name, ServiceAccount and mechanism.
public sealed class AccessEngine(
    OperatorOptions opt, IAwsAccountResolver account, IPolicySource policySource, IPolicyValidator validator, IConflictDetector conflicts,
    IResourceOwnershipService owners, IReconciliationPlanner planner, IIamRoleManager roles, IIamPolicyManager policies,
    IBindingResolver bindings, IOidcResolver oidc, IErrorClassifier classifier, TimeProvider time, ILogger<AccessEngine> log)
{
    const int MaxInlineChars = 9500; // IAM allows 10,240 non-whitespace characters of inline policy per role

    static string BindingWord(Mechanism m) => m == Mechanism.Irsa ? "ServiceAccountAnnotation" : "Association";
    static string BindingText(Mechanism m, string id, string ns, string sa) =>
        m == Mechanism.Irsa ? $"IRSA annotation on ServiceAccount {ns}/{sa}" : $"Pod Identity association {id} for {ns}/{sa}";
    static string BindingCondition(Mechanism m) => m == Mechanism.Irsa ? "ServiceAccountReady" : "AssociationReady";

    IDisposable? Scope(string uid, string ns, string name, string sa, Mechanism m) => log.BeginScope(new Dictionary<string, object>
    {
        ["uid"] = uid, ["namespace"] = ns, ["name"] = name, ["serviceAccount"] = sa, ["mechanism"] = MechanismNames.Name(m),
        ["attempt"] = Guid.NewGuid().ToString("N")[..8], ["cluster"] = opt.ClusterName,
    });

    public async Task<ReconcileResult> ReconcileAsync(AccessRequest req, PriorState prior, Func<PriorState, Task>? recordIntent, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var activity = Telemetry.Activity.StartActivity("reconcile");
        activity?.SetTag("k8s.object.uid", req.Uid);
        activity?.SetTag("k8s.namespace", req.Namespace);
        using var _ = Scope(req.Uid, req.Namespace, req.ObjectName, req.ServiceAccount, req.Mechanism);
        log.LogDebug("reconcile started (generation {Generation}, last reconciled generation {Observed})", req.Generation, prior.ObservedGeneration);
        ReconcileResult result;
        try { result = await CoreAsync(req, prior, recordIntent, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { log.LogDebug("reconcile cancelled (shutting down)"); throw; }
        catch (Exception e)
        {
            var kind = classifier.Classify(e);
            var safe = Redactor.Redact(e.Message);
            log.LogDebug(e, "reconcile failure details ({Type})", e.GetType().Name); // full exception only at Debug; the Warning below carries the message
            var reason = e is PodIdentityException p ? p.Reason : kind.ToString();
            result = new ReconcileResult(Phase.Failed, [new("Ready", false, reason, safe)], Error: kind, Message: safe);
        }
        var elapsed = sw.Elapsed;
        switch (result.Phase)
        {
            case Phase.Ready when result.Changes is { Count: > 0 }:
                log.LogInformation("reconciled in {Ms} ms with {Count} change(s): role {RoleArn}, binding {BindingId}", (long)elapsed.TotalMilliseconds, result.Changes.Count, result.RoleArn, result.AssociationId);
                break;
            case Phase.Ready: log.LogDebug("in sync, nothing to do ({Ms} ms)", (long)elapsed.TotalMilliseconds); break;
            case Phase.Invalid: log.LogWarning("request rejected by the guardrails: {Message}", result.Message); break;
            case Phase.Conflict: log.LogWarning("ownership conflict: {Message}", result.Message); break;
            default: log.LogWarning("reconcile failed ({Kind}): {Message}", result.Error, result.Message); break;
        }
        Telemetry.ReconcileTotal.Add(1, new KeyValuePair<string, object?>("outcome", result.Phase));
        Telemetry.ReconcileDuration.Record(elapsed.TotalSeconds);
        if (result.Error is { } k) Telemetry.ReconcileErrors.Add(1, new KeyValuePair<string, object?>("kind", k.ToString()));
        return result;
    }

    void LogChange(Change c)
    {
        if (c.Drift) log.LogWarning("{Reason}: {Message}", c.Reason, c.Message);
        else log.LogInformation("{Reason}: {Message}", c.Reason, c.Message);
    }

    async Task<ReconcileResult> CoreAsync(AccessRequest req, PriorState prior, Func<PriorState, Task>? recordIntent, CancellationToken ct)
    {
        var clusterAccount = await account.GetClusterAccountIdAsync(ct);
        var cluster = new ClusterContext(opt.ClusterName, clusterAccount, opt.Partition, opt.Region, opt.Environment);
        var matching = (await policySource.ListAsync(ct)).Where(p => p.Governs(req.Namespace)).ToList();
        log.LogDebug("{Count} PodIdentityPolicy object(s) govern namespace {Namespace}", matching.Count, req.Namespace);

        // 1. validation: nothing below this line runs (and nothing in AWS changes) for an invalid request
        var v = validator.Validate(req, matching, cluster);
        if (!v.IsValid)
        {
            foreach (var code in v.Issues.Select(i => i.Code).Distinct()) Telemetry.ValidationFailures.Add(1, new KeyValuePair<string, object?>("code", code));
            return new ReconcileResult(Phase.Invalid, [new("Validated", false, v.Issues[0].Code, v.Summary), new("Ready", false, "ValidationFailed", v.Summary)],
                Error: ErrorKind.Permanent, Message: v.Summary);
        }
        var validated = new Condition("Validated", true, "PolicyAccepted", "request satisfies every PodIdentityPolicy that governs the namespace");

        // 2. ownership arbitration for the (namespace, ServiceAccount) pair
        var clash = await conflicts.FindConflictsAsync(req, ct);
        if (clash.Count > 0)
        {
            var msg = $"ServiceAccount {req.Namespace}/{req.ServiceAccount} is also claimed by: {string.Join(", ", clash)}";
            return new ReconcileResult(Phase.Conflict, [validated, new("Ready", false, "OwnershipConflict", msg)], Error: ErrorKind.Conflict, Message: msg, Conflicts: clash);
        }

        // 3. desired state (IRSA roles trust the cluster's OIDC provider, which must exist: that is checked here, before anything is created)
        var oidcInfo = req.Mechanism == Mechanism.Irsa ? await oidc.ResolveAsync(cluster, ct) : null;
        var d = BuildDesired(req, matching, cluster, oidcInfo, out var invalid);
        if (invalid is not null)
            return new ReconcileResult(Phase.Invalid, [new("Validated", false, "PolicyTooLarge", invalid), new("Ready", false, "ValidationFailed", invalid)], Error: ErrorKind.Permanent, Message: invalid);
        var binding = bindings.For(req.Mechanism);

        // 4. observe (read-only) and refuse to touch anything this request does not own
        var (state, refusal) = await ObserveAsync(req, cluster, d, prior, ct);
        if (refusal is not null)
            return new ReconcileResult(Phase.Failed, [validated, new("Ready", false, refusal.Value.reason, refusal.Value.msg)], Error: refusal.Value.kind, Message: refusal.Value.msg);

        var plan = planner.Plan(d, state);
        var specChanged = req.Generation != prior.ObservedGeneration; // a requested change is an update; the same spec with different AWS state is drift
        if (specChanged) plan = plan with { Drift = [] };
        if (plan.Converged) return Ready(validated, d, state.Association.Id!, plan);
        foreach (var _d in plan.Drift) Telemetry.DriftDetected.Add(1);
        log.LogDebug("plan ({Kind}): {Steps}", specChanged ? "spec changed" : "drift or first run", string.Join("; ", plan.Steps.Select(s => $"{s.Kind} [{s.Reason}]")));

        // 5. record intent so crash recovery can find the role even if the status write after the AWS calls fails
        if (recordIntent is not null && d.Role.Arn != prior.RoleArn) await recordIntent(new PriorState(d.Role.Arn, prior.AssociationId, prior.ObservedGeneration));

        // 6. execute in order
        var associationId = state.Association.Id;
        var changes = new List<Change>();
        void Record(Change c) { changes.Add(c); LogChange(c); }
        foreach (var step in plan.Steps)
        {
            var fresh = step.Reason is "role missing" or "binding missing" or "inline policy for new role" or "managed policies for new role";
            // a role or binding that vanished after we created it is drift, not creation (and so is the policy that has to be put back on it)
            var drift = plan.Drift.Any(x => x.StartsWith(step.Kind + ":")) || (!specChanged && prior.RoleArn is not null && fresh);
            switch (step.Kind)
            {
                case StepKind.EnsureRole:
                    await roles.EnsureRoleAsync(d.Role, state.Role, ct);
                    Record(Describe(fresh, drift, "Role", $"role {d.Role.Arn}", step.Reason));
                    break;
                case StepKind.EnsureInlinePolicy:
                    await policies.EnsureInlinePolicyAsync(d.Role.Name, d.Role.InlinePolicyName, d.Role.InlinePolicy, ct);
                    Record(Describe(fresh, drift, "Policy", $"permissions policy on {d.Role.Name}", step.Reason));
                    break;
                case StepKind.EnsureManagedPolicies:
                    await policies.EnsureAttachedPoliciesAsync(d.Role.Name, d.Role.ManagedPolicyArns, ct);
                    if (!fresh || d.Role.ManagedPolicyArns.Count > 0) Record(Describe(fresh, drift, "ManagedPolicies", $"managed policies on {d.Role.Name}", step.Reason));
                    break;
                case StepKind.EnsureBinding:
                    associationId = await binding.EnsureAsync(cluster.ClusterName, req.Namespace, req.ServiceAccount, d.Role.Arn, d.Role.Tags, ct);
                    Record(Describe(fresh, drift, BindingWord(req.Mechanism), BindingText(req.Mechanism, associationId, req.Namespace, req.ServiceAccount), step.Reason));
                    break;
                case StepKind.DeletePreviousRole:
                    var prevName = PreviousRoleName(state, prior, d)!;
                    await roles.DeleteRoleAsync(prevName, ct); // ownership was verified in ObserveAsync
                    Record(new Change("PreviousRoleDeleted", $"deleted previous role {prevName} ({step.Reason})"));
                    break;
            }
        }

        // 7. verify against AWS read APIs (eventual consistency: poll until converged or time out)
        var started = time.GetTimestamp();
        while (true)
        {
            var (again, _) = await ObserveAsync(req, cluster, d, prior with { RoleArn = d.Role.Arn }, ct);
            var left = planner.Plan(d, again);
            if (left.Converged) return Ready(validated, d, again.Association.Id ?? associationId!, plan, changes);
            if (time.GetElapsedTime(started) > opt.ConsistencyTimeout)
                throw new PodIdentityException(ErrorKind.Transient, "NotConverged", $"AWS state did not converge: {string.Join("; ", left.Steps.Select(s => $"{s.Kind} ({s.Reason})"))}");
            log.LogDebug("waiting for AWS to converge ({Left})", string.Join("; ", left.Steps.Select(s => s.Kind)));
            await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
        }
    }

    static ReconcileResult Ready(Condition validated, DesiredAccess d, string bindingId, Plan plan, IReadOnlyList<Change>? changes = null) => new(Phase.Ready,
    [
        validated,
        new("RoleReady", true, "RoleConfigured", d.Role.Arn),
        new(BindingCondition(d.Request.Mechanism), true, d.Request.Mechanism == Mechanism.Irsa ? "AnnotationConfigured" : "AssociationConfigured", bindingId),
        new("Ready", true, "ReconciliationSucceeded", "AWS state was read back and matches the desired state"),
    ], d.Role.Arn, bindingId, Drift: plan.Drift, Changes: changes);

    /// "Created" for objects that did not exist, "Repaired" for drift, "Updated" for a requested change.
    static Change Describe(bool fresh, bool drift, string what, string text, string why) =>
        new(what + (drift ? "Repaired" : fresh ? "Created" : "Updated"), $"{(drift ? "repaired" : fresh ? "created" : "updated")} {text} ({why})", drift);

    // ---------------------------------------------------------------- desired state

    DesiredAccess BuildDesired(AccessRequest req, IReadOnlyList<PolicyRule> matching, ClusterContext c, OidcInfo? oidcInfo, out string? invalid)
    {
        invalid = null;
        var boundary = matching.Any(p => p.RequirePermissionsBoundary) ? Arn.Policy(c.Partition, c.AccountId, opt.PermissionsBoundary) : null;
        var name = Naming.RoleName(opt, c, req);
        var permissions = req.Statements.Count > 0 ? PolicyDocuments.Permissions(req.Statements) : null;
        if (permissions is { Length: > MaxInlineChars }) invalid = $"generated permission policy is {permissions.Length} characters; the limit is {MaxInlineChars}";
        var desc = req.Description ?? $"{MechanismNames.Name(req.Mechanism)} access for {req.Namespace}/{req.ServiceAccount} (managed by {opt.OperatorId})";
        var trust = req.Mechanism == Mechanism.Irsa
            ? PolicyDocuments.IrsaTrust(oidcInfo!, req.Namespace, req.ServiceAccount)
            : PolicyDocuments.Trust(c, req.Namespace, req.ServiceAccount, opt.EnforceSessionTagConditions);
        var role = new DesiredRole(name, Arn.Role(c.Partition, c.AccountId, opt.IamPath, name), trust,
            ReconciliationPlanner.PermissionsPolicyName, permissions, req.ManagedPolicyArns, boundary, owners.BuildTags(req, c), desc.Length > 1000 ? desc[..1000] : desc);
        return new DesiredAccess(req, c, role);
    }

    // ---------------------------------------------------------------- observation

    static string? PreviousRoleName(ObservedState s, PriorState prior, DesiredAccess d)
    {
        var arn = new[] { s.Association.RoleArn, prior.RoleArn }.FirstOrDefault(x => x is not null && x != d.Role.Arn);
        return arn is not null && Arn.TryParse(arn, out var a) && a.Resource.StartsWith("role/") ? a.Resource[(a.Resource.LastIndexOf('/') + 1)..] : null;
    }

    async Task<(ObservedState state, (ErrorKind kind, string reason, string msg)? refusal)> ObserveAsync(AccessRequest req, ClusterContext c, DesiredAccess d, PriorState prior, CancellationToken ct)
    {
        (ErrorKind, string, string)? Refuse(ErrorKind k, string reason, string msg) => (k, reason, msg);
        ObservedState Empty() => new(ObservedRole.Absent, ObservedAssociation.Absent, null);

        var role = await roles.ReadAsync(d.Role.Name, ct);
        var own = owners.Check(role, req, c);
        if (own is Ownership.Foreign or Ownership.OwnedByOtherWorkload)
            return (Empty(), Refuse(ErrorKind.Permanent, "RoleNotOwned", $"IAM role {d.Role.Arn} already exists and is not owned by this request ({own}); the operator will not modify it"));

        var assoc = await bindings.For(req.Mechanism).ReadAsync(c.ClusterName, req.Namespace, req.ServiceAccount, ct);
        if (assoc.Exists && assoc.RoleArn != d.Role.Arn && assoc.RoleArn != prior.RoleArn && !await BindingIsOursAsync(req, c, assoc, ct))
        {
            var what = req.Mechanism == Mechanism.Irsa ? "ServiceAccountAnnotation" : "Association";
            var text = req.Mechanism == Mechanism.Irsa ? $"ServiceAccount {req.Namespace}/{req.ServiceAccount} already has an eks.amazonaws.com/role-arn annotation" : $"a Pod Identity association for {req.Namespace}/{req.ServiceAccount} already exists";
            return (Empty(), Refuse(ErrorKind.Conflict, what + "NotOwned", $"{text} with role {assoc.RoleArn} that this operator does not own"));
        }

        // a role this operator created for this workload that the binding no longer uses (for example after a role name change)
        ObservedRole? previous = null;
        var probe = new ObservedState(role, assoc, null);
        if (PreviousRoleName(probe, prior, d) is { } prevName)
        {
            var p = await roles.ReadAsync(prevName, ct);
            if (owners.Check(p, req, c) == Ownership.Owned) previous = p;
        }

        return (new ObservedState(role, assoc, previous), null);
    }

    async Task<bool> BindingIsOursAsync(AccessRequest req, ClusterContext c, ObservedAssociation a, CancellationToken ct)
    {
        if (!Arn.TryParse(a.RoleArn, out var ra) || ra.AccountId != c.AccountId || !ra.Resource.StartsWith("role" + Arn.NormalizePath(opt.IamPath))) return false;
        var role = await roles.ReadAsync(ra.Resource[(ra.Resource.LastIndexOf('/') + 1)..], ct);
        return owners.Check(role, req, c) == Ownership.Owned;
    }

    // ---------------------------------------------------------------- cleanup

    /// Delete removes the binding and the role; Retain removes only the binding and leaves the role (still tagged).
    /// A role this operator does not own is never touched.
    public async Task<CleanupResult> CleanupAsync(CleanupInput i, CancellationToken ct)
    {
        var removed = new List<string>(); var retained = new List<string>();
        using var _ = Scope(i.Uid, i.Namespace, i.ServiceAccount, i.ServiceAccount, i.Mechanism);
        log.LogInformation("cleaning up with deletionPolicy {Policy}", i.Policy);
        try
        {
            var acct = await account.GetClusterAccountIdAsync(ct);
            var cluster = new ClusterContext(opt.ClusterName, acct, opt.Partition, opt.Region, opt.Environment);
            var req = new AccessRequest { Namespace = i.Namespace, ObjectName = i.ServiceAccount, Uid = i.Uid, ServiceAccount = i.ServiceAccount, RequestedRoleName = i.RequestedRoleName };
            var roleArn = i.Prior.RoleArn ?? Arn.Role(cluster.Partition, acct, opt.IamPath, Naming.RoleName(opt, cluster, req));

            var b = bindings.For(i.Mechanism);
            var seen = await b.ReadAsync(cluster.ClusterName, i.Namespace, i.ServiceAccount, ct);
            if (seen.Exists)
            {
                var label = i.Mechanism == Mechanism.Irsa ? $"serviceaccount-annotation {seen.Id}" : $"association {seen.Id}";
                if (seen.RoleArn == roleArn)
                {
                    await b.DeleteAsync(cluster.ClusterName, i.Namespace, i.ServiceAccount, seen.Id!, ct);
                    removed.Add(label);
                    log.LogInformation("removed {Binding}", label);
                }
                else { retained.Add($"{label} (not owned by this request)"); log.LogWarning("left {Binding} alone: it uses {Role}, not this workload's role", label, seen.RoleArn); }
            }

            Arn.TryParse(roleArn, out var ra);
            var name = ra.Resource[(ra.Resource.LastIndexOf('/') + 1)..];
            var role = await roles.ReadAsync(name, ct);
            switch (owners.Check(role, req, cluster))
            {
                case Ownership.Absent: log.LogDebug("role {Role} is already gone", roleArn); break;
                case Ownership.Owned when i.Policy == DeletionPolicy.Delete: await roles.DeleteRoleAsync(name, ct); removed.Add(roleArn); log.LogInformation("deleted role {Role}", roleArn); break;
                case Ownership.Owned: retained.Add($"{roleArn} (retained, still tagged {Tags.Operator}={opt.OperatorId})"); log.LogInformation("kept role {Role} (deletionPolicy Retain)", roleArn); break;
                default: retained.Add($"{roleArn} (not owned by this operator)"); log.LogWarning("did not delete role {Role}: it is not owned by this operator", roleArn); break;
            }
            log.LogInformation("cleanup complete: {Removed} removed, {Retained} retained", removed.Count, retained.Count);
            return new CleanupResult(true, retained, removed);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Telemetry.CleanupFailures.Add(1);
            var kind = classifier.Classify(e);
            log.LogWarning("cleanup failed ({Kind}), will retry: {Message}", kind, Redactor.Redact(e.Message));
            log.LogDebug(e, "cleanup failure details");
            return new CleanupResult(false, retained, removed, kind, Redactor.Redact(e.Message));
        }
    }
}
