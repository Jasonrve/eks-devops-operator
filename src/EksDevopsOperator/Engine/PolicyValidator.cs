using System.Text.RegularExpressions;
using EksDevopsOperator.Core;

namespace EksDevopsOperator.Policy;

/// Deny-by-default request validation. Runs on every reconcile; a permissions boundary is an additional guardrail,
/// never a substitute for these checks.
public sealed partial class PolicyValidator(OperatorOptions options) : IPolicyValidator
{
    static readonly string[] EscalationServices = ["iam", "sts", "organizations", "account", "sso", "sso-admin", "identitystore", "cloudformation"];
    static readonly string[] EscalationActions =
    [
        "ec2:AssociateIamInstanceProfile", "ec2:ReplaceIamInstanceProfileAssociation", "lambda:CreateFunction", "lambda:UpdateFunctionCode",
        "lambda:UpdateFunctionConfiguration", "kms:CreateGrant", "kms:PutKeyPolicy", "s3:PutBucketPolicy", "s3:PutBucketAcl",
        "secretsmanager:PutResourcePolicy", "sqs:AddPermission", "sns:AddPermission", "ssm:SendCommand", "ssm:StartSession",
    ];
    static readonly Regex ActionFormat = ActionRegex();
    static readonly Regex DnsLabel = DnsRegex();

    public ValidationResult Validate(AccessRequest r, IReadOnlyList<PolicyRule> matching, ClusterContext cluster)
    {
        var issues = new List<ValidationIssue>();
        void Fail(string code, string msg) => issues.Add(new(code, msg));

        if (!DnsLabel.IsMatch(r.Namespace) || !DnsLabel.IsMatch(r.ServiceAccount.Replace('.', '-'))) Fail("InvalidIdentity", "namespace/serviceAccount are not valid Kubernetes names");

        // deny by default: at least one PodIdentityPolicy must govern the namespace, and every governing policy must accept the request
        if (matching.Count == 0) Fail("NamespaceNotAuthorized", $"no PodIdentityPolicy applies to namespace '{r.Namespace}'");
        foreach (var p in matching) ValidateAgainstPolicy(r, p, cluster, Fail);
        ValidateCommon(r, Fail);
        return new ValidationResult(issues);
    }

    void ValidateAgainstPolicy(AccessRequest r, PolicyRule p, ClusterContext c, Action<string, string> fail)
    {
        var mechanism = MechanismNames.Name(r.Mechanism);
        if (!p.AllowedMechanisms.Contains("*") && !p.AllowedMechanisms.Contains(mechanism, StringComparer.OrdinalIgnoreCase)) fail("MechanismNotAllowed", $"policy '{p.Name}' does not allow the {mechanism} mechanism");
        if (!p.AllowedDeletionPolicies.Contains(r.DeletionPolicy)) fail("DeletionPolicyNotAllowed", $"policy '{p.Name}' does not allow deletionPolicy {r.DeletionPolicy}");
        if (p.RequirePermissionsBoundary && string.IsNullOrWhiteSpace(options.PermissionsBoundary)) fail("BoundaryRequired", "a permissions boundary is required but the operator has none configured");

        foreach (var arn in r.ManagedPolicyArns)
            if (!p.AllowedManagedPolicyArns.Any(pat => Arn.GlobMatch(pat, arn))) fail("ManagedPolicyNotAllowed", $"managed policy '{arn}' is not on policy '{p.Name}' allow-list");

        foreach (var s in r.Statements)
        {
            foreach (var action in s.Actions)
            {
                if (!ActionFormat.IsMatch(action)) { fail("InvalidAction", $"'{action}' is not a valid IAM action"); continue; }
                var service = action.Split(':')[0].ToLowerInvariant();
                if (action.Contains('*') || action.Contains('?')) { if (!p.AllowWildcardActions) fail("WildcardAction", $"wildcard action '{action}' is not allowed by policy '{p.Name}'"); }
                if (!p.AllowedServices.Contains("*") && !p.AllowedServices.Contains(service, StringComparer.OrdinalIgnoreCase)) fail("ServiceNotAllowed", $"service '{service}' (action '{action}') is not allowed by policy '{p.Name}'");
                if (!p.AllowPrivilegeEscalationActions && IsEscalation(action)) fail("PrivilegeEscalation", $"action '{action}' can escalate privileges");
                if (p.DeniedActions.Any(d => Arn.GlobMatch(d.ToLowerInvariant(), action.ToLowerInvariant()))) fail("ActionDenied", $"action '{action}' is denied by policy '{p.Name}'");
                if (!p.AllowedActions.Any(a => Arn.GlobMatch(a.ToLowerInvariant(), action.ToLowerInvariant()))) fail("ActionNotAllowed", $"action '{action}' is not on the allowed action list of policy '{p.Name}'");
            }
            foreach (var res in s.Resources) ValidateResource(res, p, c, fail);
        }
    }

    // Resources may belong to any AWS account (for example a bucket in another account that trusts this role); only the partition is checked.
    static void ValidateResource(string res, PolicyRule p, ClusterContext c, Action<string, string> fail)
    {
        if (res == "*") { if (!p.AllowWildcardResources) fail("WildcardResource", $"resource '*' is not allowed by policy '{p.Name}'"); return; }
        if (!Arn.TryParse(res, out var arn)) { fail("InvalidResource", $"'{res}' is not a valid ARN"); return; }
        if (arn.Partition != c.Partition) { fail("PartitionMismatch", $"resource '{res}' is not in partition '{c.Partition}'"); return; }
        var broad = arn.Service == "*" || arn.Resource.Trim('*', '?', '/').Length == 0;
        if (broad && !p.AllowWildcardResources) fail("WildcardResource", $"resource '{res}' is too broad for policy '{p.Name}'");
        if (p.AllowedResourceArnPatterns.Count > 0 && !p.AllowedResourceArnPatterns.Any(pat => Arn.GlobMatch(pat, res)))
            fail("ResourceOutOfScope", $"resource '{res}' is outside the scope approved by policy '{p.Name}'");
    }

    public static bool IsEscalation(string action)
    {
        var parts = action.Split(':', 2);
        if (parts.Length != 2 || action.Equals("sts:GetCallerIdentity", StringComparison.OrdinalIgnoreCase)) return false;
        var svc = parts[0].ToLowerInvariant();
        if (svc == "*") return true;
        return EscalationServices.Any(s => Arn.GlobMatch(svc, s)) || EscalationActions.Any(e => Arn.GlobMatch(action.ToLowerInvariant(), e.ToLowerInvariant()));
    }

    void ValidateCommon(AccessRequest r, Action<string, string> fail)
    {
        if (r.Mechanism == Mechanism.Irsa && !options.IrsaEnabled) fail("IrsaDisabled", "IRSA is not enabled on this operator (platform team: set Helm value irsa.enabled=true)");
        if (r.RequestedRoleName is not null)
        {
            if (!Naming.IsValidRequestedName(r.RequestedRoleName)) fail("InvalidRoleName", "role name must match ^[a-z0-9][a-z0-9-]{0,39}$");
            if (options.ProtectedRoleNamePatterns.Any(pat => Arn.GlobMatch(pat, r.RequestedRoleName))) fail("ProtectedRoleName", $"role name '{r.RequestedRoleName}' matches a protected role pattern");
        }
        foreach (var (code, message) in TagRules.Validate(r.Tags, options.Tags, isGlobal: false)) fail(code, message);
        if (r.Statements.Count > 20) fail("TooManyStatements", "at most 20 statements are allowed");
        if (r.Statements.Count == 0 && r.ManagedPolicyArns.Count == 0) fail("NoPermissions", "the request grants no permissions");
        foreach (var s in r.Statements) if (s.Actions.Count == 0 || s.Resources.Count == 0) fail("EmptyStatement", $"statement '{s.Sid}' needs actions and resources");
    }

    [GeneratedRegex(@"^([a-zA-Z0-9-]+|\*):[a-zA-Z0-9*?]+$")] private static partial Regex ActionRegex();
    [GeneratedRegex(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")] private static partial Regex DnsRegex();
}
