namespace EksDevopsOperator.Core;

public enum DeletionPolicy { Delete, Retain }

/// How a ServiceAccount receives the IAM role. PodIdentity: an EKS Pod Identity association (kind PodIdentityAccess). Irsa: OIDC trust plus the
/// eks.amazonaws.com/role-arn annotation on the ServiceAccount, IAM Roles for Service Accounts (kind IrsaAccess).
public enum Mechanism { PodIdentity, Irsa }

public static class MechanismNames
{
    public static string Name(Mechanism m) => m == Mechanism.Irsa ? "IRSA" : "PodIdentity";
    public static Mechanism Parse(string? s, Mechanism fallback = Mechanism.PodIdentity) =>
        string.Equals(s, "IRSA", StringComparison.OrdinalIgnoreCase) ? Mechanism.Irsa
        : string.Equals(s, "PodIdentity", StringComparison.OrdinalIgnoreCase) ? Mechanism.PodIdentity : fallback;
}

/// The cluster's OIDC identity provider, which IRSA roles trust. Issuer has no scheme (oidc.eks.<region>.amazonaws.com/id/<id>).
public sealed record OidcInfo(string Issuer, string ProviderArn);

public sealed record PolicyStatement(string? Sid, string Effect, IReadOnlyList<string> Actions, IReadOnlyList<string> Resources);

/// The internal request model built from a PodIdentityAccess resource.
public sealed record AccessRequest
{
    public required string Namespace { get; init; }
    public required string ObjectName { get; init; }
    public required string Uid { get; init; }
    public long Generation { get; init; }
    public required string ServiceAccount { get; init; }
    public string? RequestedRoleName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> ManagedPolicyArns { get; init; } = [];
    public IReadOnlyList<PolicyStatement> Statements { get; init; } = [];
    /// Per-request tags, added to the global tags. Keys that the global tags already set are rejected.
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public DeletionPolicy DeletionPolicy { get; init; } = DeletionPolicy.Delete;
    public Mechanism Mechanism { get; init; } = Mechanism.PodIdentity;
}

/// The cluster the operator runs in. Roles and associations always belong to this cluster and its AWS account.
public sealed record ClusterContext(string ClusterName, string AccountId, string Partition, string Region, string Environment)
{
    public string ClusterArn => $"arn:{Partition}:eks:{Region}:{AccountId}:cluster/{ClusterName}";
}

// ----- administrator guardrails (PodIdentityPolicy objects in the operator namespace) -----

public sealed record PolicyRule
{
    public required string Name { get; init; }
    /// Namespaces this policy governs. Empty = every namespace.
    public IReadOnlyList<string> Namespaces { get; init; } = [];
    /// IAM service prefixes teams may use. "*" (the default) allows every service; an empty list allows none.
    /// Ways teams may bind the role to a ServiceAccount: "PodIdentity", "IRSA". "*" (the default) allows both; an empty list allows none.
    public IReadOnlyList<string> AllowedMechanisms { get; init; } = ["*"];
    public IReadOnlyList<string> AllowedServices { get; init; } = ["*"];
    /// Action globs teams may request (e.g. s3:Get*). "*" (the default) allows every action; an empty list allows none.
    public IReadOnlyList<string> AllowedActions { get; init; } = ["*"];
    /// Action globs that are always refused.
    public IReadOnlyList<string> DeniedActions { get; init; } = [];
    /// Resource ARN globs ('*' only) every requested resource must match. Empty = any ARN. Resources may belong to any AWS account.
    public IReadOnlyList<string> AllowedResourceArnPatterns { get; init; } = [];
    public IReadOnlyList<string> AllowedManagedPolicyArns { get; init; } = [];
    public IReadOnlyList<DeletionPolicy> AllowedDeletionPolicies { get; init; } = [DeletionPolicy.Delete];
    public bool RequirePermissionsBoundary { get; init; } = true;
    public bool AllowWildcardActions { get; init; }
    public bool AllowWildcardResources { get; init; }
    public bool AllowPrivilegeEscalationActions { get; init; }

    public bool Governs(string ns) => Namespaces.Count == 0 || Namespaces.Contains(ns);
}

public sealed class OperatorOptions
{
    public string ClusterName { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Partition { get; set; } = "aws";
    public string Environment { get; set; } = "dev";
    public string IamPath { get; set; } = "/eks-devops-operator/";
    public string RolePrefix { get; set; } = "pio";
    public string OperatorId { get; set; } = "eks-devops-operator";
    /// Permissions boundary policy (path + name) attached to every role when a policy requires it. Must exist in the cluster account.
    public string PermissionsBoundary { get; set; } = "/eks-devops-operator/standard-workload";
    /// Tags added to every AWS resource the operator creates (roles and associations). Admin controlled.
    public Dictionary<string, string> Tags { get; set; } = new();
    /// Namespace the operator runs in. PodIdentityPolicy objects are read ONLY from here, so teams cannot create their own guardrails.
    public string PolicyNamespace { get; set; } = "";
    /// Port of the probe and metrics endpoint (/health/live, /health/ready, /metrics).
    public int HttpPort { get; set; } = 8080;
    /// IRSA needs permission to annotate ServiceAccounts, so it is opt-in (Helm: irsa.enabled). When false, IRSA requests are rejected.
    public bool IrsaEnabled { get; set; }
    /// ServiceAccounts annotated identity.platform.io/enabled=true create access resources (Helm: serviceAccountAnnotations.enabled). Needs extra RBAC, so opt-in.
    public bool ServiceAccountAnnotations { get; set; }
    public TimeSpan ServiceAccountSyncInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// OIDC issuer of the cluster (with or without https://). Empty = discovered from the cluster (eks:DescribeCluster).
    public string? OidcIssuer { get; set; }
    /// "+" listens on every interface (needed in a pod). Windows only allows "localhost" without administrator rights.
    public string HttpHost { get; set; } = OperatingSystem.IsWindows() ? "localhost" : "+";
    public List<string> ProtectedRoleNamePatterns { get; set; } = ["*Admin*", "*admin*", "AWSServiceRole*", "OrganizationAccountAccessRole", "aws-*", "eks-*"];
    public TimeSpan DriftInterval { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ConsistencyTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// Require the Pod Identity session tags (cluster, namespace, ServiceAccount) in the role trust policy.
    public bool EnforceSessionTagConditions { get; set; } = true;
}

// ----- observed AWS state -----

public sealed record ObservedRole(
    bool Exists, string? Arn, string? Path, string? TrustPolicy,
    IReadOnlyDictionary<string, string> Tags, string? PermissionsBoundaryArn,
    IReadOnlyDictionary<string, string> InlinePolicies, IReadOnlyList<string> AttachedPolicyArns)
{
    public static ObservedRole Absent { get; } = new(false, null, null, null, new Dictionary<string, string>(), null, new Dictionary<string, string>(), []);
}

public sealed record ObservedAssociation(bool Exists, string? Id, string? Arn, string? RoleArn, IReadOnlyDictionary<string, string> Tags)
{
    public static ObservedAssociation Absent { get; } = new(false, null, null, null, new Dictionary<string, string>());
}

public enum Ownership { Absent, Owned, OwnedByOtherWorkload, Foreign }

// ----- desired state -----

public sealed record DesiredRole(
    string Name, string Arn, string TrustPolicy, string InlinePolicyName, string? InlinePolicy,
    IReadOnlyList<string> ManagedPolicyArns, string? PermissionsBoundaryArn, IReadOnlyDictionary<string, string> Tags, string? Description);

public sealed record DesiredAccess(AccessRequest Request, ClusterContext Cluster, DesiredRole Role);

/// PreviousRole: a role this operator created for this workload that the association no longer uses (for example after a role name change).
/// Association = the binding between ServiceAccount and role: a Pod Identity association, or the IRSA annotation.
public sealed record ObservedState(ObservedRole Role, ObservedAssociation Association, ObservedRole? PreviousRole);

/// ObservedGeneration: the spec generation last reconciled successfully. A difference from the request's generation means the
/// team changed the spec (an update, not drift).
public sealed record PriorState(string? RoleArn, string? AssociationId, long ObservedGeneration = 0);
