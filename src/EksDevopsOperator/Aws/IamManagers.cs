using Amazon.IdentityManagement.Model;
using EksDevopsOperator.Core;

namespace EksDevopsOperator.Aws;

public sealed class IamRoleManager(AwsClients aws) : IIamRoleManager
{
    public async Task<ObservedRole> ReadAsync(string roleName, CancellationToken ct)
    {
        var iam = aws.Iam;
        Role role;
        try { role = (await Telemetry.AwsCall("iam", "GetRole", () => iam.GetRoleAsync(new GetRoleRequest { RoleName = roleName }, ct))).Role; }
        catch (NoSuchEntityException) { return ObservedRole.Absent; }

        var inline = new Dictionary<string, string>();
        var names = await Telemetry.AwsCall("iam", "ListRolePolicies", () => iam.ListRolePoliciesAsync(new ListRolePoliciesRequest { RoleName = roleName }, ct));
        foreach (var n in names.PolicyNames ?? [])
        {
            var p = await Telemetry.AwsCall("iam", "GetRolePolicy", () => iam.GetRolePolicyAsync(new GetRolePolicyRequest { RoleName = roleName, PolicyName = n }, ct));
            inline[n] = p.PolicyDocument;
        }
        var attached = await Telemetry.AwsCall("iam", "ListAttachedRolePolicies", () => iam.ListAttachedRolePoliciesAsync(new ListAttachedRolePoliciesRequest { RoleName = roleName }, ct));
        var tags = role.Tags is { Count: > 0 } ? role.Tags
            : (await Telemetry.AwsCall("iam", "ListRoleTags", () => iam.ListRoleTagsAsync(new ListRoleTagsRequest { RoleName = roleName }, ct))).Tags ?? [];
        return new ObservedRole(true, role.Arn, role.Path, role.AssumeRolePolicyDocument, tags.ToDictionary(t => t.Key, t => t.Value),
            role.PermissionsBoundary?.PermissionsBoundaryArn, inline, (attached.AttachedPolicies ?? []).Select(a => a.PolicyArn).ToList());
    }

    public async Task EnsureRoleAsync(DesiredRole want, ObservedRole cur, CancellationToken ct)
    {
        var iam = aws.Iam;
        var path = want.Arn[(want.Arn.IndexOf(":role", StringComparison.Ordinal) + 5)..];
        path = path[..(path.LastIndexOf('/') + 1)];
        if (!cur.Exists)
        {
            try
            {
                await Telemetry.AwsCall("iam", "CreateRole", () => iam.CreateRoleAsync(new CreateRoleRequest
                {
                    RoleName = want.Name, Path = path, AssumeRolePolicyDocument = want.TrustPolicy, Description = want.Description,
                    PermissionsBoundary = want.PermissionsBoundaryArn, Tags = want.Tags.Select(t => new Tag { Key = t.Key, Value = t.Value }).ToList(),
                }, ct));
                return;
            }
            catch (EntityAlreadyExistsException) { cur = await ReadAsync(want.Name, ct); } // created by someone else since we looked: converge it
        }

        if (!PolicyJson.Equivalent(cur.TrustPolicy, want.TrustPolicy))
            await Telemetry.AwsCall("iam", "UpdateAssumeRolePolicy", () => iam.UpdateAssumeRolePolicyAsync(new UpdateAssumeRolePolicyRequest { RoleName = want.Name, PolicyDocument = want.TrustPolicy }, ct));
        if (cur.PermissionsBoundaryArn != want.PermissionsBoundaryArn)
        {
            if (want.PermissionsBoundaryArn is null) await Telemetry.AwsCall("iam", "DeleteRolePermissionsBoundary", () => iam.DeleteRolePermissionsBoundaryAsync(new DeleteRolePermissionsBoundaryRequest { RoleName = want.Name }, ct));
            else await Telemetry.AwsCall("iam", "PutRolePermissionsBoundary", () => iam.PutRolePermissionsBoundaryAsync(new PutRolePermissionsBoundaryRequest { RoleName = want.Name, PermissionsBoundary = want.PermissionsBoundaryArn }, ct));
        }
        var missing = want.Tags.Where(t => !cur.Tags.TryGetValue(t.Key, out var v) || v != t.Value).Select(t => new Tag { Key = t.Key, Value = t.Value }).ToList();
        if (missing.Count > 0) await Telemetry.AwsCall("iam", "TagRole", () => iam.TagRoleAsync(new TagRoleRequest { RoleName = want.Name, Tags = missing }, ct));
        var stale = TagRules.Stale(cur.Tags, want.Tags); // only keys the operator itself applied earlier
        if (stale.Count > 0) await Telemetry.AwsCall("iam", "UntagRole", () => iam.UntagRoleAsync(new UntagRoleRequest { RoleName = want.Name, TagKeys = stale }, ct));
    }

    public async Task DeleteRoleAsync(string roleName, CancellationToken ct)
    {
        var iam = aws.Iam;
        try
        {
            foreach (var p in (await Telemetry.AwsCall("iam", "ListAttachedRolePolicies", () => iam.ListAttachedRolePoliciesAsync(new ListAttachedRolePoliciesRequest { RoleName = roleName }, ct))).AttachedPolicies ?? [])
                await Telemetry.AwsCall("iam", "DetachRolePolicy", () => iam.DetachRolePolicyAsync(new DetachRolePolicyRequest { RoleName = roleName, PolicyArn = p.PolicyArn }, ct));
            foreach (var n in (await Telemetry.AwsCall("iam", "ListRolePolicies", () => iam.ListRolePoliciesAsync(new ListRolePoliciesRequest { RoleName = roleName }, ct))).PolicyNames ?? [])
                await Telemetry.AwsCall("iam", "DeleteRolePolicy", () => iam.DeleteRolePolicyAsync(new DeleteRolePolicyRequest { RoleName = roleName, PolicyName = n }, ct));
            await Telemetry.AwsCall("iam", "DeleteRole", () => iam.DeleteRoleAsync(new DeleteRoleRequest { RoleName = roleName }, ct));
        }
        catch (NoSuchEntityException) { /* already gone */ }
    }
}

public sealed class IamPolicyManager(AwsClients aws) : IIamPolicyManager
{
    public async Task EnsureInlinePolicyAsync(string roleName, string policyName, string? document, CancellationToken ct)
    {
        var iam = aws.Iam;
        if (document is null)
        {
            try { await Telemetry.AwsCall("iam", "DeleteRolePolicy", () => iam.DeleteRolePolicyAsync(new DeleteRolePolicyRequest { RoleName = roleName, PolicyName = policyName }, ct)); }
            catch (NoSuchEntityException) { }
            return;
        }
        string? cur = null;
        try { cur = (await Telemetry.AwsCall("iam", "GetRolePolicy", () => iam.GetRolePolicyAsync(new GetRolePolicyRequest { RoleName = roleName, PolicyName = policyName }, ct))).PolicyDocument; }
        catch (NoSuchEntityException) { }
        if (cur is null || !PolicyJson.Equivalent(cur, document))
            await Telemetry.AwsCall("iam", "PutRolePolicy", () => iam.PutRolePolicyAsync(new PutRolePolicyRequest { RoleName = roleName, PolicyName = policyName, PolicyDocument = document }, ct));
    }

    /// The role belongs to the operator, so the attached set is made to match exactly.
    public async Task EnsureAttachedPoliciesAsync(string roleName, IReadOnlyList<string> desired, CancellationToken ct)
    {
        var iam = aws.Iam;
        var have = ((await Telemetry.AwsCall("iam", "ListAttachedRolePolicies", () => iam.ListAttachedRolePoliciesAsync(new ListAttachedRolePoliciesRequest { RoleName = roleName }, ct)))
            .AttachedPolicies ?? []).Select(a => a.PolicyArn).ToHashSet();
        foreach (var arn in desired.Where(a => !have.Contains(a)))
            await Telemetry.AwsCall("iam", "AttachRolePolicy", () => iam.AttachRolePolicyAsync(new AttachRolePolicyRequest { RoleName = roleName, PolicyArn = arn }, ct));
        foreach (var arn in have.Where(a => !desired.Contains(a)))
            await Telemetry.AwsCall("iam", "DetachRolePolicy", () => iam.DetachRolePolicyAsync(new DetachRolePolicyRequest { RoleName = roleName, PolicyArn = arn }, ct));
    }
}
