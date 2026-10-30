using EksDevopsOperator.Aws;
using EksDevopsOperator.Core;
using EksDevopsOperator.Policy;
using Microsoft.Extensions.Logging;

namespace EksDevopsOperator.IntegrationTests;

// In-memory stand-ins for AWS and the cluster, so the whole engine can be tested (logs, events, IRSA, switching) with no Docker.
// They mirror what the real managers do (including tag bookkeeping), and the Floci tests cover the real SDK calls.

/// IAM roles and inline/managed policies of one account.
public sealed class FakeAws : IIamRoleManager, IIamPolicyManager, IAwsAccountResolver
{
    public sealed class FakeRole
    {
        public string Arn = "", Path = "/", Trust = "", Boundary = "";
        public Dictionary<string, string> Tags = new();
        public Dictionary<string, string> Inline = new();
        public List<string> Attached = [];
    }

    public string Account { get; set; } = "111111111111";
    public Dictionary<string, FakeRole> Roles { get; } = new();
    public Func<Exception?>? FailOnRoleCreate { get; set; }

    public Task<string> GetClusterAccountIdAsync(CancellationToken ct) => Task.FromResult(Account);

    public Task<ObservedRole> ReadAsync(string roleName, CancellationToken ct) =>
        Task.FromResult(!Roles.TryGetValue(roleName, out var r) ? ObservedRole.Absent
            : new ObservedRole(true, r.Arn, r.Path, r.Trust, new Dictionary<string, string>(r.Tags), r.Boundary.Length == 0 ? null : r.Boundary, new Dictionary<string, string>(r.Inline), r.Attached.ToList()));

    public Task EnsureRoleAsync(DesiredRole want, ObservedRole current, CancellationToken ct)
    {
        if (!Roles.TryGetValue(want.Name, out var r))
        {
            if (FailOnRoleCreate?.Invoke() is { } e) throw e;
            var path = want.Arn[(want.Arn.IndexOf(":role", StringComparison.Ordinal) + 5)..];
            Roles[want.Name] = r = new FakeRole { Arn = want.Arn, Path = path[..(path.LastIndexOf('/') + 1)], Tags = new() };
        }
        r.Trust = want.TrustPolicy;
        r.Boundary = want.PermissionsBoundaryArn ?? "";
        foreach (var k in TagRules.Stale(r.Tags, want.Tags)) r.Tags.Remove(k); // only keys the operator applied earlier
        foreach (var (k, v) in want.Tags) r.Tags[k] = v;
        return Task.CompletedTask;
    }

    public Task DeleteRoleAsync(string roleName, CancellationToken ct) { Roles.Remove(roleName); return Task.CompletedTask; }

    public Task EnsureInlinePolicyAsync(string roleName, string policyName, string? document, CancellationToken ct)
    {
        if (document is null) Roles[roleName].Inline.Remove(policyName); else Roles[roleName].Inline[policyName] = document;
        return Task.CompletedTask;
    }

    public Task EnsureAttachedPoliciesAsync(string roleName, IReadOnlyList<string> desiredArns, CancellationToken ct)
    {
        Roles[roleName].Attached = desiredArns.ToList();
        return Task.CompletedTask;
    }
}

/// A Pod Identity associations store, or (for Mechanism.Irsa) the annotations of ServiceAccounts.
public sealed class FakeBinding(Mechanism mechanism) : IBindingManager
{
    public Mechanism Mechanism => mechanism;
    public Dictionary<(string Ns, string Sa), (string Id, string RoleArn, Dictionary<string, string> Tags)> Items { get; } = new();
    /// IRSA only: a ServiceAccount must exist before it can be annotated.
    public bool ServiceAccountExists { get; set; } = true;
    int _next;

    public Task<ObservedAssociation> ReadAsync(string cluster, string ns, string sa, CancellationToken ct) =>
        Task.FromResult(Items.TryGetValue((ns, sa), out var i) ? new ObservedAssociation(true, i.Id, $"arn:binding/{i.Id}", i.RoleArn, new Dictionary<string, string>(i.Tags)) : ObservedAssociation.Absent);

    public Task<string> EnsureAsync(string cluster, string ns, string sa, string roleArn, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        if (mechanism == Mechanism.Irsa && !ServiceAccountExists)
            throw new PodIdentityException(ErrorKind.Transient, "ServiceAccountNotFound", $"ServiceAccount {ns}/{sa} does not exist yet");
        var id = Items.TryGetValue((ns, sa), out var existing) ? existing.Id : mechanism == Mechanism.Irsa ? $"{ns}/{sa}" : $"a-{++_next}";
        Items[(ns, sa)] = (id, roleArn, mechanism == Mechanism.Irsa ? new() : new Dictionary<string, string>(tags)); // an annotation has no tags
        return Task.FromResult(id);
    }

    public Task DeleteAsync(string cluster, string ns, string sa, string bindingId, CancellationToken ct) { Items.Remove((ns, sa)); return Task.CompletedTask; }
}

public sealed class FakeOidc : IOidcResolver
{
    public const string Issuer = "oidc.eks.us-east-1.amazonaws.com/id/ABCDEF123456";
    public bool ProviderExists { get; set; } = true;

    public Task<OidcInfo> ResolveAsync(ClusterContext cluster, CancellationToken ct) =>
        ProviderExists
            ? Task.FromResult(new OidcInfo(Issuer, $"arn:aws:iam::{cluster.AccountId}:oidc-provider/{Issuer}"))
            : throw new PodIdentityException(ErrorKind.Permanent, "OidcProviderMissing", $"the IAM OIDC provider for {Issuer} does not exist");
}

/// Keeps every log line with its level and the scope values (uid, namespace, ...) that were active when it was written.
public sealed class CapturingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, IReadOnlyDictionary<string, object> Scope);

    readonly List<Dictionary<string, object>> _scopes = [];
    readonly object _gate = new();
    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        var d = new Dictionary<string, object>();
        if (state is IEnumerable<KeyValuePair<string, object>> kv) foreach (var (k, v) in kv) d[k] = v;
        lock (_gate) _scopes.Add(d);
        return new Pop(this, d);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            var scope = new Dictionary<string, object>();
            foreach (var s in _scopes) foreach (var (k, v) in s) scope[k] = v;
            Entries.Add(new Entry(level, formatter(state, exception), scope));
        }
    }

    public IEnumerable<string> At(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message);
    public void Clear() { lock (_gate) Entries.Clear(); }

    sealed class Pop(CapturingLogger<T> owner, Dictionary<string, object> scope) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._scopes.Remove(scope); }
    }
}

/// The real engine wired to the fakes.
public sealed class EngineRig
{
    public OperatorOptions Options { get; } = new() { ClusterName = "c1", IrsaEnabled = true, ConsistencyTimeout = TimeSpan.FromSeconds(1) };
    public FakeAws Aws { get; } = new();
    public FakeBinding PodIdentity { get; } = new(Mechanism.PodIdentity);
    public FakeBinding Irsa { get; } = new(Mechanism.Irsa);
    public FakeOidc Oidc { get; } = new();
    public List<PolicyRule> Rules { get; set; } = [new() { Name = "default" }];
    public StaticConflictDetector Conflicts { get; } = new();
    public CapturingLogger<AccessEngine> Log { get; } = new();
    public AccessEngine Engine { get; }

    public EngineRig()
    {
        Engine = new AccessEngine(Options, Aws, new StaticPolicySource(() => Rules), new PolicyValidator(Options), Conflicts, new ResourceOwnershipService(Options), new ReconciliationPlanner(),
            Aws, Aws, new BindingResolver([PodIdentity, Irsa]), Oidc, new AwsErrorClassifier(), TimeProvider.System, Log);
    }

    public static AccessRequest Request(Mechanism m = Mechanism.PodIdentity, string ns = "payments", string sa = "api", long generation = 1, string uid = "uid-1") => new()
    {
        Namespace = ns, ObjectName = "req", Uid = uid, ServiceAccount = sa, Generation = generation, Mechanism = m,
        Statements = [new PolicyStatement("Read", "Allow", ["s3:GetObject"], [$"arn:aws:s3:::bucket-{ns}/*"])],
    };

    public Task<ReconcileResult> Reconcile(AccessRequest r, PriorState? prior = null) => Engine.ReconcileAsync(r, prior ?? new PriorState(null, null), null, CancellationToken.None);

    /// What the controller would remember in status after a successful reconcile.
    public static PriorState Prior(ReconcileResult r, AccessRequest req) => new(r.RoleArn, r.AssociationId, req.Generation);

    public static string[] Reasons(ReconcileResult r) => (r.Changes ?? []).Select(c => c.Reason).ToArray();
}
