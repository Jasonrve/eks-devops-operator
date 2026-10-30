namespace EksDevopsOperator.Core;

public static class Tags
{
    public const string Operator = "pio:operator";
    public const string Cluster = "pio:cluster";
    public const string Namespace = "pio:namespace";
    public const string ServiceAccount = "pio:service-account";
    public const string Uid = "pio:uid";
    public const string Environment = "pio:environment";
}

public sealed class ResourceOwnershipService(OperatorOptions options) : IResourceOwnershipService
{
    public IReadOnlyDictionary<string, string> BuildTags(AccessRequest r, ClusterContext c)
    {
        var custom = TagRules.Merge(options.Tags, r.Tags);
        var tags = new Dictionary<string, string>(custom)
        {
            [Tags.Operator] = options.OperatorId, [Tags.Cluster] = c.ClusterName, [Tags.Namespace] = r.Namespace,
            [Tags.ServiceAccount] = r.ServiceAccount, [Tags.Uid] = r.Uid, [Tags.Environment] = c.Environment,
        };
        return TagRules.WithManagedKeys(tags, custom.Keys);
    }

    public Ownership Check(ObservedRole role, AccessRequest r, ClusterContext c)
    {
        if (!role.Exists) return Ownership.Absent;
        var ours = role.Tags.GetValueOrDefault(Tags.Operator) == options.OperatorId && role.Tags.GetValueOrDefault(Tags.Cluster) == c.ClusterName
            && role.Path == Arn.NormalizePath(options.IamPath);
        if (!ours) return Ownership.Foreign;
        return role.Tags.GetValueOrDefault(Tags.Namespace) == r.Namespace && role.Tags.GetValueOrDefault(Tags.ServiceAccount) == r.ServiceAccount
            ? Ownership.Owned : Ownership.OwnedByOtherWorkload;
    }
}
