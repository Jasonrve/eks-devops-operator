using EksDevopsOperator.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EksDevopsOperator.IntegrationTests;

// The whole engine against in-memory fakes: no Docker, no AWS. Covers Pod Identity and IRSA end to end, the Kubernetes events
// (as the "Changes" the controller turns into events) for every create, update, repair and delete, and the logs.
public class EngineUnitTests
{
    static string[] Reasons(ReconcileResult r) => EngineRig.Reasons(r);
    static FakeAws.FakeRole TheRole(EngineRig rig) => rig.Aws.Roles.Values.Single();

    // ------------------------------------------------------------------ Pod Identity

    [Fact] public async Task Pod_identity_creates_the_role_and_the_association()
    {
        var rig = new EngineRig();
        var r = await rig.Reconcile(EngineRig.Request());
        r.Phase.Should().Be(Phase.Ready, r.Message);
        r.Conditions.Select(c => c.Type).Should().Equal("Validated", "RoleReady", "AssociationReady", "Ready");
        Reasons(r).Should().Equal("RoleCreated", "PolicyCreated", "AssociationCreated");
        PolicyJson.Normalize(TheRole(rig).Trust).Should().Contain("pods.eks.amazonaws.com").And.NotContain("AssumeRoleWithWebIdentity");
        rig.PodIdentity.Items.Should().ContainKey(("payments", "api"));
        rig.Irsa.Items.Should().BeEmpty();
    }

    [Fact] public async Task A_converged_reconcile_changes_nothing_and_logs_nothing_above_debug()
    {
        var rig = new EngineRig();
        var req = EngineRig.Request();
        var first = await rig.Reconcile(req);
        rig.Log.Clear();
        var second = await rig.Reconcile(req, EngineRig.Prior(first, req));
        second.Phase.Should().Be(Phase.Ready);
        Reasons(second).Should().BeEmpty("a converged reconcile raises no events");
        rig.Log.Entries.Where(e => e.Level >= LogLevel.Information).Should().BeEmpty("routine checks must not spam the log");
        rig.Log.At(LogLevel.Debug).Should().Contain(m => m.StartsWith("in sync"));
    }

    // ------------------------------------------------------------------ IRSA

    [Fact] public async Task Irsa_creates_a_role_that_trusts_the_oidc_provider_for_exactly_this_service_account_and_annotates_it()
    {
        var rig = new EngineRig();
        var req = EngineRig.Request(Mechanism.Irsa);
        var r = await rig.Reconcile(req);
        r.Phase.Should().Be(Phase.Ready, r.Message);
        r.Conditions.Select(c => c.Type).Should().Equal("Validated", "RoleReady", "ServiceAccountReady", "Ready");
        Reasons(r).Should().Equal("RoleCreated", "PolicyCreated", "ServiceAccountAnnotationCreated");

        var trust = PolicyJson.Normalize(TheRole(rig).Trust);
        trust.Should().Contain("sts:AssumeRoleWithWebIdentity").And.Contain($"oidc-provider/{FakeOidc.Issuer}")
            .And.Contain($"{FakeOidc.Issuer}:sub").And.Contain("system:serviceaccount:payments:api")
            .And.Contain($"{FakeOidc.Issuer}:aud").And.Contain("sts.amazonaws.com")
            .And.NotContain("pods.eks.amazonaws.com").And.NotContain("\"*\"");
        rig.Irsa.Items[("payments", "api")].RoleArn.Should().Be(r.RoleArn);
        rig.PodIdentity.Items.Should().BeEmpty();
        TheRole(rig).Tags.Should().ContainKey(Tags.Namespace, "IRSA roles carry the same ownership tags and boundary");
        TheRole(rig).Boundary.Should().EndWith("standard-workload");
    }

    [Fact] public async Task Irsa_does_not_loop_because_an_annotation_has_no_tags()
    {
        var rig = new EngineRig();
        rig.Options.Tags["CostCenter"] = "cc-1";
        var req = EngineRig.Request(Mechanism.Irsa);
        var first = await rig.Reconcile(req);
        var second = await rig.Reconcile(req, EngineRig.Prior(first, req));
        second.Phase.Should().Be(Phase.Ready);
        Reasons(second).Should().BeEmpty("the planner must not keep re-applying tags to an annotation, which cannot hold them");
    }

    [Fact] public async Task Irsa_is_rejected_before_any_aws_change_when_it_is_not_enabled()
    {
        var rig = new EngineRig();
        rig.Options.IrsaEnabled = false;
        var r = await rig.Reconcile(EngineRig.Request(Mechanism.Irsa));
        r.Phase.Should().Be(Phase.Invalid);
        r.Message.Should().Contain("IrsaDisabled");
        rig.Aws.Roles.Should().BeEmpty();
        (await rig.Reconcile(EngineRig.Request(Mechanism.PodIdentity, ns: "other"))).Phase.Should().Be(Phase.Ready, "Pod Identity is unaffected");
    }

    [Theory]
    [InlineData("PodIdentity", Mechanism.Irsa, true)]
    [InlineData("IRSA", Mechanism.PodIdentity, true)]
    [InlineData("IRSA", Mechanism.Irsa, false)]
    [InlineData("irsa", Mechanism.Irsa, false)]
    [InlineData("*", Mechanism.Irsa, false)]
    public async Task A_policy_decides_which_mechanisms_teams_may_use(string allowed, Mechanism asked, bool rejected)
    {
        var rig = new EngineRig { Rules = [new PolicyRule { Name = "p", AllowedMechanisms = [allowed] }] };
        var r = await rig.Reconcile(EngineRig.Request(asked));
        if (rejected) { r.Phase.Should().Be(Phase.Invalid); r.Message.Should().Contain("MechanismNotAllowed"); rig.Aws.Roles.Should().BeEmpty(); }
        else r.Phase.Should().Be(Phase.Ready, r.Message);
    }

    [Fact] public async Task An_empty_mechanism_list_allows_neither()
    {
        var rig = new EngineRig { Rules = [new PolicyRule { Name = "p", AllowedMechanisms = [] }] };
        (await rig.Reconcile(EngineRig.Request(Mechanism.PodIdentity))).Message.Should().Contain("MechanismNotAllowed");
        (await rig.Reconcile(EngineRig.Request(Mechanism.Irsa))).Message.Should().Contain("MechanismNotAllowed");
    }

    [Fact] public async Task A_missing_oidc_provider_fails_with_guidance_and_creates_nothing()
    {
        var rig = new EngineRig();
        rig.Oidc.ProviderExists = false;
        var r = await rig.Reconcile(EngineRig.Request(Mechanism.Irsa));
        r.Phase.Should().Be(Phase.Failed);
        r.Conditions.Last().Reason.Should().Be("OidcProviderMissing");
        rig.Aws.Roles.Should().BeEmpty("the role is not created until its trust target exists");
        rig.Log.At(LogLevel.Warning).Should().Contain(m => m.Contains("OIDC provider"));
    }

    [Fact] public async Task A_service_account_that_does_not_exist_yet_is_retried_and_then_annotated()
    {
        var rig = new EngineRig();
        rig.Irsa.ServiceAccountExists = false;
        var req = EngineRig.Request(Mechanism.Irsa);
        var first = await rig.Reconcile(req);
        first.Phase.Should().Be(Phase.Failed);
        first.Error.Should().Be(ErrorKind.Transient, "it is retried with backoff, not given up on");
        first.Conditions.Last().Reason.Should().Be("ServiceAccountNotFound");
        rig.Aws.Roles.Should().HaveCount(1, "the role is ready and waits for the ServiceAccount");

        rig.Irsa.ServiceAccountExists = true;
        var second = await rig.Reconcile(req);
        second.Phase.Should().Be(Phase.Ready, second.Message);
        Reasons(second).Should().Contain("ServiceAccountAnnotationCreated");
    }

    [Fact] public async Task An_annotation_that_points_at_someone_elses_role_is_never_overwritten()
    {
        var rig = new EngineRig();
        rig.Aws.Roles["other"] = new FakeAws.FakeRole { Arn = "arn:aws:iam::111111111111:role/other", Path = "/" };
        rig.Irsa.Items[("payments", "api")] = ("payments/api", "arn:aws:iam::111111111111:role/other", new());
        var r = await rig.Reconcile(EngineRig.Request(Mechanism.Irsa));
        r.Phase.Should().Be(Phase.Failed);
        r.Conditions.Last().Reason.Should().Be("ServiceAccountAnnotationNotOwned");
        rig.Irsa.Items[("payments", "api")].RoleArn.Should().EndWith("role/other");
        rig.Aws.Roles.Should().HaveCount(1, "nothing was created either");
    }

    [Fact] public async Task A_removed_annotation_is_repaired_as_drift_with_a_warning_event()
    {
        var rig = new EngineRig();
        var req = EngineRig.Request(Mechanism.Irsa);
        var first = await rig.Reconcile(req);
        rig.Irsa.Items.Clear(); // someone removed the annotation
        rig.Log.Clear();
        var repaired = await rig.Reconcile(req, EngineRig.Prior(first, req));
        repaired.Phase.Should().Be(Phase.Ready);
        repaired.Changes.Should().ContainSingle(c => c.Reason == "ServiceAccountAnnotationRepaired" && c.Drift);
        rig.Log.At(LogLevel.Warning).Should().Contain(m => m.StartsWith("ServiceAccountAnnotationRepaired"));
    }

    // ------------------------------------------------------------------ events for every create, update, repair and delete

    [Fact] public async Task Every_kind_of_change_is_reported_for_a_kubernetes_event()
    {
        var rig = new EngineRig();
        var all = new HashSet<string>();
        async Task<ReconcileResult> Step(AccessRequest req, PriorState prior)
        {
            var r = await rig.Reconcile(req, prior);
            r.Phase.Should().Be(Phase.Ready, r.Message);
            foreach (var reason in Reasons(r)) all.Add(reason);
            return r;
        }

        var r1 = EngineRig.Request(generation: 1);
        var created = await Step(r1, new PriorState(null, null));
        Reasons(created).Should().Equal("RoleCreated", "PolicyCreated", "AssociationCreated");

        var r2 = r1 with { Generation = 2, Statements = [new PolicyStatement("W", "Allow", ["s3:PutObject"], ["arn:aws:s3:::bucket-payments/*"])] };
        Reasons(await Step(r2, EngineRig.Prior(created, r1))).Should().Equal("PolicyUpdated");

        var r3 = r2 with { Generation = 3, Tags = new Dictionary<string, string> { ["App"] = "x" } };
        Reasons(await Step(r3, EngineRig.Prior(created, r2))).Should().Contain(["RoleUpdated", "AssociationUpdated"]);

        var prior = EngineRig.Prior(created, r3);
        TheRole(rig).Inline.Clear();
        Reasons(await Step(r3, prior)).Should().Equal("PolicyRepaired");

        rig.PodIdentity.Items.Clear();
        Reasons(await Step(r3, prior)).Should().Equal("AssociationRepaired");

        TheRole(rig).Trust = "{}";
        Reasons(await Step(r3, prior)).Should().Equal("RoleRepaired");

        rig.Aws.Roles.Clear();
        Reasons(await Step(r3, prior)).Should().Equal("RoleRepaired", "PolicyRepaired");

        var r4 = r3 with { Generation = 4, RequestedRoleName = "renamed" };
        Reasons(await Step(r4, prior)).Should().Contain(["RoleCreated", "AssociationUpdated", "PreviousRoleDeleted"]);

        all.Should().Contain(["RoleCreated", "RoleUpdated", "RoleRepaired", "PolicyCreated", "PolicyUpdated", "PolicyRepaired",
            "AssociationCreated", "AssociationUpdated", "AssociationRepaired", "PreviousRoleDeleted"]);
    }

    [Fact] public async Task Only_drift_repairs_are_warnings()
    {
        var rig = new EngineRig();
        var req = EngineRig.Request();
        var first = await rig.Reconcile(req);
        first.Changes.Should().OnlyContain(c => !c.Drift);
        TheRole(rig).Inline.Clear();
        var repaired = await rig.Reconcile(req, EngineRig.Prior(first, req));
        repaired.Changes.Should().OnlyContain(c => c.Drift);
    }

    // ------------------------------------------------------------------ cleanup (delete)

    [Theory]
    [InlineData(Mechanism.PodIdentity, "association ")]
    [InlineData(Mechanism.Irsa, "serviceaccount-annotation payments/api")]
    public async Task Deleting_with_the_delete_policy_removes_the_binding_and_the_role(Mechanism m, string bindingLabel)
    {
        var rig = new EngineRig();
        var req = EngineRig.Request(m);
        var r = await rig.Reconcile(req);
        var c = await rig.Engine.CleanupAsync(new CleanupInput("payments", "api", "uid-1", null, DeletionPolicy.Delete, EngineRig.Prior(r, req), m), default);
        c.Done.Should().BeTrue(c.Message);
        c.Removed.Should().HaveCount(2).And.Contain(x => x.StartsWith(bindingLabel)).And.Contain(r.RoleArn!);
        c.Retained.Should().BeEmpty();
        rig.Aws.Roles.Should().BeEmpty();
        rig.PodIdentity.Items.Should().BeEmpty();
        rig.Irsa.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Mechanism.PodIdentity)]
    [InlineData(Mechanism.Irsa)]
    public async Task Deleting_with_the_retain_policy_removes_only_the_binding(Mechanism m)
    {
        var rig = new EngineRig();
        var req = EngineRig.Request(m);
        var r = await rig.Reconcile(req);
        var c = await rig.Engine.CleanupAsync(new CleanupInput("payments", "api", "uid-1", null, DeletionPolicy.Retain, EngineRig.Prior(r, req), m), default);
        c.Done.Should().BeTrue();
        c.Removed.Should().ContainSingle();
        c.Retained.Should().ContainSingle().Which.Should().Contain("retained");
        rig.Aws.Roles.Should().HaveCount(1);
        (m == Mechanism.Irsa ? rig.Irsa : rig.PodIdentity).Items.Should().BeEmpty("the binding is always removed, so the workload stops using the role");
    }

    [Fact] public async Task Deleting_leaves_a_binding_that_points_at_another_role_and_says_so()
    {
        var rig = new EngineRig();
        var req = EngineRig.Request(Mechanism.Irsa);
        var r = await rig.Reconcile(req);
        rig.Irsa.Items[("payments", "api")] = ("payments/api", "arn:aws:iam::111111111111:role/someone-else", new());
        rig.Log.Clear();
        var c = await rig.Engine.CleanupAsync(new CleanupInput("payments", "api", "uid-1", null, DeletionPolicy.Delete, EngineRig.Prior(r, req), Mechanism.Irsa), default);
        c.Retained.Should().Contain(x => x.Contains("not owned by this request"));
        rig.Irsa.Items.Should().ContainKey(("payments", "api"));
        rig.Log.At(LogLevel.Warning).Should().Contain(m => m.Contains("left") && m.Contains("alone"));
    }

    // ------------------------------------------------------------------ logs

    [Fact] public async Task Every_log_line_carries_the_identifiers_needed_to_follow_one_resource()
    {
        var rig = new EngineRig();
        await rig.Reconcile(EngineRig.Request(Mechanism.Irsa));
        rig.Log.Entries.Should().NotBeEmpty();
        foreach (var key in new[] { "uid", "namespace", "name", "serviceAccount", "mechanism", "attempt", "cluster" })
            rig.Log.Entries.Should().OnlyContain(e => e.Scope.ContainsKey(key), $"scope key '{key}'");
        rig.Log.Entries.Select(e => e.Scope["attempt"]).Distinct().Should().HaveCount(1, "one reconcile is one attempt id");
        rig.Log.Entries[0].Scope["mechanism"].Should().Be("IRSA");
    }

    [Fact] public async Task Creation_logs_each_change_at_information_and_a_summary()
    {
        var rig = new EngineRig();
        await rig.Reconcile(EngineRig.Request());
        var info = rig.Log.At(LogLevel.Information).ToList();
        info.Should().Contain(m => m.StartsWith("RoleCreated: created role")).And.Contain(m => m.StartsWith("PolicyCreated:")).And.Contain(m => m.StartsWith("AssociationCreated:"));
        info.Should().Contain(m => m.StartsWith("reconciled in") && m.Contains("3 change(s)"));
        rig.Log.At(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact] public async Task A_rejected_request_logs_a_warning_with_the_reason_and_changes_nothing()
    {
        var rig = new EngineRig { Rules = [new PolicyRule { Name = "p" }] };
        var bad = EngineRig.Request() with { Statements = [new PolicyStatement("B", "Allow", ["iam:*"], ["*"])] };
        var r = await rig.Reconcile(bad);
        r.Phase.Should().Be(Phase.Invalid);
        rig.Log.At(LogLevel.Warning).Should().ContainSingle(m => m.StartsWith("request rejected by the guardrails") && m.Contains("PrivilegeEscalation"));
        rig.Aws.Roles.Should().BeEmpty();
    }

    [Fact] public async Task A_conflict_and_a_refusal_are_logged_as_warnings()
    {
        var rig = new EngineRig();
        rig.Conflicts.Conflicts.Add("PodIdentityAccess/older");
        await rig.Reconcile(EngineRig.Request());
        rig.Log.At(LogLevel.Warning).Should().Contain(m => m.StartsWith("ownership conflict") && m.Contains("PodIdentityAccess/older"));

        rig.Conflicts.Conflicts.Clear();
        var req = EngineRig.Request();
        var name = Naming.RoleName(rig.Options, new ClusterContext("c1", "111111111111", "aws", "us-east-1", "dev"), req);
        rig.Aws.Roles[name] = new FakeAws.FakeRole { Arn = $"arn:aws:iam::111111111111:role/eks-devops-operator/{name}", Path = "/eks-devops-operator/" }; // exists, not ours
        rig.Log.Clear();
        await rig.Reconcile(req);
        rig.Log.At(LogLevel.Warning).Should().ContainSingle(m => m.StartsWith("reconcile failed (Permanent)") && m.Contains("not owned"));
    }

    [Fact] public async Task A_failure_is_logged_without_leaking_credentials()
    {
        var rig = new EngineRig();
        rig.Aws.FailOnRoleCreate = () => new InvalidOperationException("denied for AKIAABCDEFGHIJKLMNOP and aws_secret_access_key=abcdEFGH1234/xyz+");
        var r = await rig.Reconcile(EngineRig.Request());
        r.Phase.Should().Be(Phase.Failed);
        r.Message.Should().NotContain("AKIAABCDEFGHIJKLMNOP").And.NotContain("abcdEFGH1234");
        var warnings = rig.Log.At(LogLevel.Warning).ToList();
        warnings.Should().ContainSingle(m => m.StartsWith("reconcile failed (Transient)"));
        rig.Log.Entries.Select(e => e.Message).Should().NotContain(m => m.Contains("AKIAABCDEFGHIJKLMNOP") || m.Contains("abcdEFGH1234"), "no log line at any level may hold the secret");
    }

    [Fact] public async Task Cleanup_logs_what_it_removed_and_keeps_going_to_the_end()
    {
        var rig = new EngineRig();
        var req = EngineRig.Request(Mechanism.Irsa);
        var r = await rig.Reconcile(req);
        rig.Log.Clear();
        await rig.Engine.CleanupAsync(new CleanupInput("payments", "api", "uid-1", null, DeletionPolicy.Delete, EngineRig.Prior(r, req), Mechanism.Irsa), default);
        var info = rig.Log.At(LogLevel.Information).ToList();
        info.Should().Contain(m => m.StartsWith("cleaning up with deletionPolicy Delete"))
            .And.Contain(m => m.StartsWith("removed serviceaccount-annotation"))
            .And.Contain(m => m.StartsWith("deleted role"))
            .And.Contain(m => m.StartsWith("cleanup complete: 2 removed, 0 retained"));
    }
}

public class IrsaPlannerTests
{
    [Fact] public void The_planner_ignores_tags_on_an_irsa_binding_but_compares_them_for_pod_identity()
    {
        var tags = new Dictionary<string, string> { ["k"] = "v" };
        var role = new DesiredRole("r", "arn:aws:iam::111111111111:role/r", """{"Statement":[]}""", "p", null, [], null, tags, null);
        var observedRole = new ObservedRole(true, role.Arn, "/", """{"Statement":[]}""", tags, null, new Dictionary<string, string>(), []);
        var untagged = new ObservedAssociation(true, "ns/sa", null, role.Arn, new Dictionary<string, string>());
        var planner = new ReconciliationPlanner();
        DesiredAccess For(Mechanism m) => new(new AccessRequest { Namespace = "n", ObjectName = "x", Uid = "u", ServiceAccount = "sa", Mechanism = m }, new ClusterContext("c", "111111111111", "aws", "us-east-1", "dev"), role);

        planner.Plan(For(Mechanism.Irsa), new ObservedState(observedRole, untagged, null)).Converged.Should().BeTrue();
        planner.Plan(For(Mechanism.PodIdentity), new ObservedState(observedRole, untagged, null)).Steps.Should().ContainSingle(s => s.Kind == StepKind.EnsureBinding);
    }
}
