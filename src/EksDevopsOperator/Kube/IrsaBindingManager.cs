using System.Text.Json;
using EksDevopsOperator.Core;
using k8s.Autorest;
using k8s.Models;
using KubeOps.KubernetesClient;

namespace EksDevopsOperator.Kubernetes;

/// The IRSA binding: the eks.amazonaws.com/role-arn annotation on the ServiceAccount, which EKS's pod identity webhook turns into a
/// projected web identity token for pods. Only that one annotation is ever written; nothing else on the ServiceAccount is touched.
public sealed class IrsaBindingManager(IKubernetesClient client) : IBindingManager
{
    public const string RoleArnAnnotation = "eks.amazonaws.com/role-arn";

    public Mechanism Mechanism => Mechanism.Irsa;

    static string Id(string ns, string sa) => $"{ns}/{sa}";

    public async Task<ObservedAssociation> ReadAsync(string cluster, string ns, string sa, CancellationToken ct)
    {
        var account = await client.GetAsync<V1ServiceAccount>(sa, ns, ct);
        return account?.Metadata.Annotations is { } a && a.TryGetValue(RoleArnAnnotation, out var arn) && !string.IsNullOrEmpty(arn)
            ? new ObservedAssociation(true, Id(ns, sa), null, arn, new Dictionary<string, string>())
            : ObservedAssociation.Absent;
    }

    public async Task<string> EnsureAsync(string cluster, string ns, string sa, string roleArn, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        var account = await client.GetAsync<V1ServiceAccount>(sa, ns, ct)
            ?? throw new PodIdentityException(ErrorKind.Transient, "ServiceAccountNotFound",
                $"ServiceAccount {ns}/{sa} does not exist yet; the IRSA annotation is added as soon as it does");
        if (account.Metadata.Annotations?.TryGetValue(RoleArnAnnotation, out var current) == true && current == roleArn) return Id(ns, sa);
        await Patch(ns, sa, roleArn, ct);
        return Id(ns, sa);
    }

    public async Task DeleteAsync(string cluster, string ns, string sa, string bindingId, CancellationToken ct)
    {
        try { await Patch(ns, sa, null, ct); }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { } // ServiceAccount already gone
    }

    // JSON merge patch on one annotation key: a null value removes it, every other field and annotation is left alone
    Task Patch(string ns, string sa, string? roleArn, CancellationToken ct) =>
        client.PatchAsync<V1ServiceAccount>(
            new V1Patch(JsonSerializer.Serialize(new { metadata = new { annotations = new Dictionary<string, string?> { [RoleArnAnnotation] = roleArn } } }), V1Patch.PatchType.MergePatch),
            sa, ns, null, null, null, ct);
}
