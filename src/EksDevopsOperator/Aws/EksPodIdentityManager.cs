using Amazon.EKS.Model;
using EksDevopsOperator.Core;

namespace EksDevopsOperator.Aws;

/// The Pod Identity binding: an EKS association from namespace/ServiceAccount to the role.
public sealed class EksPodIdentityManager(AwsClients aws) : IBindingManager
{
    public Mechanism Mechanism => Mechanism.PodIdentity;

    public async Task<ObservedAssociation> ReadAsync(string cluster, string ns, string sa, CancellationToken ct)
    {
        var eks = aws.Eks;
        try
        {
            var list = await Telemetry.AwsCall("eks", "ListPodIdentityAssociations", () => eks.ListPodIdentityAssociationsAsync(new ListPodIdentityAssociationsRequest { ClusterName = cluster, Namespace = ns, ServiceAccount = sa }, ct));
            var summary = (list.Associations ?? []).FirstOrDefault(a => a.Namespace == ns && a.ServiceAccount == sa);
            if (summary is null) return ObservedAssociation.Absent;
            var a = (await Telemetry.AwsCall("eks", "DescribePodIdentityAssociation", () => eks.DescribePodIdentityAssociationAsync(new DescribePodIdentityAssociationRequest { ClusterName = cluster, AssociationId = summary.AssociationId }, ct))).Association;
            return new ObservedAssociation(true, a.AssociationId, a.AssociationArn, a.RoleArn, new Dictionary<string, string>(a.Tags ?? []));
        }
        catch (ResourceNotFoundException) { return ObservedAssociation.Absent; }
    }

    public async Task<string> EnsureAsync(string cluster, string ns, string sa, string roleArn, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        var eks = aws.Eks;
        var cur = await ReadAsync(cluster, ns, sa, ct);
        if (!cur.Exists)
        {
            var token = Naming.Hash(cluster, ns, sa, roleArn); // idempotent retries
            var r = await Telemetry.AwsCall("eks", "CreatePodIdentityAssociation", () => eks.CreatePodIdentityAssociationAsync(new CreatePodIdentityAssociationRequest
            {
                ClusterName = cluster, Namespace = ns, ServiceAccount = sa, RoleArn = roleArn, ClientRequestToken = token, Tags = new Dictionary<string, string>(tags),
            }, ct));
            return r.Association.AssociationId;
        }
        if (cur.RoleArn != roleArn)
            await Telemetry.AwsCall("eks", "UpdatePodIdentityAssociation", () => eks.UpdatePodIdentityAssociationAsync(new UpdatePodIdentityAssociationRequest { ClusterName = cluster, AssociationId = cur.Id, RoleArn = roleArn }, ct));

        var changed = tags.Where(t => !cur.Tags.TryGetValue(t.Key, out var v) || v != t.Value).ToDictionary(t => t.Key, t => t.Value);
        var stale = TagRules.Stale(cur.Tags, tags); // only keys the operator applied earlier
        if (changed.Count == 0 && stale.Count == 0) return cur.Id!;
        try
        {
            if (changed.Count > 0) await Telemetry.AwsCall("eks", "TagResource", () => eks.TagResourceAsync(new Amazon.EKS.Model.TagResourceRequest { ResourceArn = cur.Arn, Tags = changed }, ct));
            if (stale.Count > 0) await Telemetry.AwsCall("eks", "UntagResource", () => eks.UntagResourceAsync(new Amazon.EKS.Model.UntagResourceRequest { ResourceArn = cur.Arn, TagKeys = stale }, ct));
        }
        catch (Exception e) when (IsNotFound(e))
        {
            // The association cannot be tagged in place (the emulator does not support tagging associations, and in AWS the association may
            // have just been deleted), so recreate it with the desired tags. The role is unchanged, so only new pods briefly see the gap.
            await DeleteAsync(cluster, ns, sa, cur.Id!, ct);
            return await EnsureAsync(cluster, ns, sa, roleArn, tags, ct);
        }
        return cur.Id!;
    }

    // AWS throws a typed NotFoundException; the emulator reports the same condition with only the error code
    static bool IsNotFound(Exception e) => e is ResourceNotFoundException or NotFoundException || (e is Amazon.Runtime.AmazonServiceException a && a.ErrorCode is "ResourceNotFoundException" or "NotFoundException");

    public async Task DeleteAsync(string cluster, string ns, string sa, string associationId, CancellationToken ct)
    {
        try { await Telemetry.AwsCall("eks", "DeletePodIdentityAssociation", () => aws.Eks.DeletePodIdentityAssociationAsync(new DeletePodIdentityAssociationRequest { ClusterName = cluster, AssociationId = associationId }, ct)); }
        catch (ResourceNotFoundException) { }
    }
}

public sealed class BindingResolver(IEnumerable<IBindingManager> all) : IBindingResolver
{
    public IBindingManager For(Mechanism mechanism) => all.First(b => b.Mechanism == mechanism);
}
