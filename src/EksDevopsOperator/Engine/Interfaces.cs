namespace EksDevopsOperator.Core;

public interface IAwsAccountResolver
{
    /// Account that owns the credentials the operator runs with (STS GetCallerIdentity). Cached.
    Task<string> GetClusterAccountIdAsync(CancellationToken ct);
}

public interface IIamRoleManager
{
    Task<ObservedRole> ReadAsync(string roleName, CancellationToken ct);
    /// Creates the role, or converges trust policy, boundary and tags. Never called for roles the operator does not own.
    Task EnsureRoleAsync(DesiredRole role, ObservedRole current, CancellationToken ct);
    Task DeleteRoleAsync(string roleName, CancellationToken ct);
}

public interface IIamPolicyManager
{
    /// document = null removes the policy.
    Task EnsureInlinePolicyAsync(string roleName, string policyName, string? document, CancellationToken ct);
    Task EnsureAttachedPoliciesAsync(string roleName, IReadOnlyList<string> desiredArns, CancellationToken ct);
}

/// The link between a ServiceAccount and its IAM role: an EKS Pod Identity association, or the IRSA annotation on the ServiceAccount.
public interface IBindingManager
{
    Mechanism Mechanism { get; }
    Task<ObservedAssociation> ReadAsync(string clusterName, string ns, string serviceAccount, CancellationToken ct);
    /// Creates the binding, or converges its role (and tags where the mechanism has them). Returns the binding id.
    Task<string> EnsureAsync(string clusterName, string ns, string serviceAccount, string roleArn, IReadOnlyDictionary<string, string> tags, CancellationToken ct);
    Task DeleteAsync(string clusterName, string ns, string serviceAccount, string bindingId, CancellationToken ct);
}

public interface IBindingResolver { IBindingManager For(Mechanism mechanism); }

public interface IOidcResolver
{
    /// The cluster's OIDC issuer and the IAM provider that IRSA roles trust. Fails with a clear message when either is missing.
    Task<OidcInfo> ResolveAsync(ClusterContext cluster, CancellationToken ct);
}

public sealed record ValidationIssue(string Code, string Message);
public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
    public string Summary => string.Join("; ", Issues.Select(i => $"{i.Code}: {i.Message}"));
}

public interface IPolicyValidator
{
    /// matching = the PodIdentityPolicy rules that govern the request's namespace. None means deny.
    ValidationResult Validate(AccessRequest request, IReadOnlyList<PolicyRule> matching, ClusterContext cluster);
}

public interface IPolicySource
{
    /// Every guardrail policy defined by platform admins.
    Task<IReadOnlyList<PolicyRule>> ListAsync(CancellationToken ct);
}

public interface IResourceOwnershipService
{
    /// Decides whether the operator may modify or delete a role it found in IAM.
    Ownership Check(ObservedRole role, AccessRequest request, ClusterContext cluster);
    /// Ownership tags plus the global and per-request tags.
    IReadOnlyDictionary<string, string> BuildTags(AccessRequest request, ClusterContext cluster);
}

public interface IReconciliationPlanner
{
    Plan Plan(DesiredAccess desired, ObservedState observed);
}

public interface IErrorClassifier
{
    ErrorKind Classify(Exception e);
}

public interface IConflictDetector
{
    /// Other resources that claim the same (namespace, ServiceAccount). Empty when this request is the sole or winning owner.
    Task<IReadOnlyList<string>> FindConflictsAsync(AccessRequest request, CancellationToken ct);
}
