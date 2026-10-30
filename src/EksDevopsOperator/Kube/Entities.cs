using EksDevopsOperator.Core;
using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace EksDevopsOperator.Kubernetes;

public static class Api
{
    public const string Group = "identity.platform.io";
    public const string Version = "v1alpha1";
    public const string FinalizerName = "identity.platform.io/finalizer";
}

/// Gives a ServiceAccount AWS permissions through EKS Pod Identity. The operator creates an IAM role in the cluster's AWS account and a
/// Pod Identity association that connects the ServiceAccount to it. Pods using the ServiceAccount then receive the role's credentials.
[KubernetesEntity(Group = Api.Group, ApiVersion = Api.Version, Kind = "PodIdentityAccess", PluralName = "podidentityaccesses")]
[KubernetesEntityShortNames("pia")]
[GenericAdditionalPrinterColumn(".status.phase", "Phase", "string")]
[GenericAdditionalPrinterColumn(".spec.serviceAccountName", "ServiceAccount", "string")]
[GenericAdditionalPrinterColumn(".status.roleArn", "Role", "string")]
public sealed class V1Alpha1PodIdentityAccess : CustomKubernetesEntity<PodIdentityAccessSpec, AccessStatus>
{
}

/// Gives a ServiceAccount AWS permissions through IRSA (IAM Roles for Service Accounts). The operator creates an IAM role that trusts the
/// cluster's OIDC provider for this ServiceAccount and sets the eks.amazonaws.com/role-arn annotation on it.
[KubernetesEntity(Group = Api.Group, ApiVersion = Api.Version, Kind = "IrsaAccess", PluralName = "irsaaccesses")]
[KubernetesEntityShortNames("irsa")]
[GenericAdditionalPrinterColumn(".status.phase", "Phase", "string")]
[GenericAdditionalPrinterColumn(".spec.serviceAccountName", "ServiceAccount", "string")]
[GenericAdditionalPrinterColumn(".status.roleArn", "Role", "string")]
public sealed class V1Alpha1IrsaAccess : CustomKubernetesEntity<IrsaAccessSpec, AccessStatus>
{
}

public sealed class PodIdentityAccessSpec : AccessSpec
{
    public override Mechanism BindingMechanism() => Mechanism.PodIdentity;
}

public sealed class IrsaAccessSpec : AccessSpec
{
    public override Mechanism BindingMechanism() => Mechanism.Irsa;
}

/// Fields shared by both kinds. The kind, not a field, decides how the ServiceAccount receives the role.
public abstract class AccessSpec
{
    public abstract Mechanism BindingMechanism();

    [Required, Pattern(@"^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$")]
    public string ServiceAccountName { get; set; } = "";

    [Description("Optional role name prefix; the operator appends a stable hash."), Pattern(@"^[a-z0-9][a-z0-9-]{0,39}$")]
    public string? RoleName { get; set; }

    [Description("Extra AWS tags for the role (and the Pod Identity association), added to the global tags the platform team configured. Max 10. Keys starting with pio: or aws: are reserved, and keys the global tags already set are rejected.")]
    public Dictionary<string, string> Tags { get; set; } = new();

    [Description("IAM managed policy ARNs to attach (must be on the PodIdentityPolicy allow-list).")]
    public List<string> ManagedPolicies { get; set; } = [];

    public List<StatementSpec> Statements { get; set; } = [];

    [Description("Delete removes the role and binding when this resource is deleted. Retain removes only the binding."), EnumValues("Delete", "Retain"), DefaultValue("Delete")]
    public string DeletionPolicy { get; set; } = "Delete";
}

public sealed class StatementSpec
{
    [Pattern(@"^[A-Za-z0-9]{1,128}$")] public string? Sid { get; set; }
    [Required] public List<string> Actions { get; set; } = [];
    [Description("Resource ARNs. They may belong to any AWS account (that account must also allow this role).")]
    [Required] public List<string> Resources { get; set; } = [];
}

public sealed class AccessStatus
{
    [Description("Pending | Ready | Failed | Invalid | Conflict")] public string Phase { get; set; } = "Pending";
    public long ObservedGeneration { get; set; }
    public string? RoleArn { get; set; }
    [Description("Pod Identity association id, or namespace/name of the annotated ServiceAccount for IRSA.")] public string? AssociationId { get; set; }
    public string? Message { get; set; }
    public List<ConditionStatus> Conditions { get; set; } = [];
}

public sealed class ConditionStatus
{
    [Required] public string Type { get; set; } = "";
    [Required, EnumValues("True", "False")] public string Status { get; set; } = "False";
    public string Reason { get; set; } = "";
    public string Message { get; set; } = "";
}

/// Guardrails for what teams may request. Only objects in the OPERATOR'S OWN namespace are read, so team members who
/// can create resources in their own namespaces cannot widen what is allowed. Protect that namespace with RBAC.
[KubernetesEntity(Group = Api.Group, ApiVersion = Api.Version, Kind = "PodIdentityPolicy", PluralName = "podidentitypolicies")]
[KubernetesEntityShortNames("pip")]
public sealed class V1Alpha1PodIdentityPolicy : CustomKubernetesEntity<PodIdentityPolicySpec>
{
}

public sealed class PodIdentityPolicySpec
{
    [Description("Namespaces this policy governs. Empty = all namespaces. A request must satisfy every policy that governs its namespace, and is denied if none does.")]
    public List<string> Namespaces { get; set; } = [];
    [Description("Kinds of access teams may request: PodIdentity (PodIdentityAccess), IRSA (IrsaAccess). \"*\" (the default) allows both; an empty list allows none. IRSA also needs irsa.enabled on the operator.")]
    public List<string> AllowedMechanisms { get; set; } = ["*"];
    [Description("IAM service prefixes, e.g. s3, sqs. \"*\" (the default) allows every service; an empty list allows none.")]
    public List<string> AllowedServices { get; set; } = ["*"];
    [Description("Action globs teams may use, e.g. s3:Get*, s3:ListBucket. \"*\" (the default) allows every action; an empty list allows none.")]
    public List<string> AllowedActions { get; set; } = ["*"];
    [Description("Action globs that are always refused.")]
    public List<string> DeniedActions { get; set; } = [];
    [Description("Resource ARN globs every requested resource must match, e.g. arn:aws:s3:::payments-*. Empty = any ARN.")]
    public List<string> AllowedResourceArnPatterns { get; set; } = [];
    [Description("Managed policy ARN globs teams may attach. Empty = none.")]
    public List<string> AllowedManagedPolicyArns { get; set; } = [];
    [Description("Delete and/or Retain.")]
    public List<string> AllowedDeletionPolicies { get; set; } = ["Delete"];
    [DefaultValue(true)] public bool RequirePermissionsBoundary { get; set; } = true;
    public bool AllowWildcardActions { get; set; }
    public bool AllowWildcardResources { get; set; }
    public bool AllowPrivilegeEscalationActions { get; set; }
}
