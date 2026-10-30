using System.Text.Json;
using EksDevopsOperator.Core;
using k8s;
using k8s.Models;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Entities;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EksDevopsOperator.Kubernetes;

/// What a team may put on a ServiceAccount. Annotations are untrusted input: they only choose a kind, a template and resource ARNs,
/// and become an ordinary PodIdentityAccess or IrsaAccess that goes through the same guardrails and engine as one written by hand.
public static class SaAnnotations
{
    public const string Prefix = Api.Group + "/";
    public const string Enabled = Prefix + "enabled";
    public const string Mechanism = Prefix + "mechanism";          // PodIdentity (default) | IRSA
    public const string PolicyTemplate = Prefix + "policy-template";
    public const string ResourceArns = Prefix + "resource-arns";   // comma separated
    public const string RoleName = Prefix + "role-name";
    public const string DeletionPolicy = Prefix + "deletion-policy";
    public const string Tags = Prefix + "tags";                    // Key=Value,Key2=Value2
    // written by the operator, never read back
    public const string Status = Prefix + "status";
    public const string StatusRoleArn = Prefix + "role-arn";
    public const string StatusMessage = Prefix + "message";

    public const string GeneratedLabel = Prefix + "generated-from";

    // template -> (actions, suffixes added to every ARN: a bucket also needs its objects, a table its indexes)
    static readonly Dictionary<string, (string[] Actions, string[] Suffixes)> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["s3-read"] = (["s3:GetObject", "s3:GetObjectVersion", "s3:ListBucket"], ["/*"]),
        ["s3-readwrite"] = (["s3:GetObject", "s3:GetObjectVersion", "s3:ListBucket", "s3:PutObject", "s3:DeleteObject", "s3:AbortMultipartUpload"], ["/*"]),
        ["sqs-consume"] = (["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes", "sqs:GetQueueUrl"], []),
        ["sqs-send"] = (["sqs:SendMessage", "sqs:GetQueueAttributes", "sqs:GetQueueUrl"], []),
        ["sns-publish"] = (["sns:Publish"], []),
        ["dynamodb-read"] = (["dynamodb:GetItem", "dynamodb:BatchGetItem", "dynamodb:Query", "dynamodb:Scan", "dynamodb:DescribeTable"], ["/index/*"]),
        ["dynamodb-readwrite"] = (["dynamodb:GetItem", "dynamodb:BatchGetItem", "dynamodb:Query", "dynamodb:Scan", "dynamodb:DescribeTable", "dynamodb:PutItem", "dynamodb:UpdateItem", "dynamodb:DeleteItem", "dynamodb:BatchWriteItem"], ["/index/*"]),
        ["secretsmanager-read"] = (["secretsmanager:GetSecretValue", "secretsmanager:DescribeSecret"], []),
        ["ssm-parameter-read"] = (["ssm:GetParameter", "ssm:GetParameters", "ssm:GetParametersByPath"], []),
    };

    public static IReadOnlyCollection<string> TemplateNames => Templates.Keys;

    public static bool IsEnabled(V1ServiceAccount sa) => sa.Metadata.Annotations is { } an && an.TryGetValue(Enabled, out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// The kind and spec the annotations ask for, or why they cannot be turned into one.
    public static (AccessSpec? Spec, string? Error) Parse(V1ServiceAccount sa)
    {
        var a = sa.Metadata.Annotations ?? new Dictionary<string, string>();
        string? Get(string k) => a.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var mech = Get(Mechanism) ?? "PodIdentity";
        AccessSpec spec = mech.ToUpperInvariant() switch
        {
            "PODIDENTITY" => new PodIdentityAccessSpec(),
            "IRSA" => new IrsaAccessSpec(),
            _ => null!,
        };
        if (spec is null) return (null, $"{Mechanism} must be PodIdentity or IRSA, not '{mech}'");

        var template = Get(PolicyTemplate);
        if (template is null) return (null, $"{PolicyTemplate} is required (one of: {string.Join(", ", Templates.Keys.Order())})");
        if (!Templates.TryGetValue(template, out var t)) return (null, $"unknown {PolicyTemplate} '{template}' (one of: {string.Join(", ", Templates.Keys.Order())})");

        var arns = (Get(ResourceArns) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (arns.Length == 0) return (null, $"{ResourceArns} is required: a comma separated list of ARNs");
        var resources = arns.SelectMany(arn => new[] { arn }.Concat(t.Suffixes.Where(_ => !arn.EndsWith('*')).Select(s => arn + s))).Distinct().ToList();

        spec.ServiceAccountName = sa.Name();
        spec.RoleName = Get(RoleName);
        spec.Statements = [new StatementSpec { Sid = "Template", Actions = [.. t.Actions], Resources = resources }];
        if (Get(DeletionPolicy) is { } dp) spec.DeletionPolicy = dp;
        if (Get(Tags) is { } tags)
            foreach (var pair in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2 || kv[0].Length == 0) return (null, $"{Tags} must look like Key=Value,Key2=Value2");
                spec.Tags[kv[0]] = kv[1];
            }
        return (spec, null);
    }

    public static string CrName(string serviceAccount) => $"sa-{serviceAccount}"; // shortcut: names near the 253 limit are not shortened, use short ServiceAccount names
}

/// Turns annotated ServiceAccounts into PodIdentityAccess / IrsaAccess resources, and keeps them in step: edit the annotations and the resource
/// follows, remove them (or delete the ServiceAccount) and it is deleted, which runs the normal cleanup. The status is mirrored back as annotations.
/// It polls instead of watching: a pass is cheap, idempotent, and cannot miss an event; every replica may run it.
public sealed class ServiceAccountSync(IKubernetesClient client, EventPublisher events, OperatorOptions opt, string? watchNamespace, ILogger<ServiceAccountSync> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        log.LogInformation("ServiceAccount annotations are enabled: ServiceAccounts annotated {Annotation}=true create access resources (every {Interval})", SaAnnotations.Enabled, opt.ServiceAccountSyncInterval);
        using var timer = new PeriodicTimer(opt.ServiceAccountSyncInterval);
        do
        {
            try { await SyncOnceAsync(stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("ServiceAccount sync failed, retrying next pass: {Error}", e.Message); }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    public async Task SyncOnceAsync(CancellationToken ct)
    {
        var accounts = (await client.ListAsync<V1ServiceAccount>(watchNamespace, null, null, ct)).Where(SaAnnotations.IsEnabled).ToDictionary(s => (s.Namespace()!, s.Name()));
        var pia = (await client.ListAsync<V1Alpha1PodIdentityAccess>(watchNamespace, SaAnnotations.GeneratedLabel, null, ct)).ToDictionary(c => (c.Namespace()!, c.Name()));
        var irsa = (await client.ListAsync<V1Alpha1IrsaAccess>(watchNamespace, SaAnnotations.GeneratedLabel, null, ct)).ToDictionary(c => (c.Namespace()!, c.Name()));

        foreach (var sa in accounts.Values)
        {
            try { await SyncAccountAsync(sa, pia, irsa, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("ServiceAccount {Namespace}/{Name}: sync failed: {Error}", sa.Namespace(), sa.Name(), e.Message); }
        }

        // generated resources whose ServiceAccount lost its annotations (a deleted ServiceAccount is handled by garbage collection through the owner reference)
        foreach (var c in pia.Values.Where(c => !accounts.ContainsKey((c.Namespace()!, c.Spec.ServiceAccountName)))) await RemoveAsync(c, ct);
        foreach (var c in irsa.Values.Where(c => !accounts.ContainsKey((c.Namespace()!, c.Spec.ServiceAccountName)))) await RemoveAsync(c, ct);
    }

    async Task SyncAccountAsync(V1ServiceAccount sa, Dictionary<(string, string), V1Alpha1PodIdentityAccess> pia, Dictionary<(string, string), V1Alpha1IrsaAccess> irsa, CancellationToken ct)
    {
        var ns = sa.Namespace()!; var key = (ns, SaAnnotations.CrName(sa.Name()));
        var (spec, error) = SaAnnotations.Parse(sa);
        if (spec is null)
        {
            log.LogWarning("ServiceAccount {Namespace}/{Name}: annotations rejected: {Error}", ns, sa.Name(), error);
            await Note(sa, "AnnotationsInvalid", error!, true, ct);
            await Mirror(sa, "Invalid", null, error, ct);
            return;
        }

        // switching kind: remove the old one first (its cleanup runs), create the new one on a later pass, so the two never fight over one role
        if (spec is PodIdentityAccessSpec && irsa.TryGetValue(key, out var oldIrsa)) { await RemoveAsync(oldIrsa, ct); return; }
        if (spec is IrsaAccessSpec && pia.TryGetValue(key, out var oldPia)) { await RemoveAsync(oldPia, ct); return; }

        var owner = new V1OwnerReference { ApiVersion = "v1", Kind = "ServiceAccount", Name = sa.Name(), Uid = sa.Uid() };
        var (phase, roleArn, message) = spec switch
        {
            PodIdentityAccessSpec p => await Ensure<V1Alpha1PodIdentityAccess, PodIdentityAccessSpec>(sa, key, pia, p, owner, ct),
            IrsaAccessSpec i => await Ensure<V1Alpha1IrsaAccess, IrsaAccessSpec>(sa, key, irsa, i, owner, ct),
            _ => throw new NotSupportedException(),
        };
        if (phase is not null) await Mirror(sa, phase, roleArn, message, ct);
    }

    async Task<(string? Phase, string? RoleArn, string? Message)> Ensure<TEntity, TSpec>(V1ServiceAccount sa, (string, string) key, Dictionary<(string, string), TEntity> existing, TSpec desired, V1OwnerReference owner, CancellationToken ct)
        where TEntity : CustomKubernetesEntity<TSpec, AccessStatus>, new() where TSpec : AccessSpec, new()
    {
        var ns = key.Item1; var kind = new TEntity().Kind;
        if (!existing.TryGetValue(key, out var cr))
        {
            if (await client.GetAsync<TEntity>(key.Item2, ns, ct) is not null) // same name, but not ours: never overwrite what somebody wrote
            {
                var text = $"{kind}/{key.Item2} already exists and was not created from this ServiceAccount; remove or rename it";
                log.LogWarning("ServiceAccount {Namespace}/{Name}: {Message}", ns, sa.Name(), text);
                await Note(sa, "GeneratedResourceConflict", text, true, ct);
                return ("Conflict", null, text);
            }
            var created = new TEntity { Metadata = new V1ObjectMeta { Name = key.Item2, NamespaceProperty = ns, Labels = new Dictionary<string, string> { [SaAnnotations.GeneratedLabel] = "serviceaccount" }, OwnerReferences = [owner] }, Spec = desired };
            await client.CreateAsync(created, ct);
            log.LogInformation("ServiceAccount {Namespace}/{Name}: created {Kind}/{Resource} from its annotations", ns, sa.Name(), kind, key.Item2);
            await Note(sa, "AccessResourceCreated", $"created {kind}/{key.Item2} from the {SaAnnotations.Prefix} annotations", false, ct);
            return ("Pending", null, null);
        }

        if (JsonSerializer.Serialize(cr.Spec) != JsonSerializer.Serialize(desired))
        {
            cr.Spec = desired;
            await client.UpdateAsync(cr, ct);
            log.LogInformation("ServiceAccount {Namespace}/{Name}: updated {Kind}/{Resource} to match its annotations", ns, sa.Name(), kind, key.Item2);
            await Note(sa, "AccessResourceUpdated", $"updated {kind}/{key.Item2} to match the annotations", false, ct);
            return ("Pending", cr.Status.RoleArn, null);
        }
        return (string.IsNullOrEmpty(cr.Status.Phase) ? "Pending" : cr.Status.Phase, cr.Status.RoleArn, cr.Status.Message);
    }

    async Task RemoveAsync<TEntity>(TEntity cr, CancellationToken ct) where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        try
        {
            await client.DeleteAsync(cr, ct);
            log.LogInformation("deleted generated {Kind} {Namespace}/{Name}: its ServiceAccount no longer asks for it", cr.Kind, cr.Namespace(), cr.Name());
        }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { }
    }

    // the status the team can read on their own ServiceAccount; a merge patch touching only these keys, and only when something changed
    async Task Mirror(V1ServiceAccount sa, string phase, string? roleArn, string? message, CancellationToken ct)
    {
        var have = sa.Metadata.Annotations ?? new Dictionary<string, string>();
        var want = new Dictionary<string, string?>
        {
            [SaAnnotations.Status] = phase, [SaAnnotations.StatusRoleArn] = roleArn, [SaAnnotations.StatusMessage] = message is null ? null : Redactor.Redact(message),
        };
        if (want.All(w => (have.TryGetValue(w.Key, out var h) ? h : null) == w.Value)) return;
        var patch = JsonSerializer.Serialize(new { metadata = new { annotations = want } });
        await client.PatchAsync<V1ServiceAccount>(new V1Patch(patch, V1Patch.PatchType.MergePatch), sa.Name(), sa.Namespace(), null, null, null, ct);
    }

    async Task Note(V1ServiceAccount sa, string reason, string message, bool warning, CancellationToken ct)
    {
        try { await events(sa, reason, Redactor.Redact(message), warning ? EventType.Warning : EventType.Normal, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogDebug("event {Reason} was not recorded: {Error}", reason, e.Message); }
    }
}
