namespace EksDevopsOperator.Core;

public enum StepKind { EnsureRole, EnsureInlinePolicy, EnsureManagedPolicies, EnsureBinding, DeletePreviousRole }

public sealed record PlanStep(StepKind Kind, string Reason);

public sealed record Plan(IReadOnlyList<PlanStep> Steps, IReadOnlyList<string> Drift)
{
    public bool Converged => Steps.Count == 0;
}

/// Pure function of desired vs observed state. The role is made right before the binding points at it, and an obsolete role is removed last.
/// Reasons are neutral ("differs"): whether a difference is an update or drift is decided by the caller (did the spec change?).
public sealed class ReconciliationPlanner : IReconciliationPlanner
{
    public const string PermissionsPolicyName = "pio-permissions";

    public Plan Plan(DesiredAccess d, ObservedState o)
    {
        var steps = new List<PlanStep>();
        var drift = new List<string>();
        void Add(StepKind k, string why, bool isDrift) { steps.Add(new(k, why)); if (isDrift) drift.Add($"{k}: {why}"); }

        var obs = o.Role;
        var want = d.Role;
        if (!obs.Exists)
        {
            Add(StepKind.EnsureRole, "role missing", false);
            Add(StepKind.EnsureInlinePolicy, "inline policy for new role", false);
            Add(StepKind.EnsureManagedPolicies, "managed policies for new role", false);
        }
        else
        {
            if (!PolicyJson.Equivalent(obs.TrustPolicy, want.TrustPolicy)) Add(StepKind.EnsureRole, "trust policy differs", true);
            else if (obs.PermissionsBoundaryArn != want.PermissionsBoundaryArn) Add(StepKind.EnsureRole, "permissions boundary differs", true);
            else if (TagRules.Differs(obs.Tags, want.Tags)) Add(StepKind.EnsureRole, "tags differ", true);

            obs.InlinePolicies.TryGetValue(want.InlinePolicyName, out var have);
            if (want.InlinePolicy is null ? have is not null : !PolicyJson.Equivalent(have, want.InlinePolicy))
                Add(StepKind.EnsureInlinePolicy, have is null ? "inline policy missing" : "inline policy differs", true);
            if (!obs.AttachedPolicyArns.ToHashSet().SetEquals(want.ManagedPolicyArns)) Add(StepKind.EnsureManagedPolicies, "attached managed policies differ", true);
        }

        var a = o.Association;
        if (!a.Exists) Add(StepKind.EnsureBinding, "binding missing", false);
        else if (a.RoleArn != want.Arn) Add(StepKind.EnsureBinding, $"binding uses role {a.RoleArn}", false);
        // an IRSA annotation cannot carry tags, so only Pod Identity associations are compared
        else if (d.Request.Mechanism == Mechanism.PodIdentity && TagRules.Differs(a.Tags, want.Tags)) Add(StepKind.EnsureBinding, "binding tags differ", true);

        if (o.PreviousRole is { Exists: true }) steps.Add(new(StepKind.DeletePreviousRole, "role no longer used by the binding"));
        return new Plan(steps, drift);
    }
}
