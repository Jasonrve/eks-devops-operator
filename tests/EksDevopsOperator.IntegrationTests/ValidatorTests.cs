using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using EksDevopsOperator.Core;
using EksDevopsOperator.Kubernetes;
using EksDevopsOperator.Policy;

namespace EksDevopsOperator.IntegrationTests;

// Pure logic, no AWS or Kubernetes needed.
public class ValidatorTests
{
    static readonly ClusterContext Ctx = new("c1", "111111111111", "aws", "us-east-1", "dev");

    static PolicyRule Rule(Func<PolicyRule, PolicyRule>? tweak = null)
    {
        var rule = new PolicyRule { Name = "p", AllowedServices = ["s3"], AllowedResourceArnPatterns = ["arn:aws:s3:::payments-*"] };
        return tweak?.Invoke(rule) ?? rule;
    }

    static AccessRequest Req(string[]? actions = null, string[]? resources = null, string? roleName = null, Dictionary<string, string>? tags = null) => new()
    {
        Namespace = "payments", ObjectName = "x", Uid = "u", ServiceAccount = "api", RequestedRoleName = roleName, Tags = tags ?? new(),
        Statements = [new PolicyStatement("S", "Allow", actions ?? ["s3:GetObject"], resources ?? ["arn:aws:s3:::payments-a/*"])],
    };

    static ValidationResult Run(AccessRequest r, PolicyRule? rule = null, OperatorOptions? o = null) =>
        new PolicyValidator(o ?? new OperatorOptions()).Validate(r, [rule ?? Rule()], Ctx);

    [Fact] public void Valid_request_passes() => Run(Req()).IsValid.Should().BeTrue();

    [Fact] public void No_governing_policy_means_deny() =>
        new PolicyValidator(new OperatorOptions()).Validate(Req(), [], Ctx).Issues.Should().Contain(i => i.Code == "NamespaceNotAuthorized");

    [Theory]
    [InlineData("iam:*", "PrivilegeEscalation")]
    [InlineData("iam:PassRole", "PrivilegeEscalation")]
    [InlineData("sts:AssumeRole", "PrivilegeEscalation")]
    [InlineData("*", "InvalidAction")]
    [InlineData("s3:*", "WildcardAction")]
    [InlineData("ec2:RunInstances", "ServiceNotAllowed")]
    [InlineData("s3:PutBucketPolicy", "PrivilegeEscalation")]
    public void Dangerous_actions_are_rejected(string action, string code) =>
        Run(Req(actions: [action])).Issues.Should().Contain(i => i.Code == code);

    [Theory]
    [InlineData("*", "WildcardResource")]
    [InlineData("arn:aws:s3:::*", "ResourceOutOfScope")]
    [InlineData("arn:aws:s3:::other-bucket", "ResourceOutOfScope")]
    [InlineData("arn:aws-cn:s3:::payments-a", "PartitionMismatch")]
    [InlineData("not-an-arn", "InvalidResource")]
    public void Resources_outside_scope_are_rejected(string resource, string code) =>
        Run(Req(resources: [resource])).Issues.Should().Contain(i => i.Code == code);

    [Fact] public void Resources_in_other_accounts_are_allowed_when_they_match_the_policy() =>
        Run(Req(actions: ["sqs:SendMessage"], resources: ["arn:aws:sqs:eu-west-1:999999999999:payments-queue"]), Rule(p => p with { AllowedServices = ["sqs"], AllowedResourceArnPatterns = ["arn:aws:sqs:*:999999999999:payments-*"] }), null)
            .IsValid.Should().BeTrue();

    [Theory]
    [InlineData("AdminRole")]
    [InlineData("Bad_Name")]
    [InlineData("aws-service")]
    public void Protected_or_malformed_role_names_are_rejected(string name) => Run(Req(roleName: name)).IsValid.Should().BeFalse();

    [Fact] public void Deletion_policy_not_allowed_is_rejected() =>
        Run(Req() with { DeletionPolicy = DeletionPolicy.Retain }, Rule(p => p with { AllowedDeletionPolicies = [DeletionPolicy.Delete] })).Issues.Should().Contain(i => i.Code == "DeletionPolicyNotAllowed");

    [Fact] public void Allowed_action_list_restricts_to_the_listed_actions()
    {
        var rule = Rule(p => p with { AllowedActions = ["s3:Get*", "s3:ListBucket"] });
        Run(Req(actions: ["s3:GetObject", "s3:ListBucket"]), rule).IsValid.Should().BeTrue();
        Run(Req(actions: ["s3:PutObject"]), rule).Issues.Should().Contain(i => i.Code == "ActionNotAllowed");
    }

    [Fact] public void Denied_actions_win_over_allowed_ones()
    {
        var rule = Rule(p => p with { AllowedActions = ["s3:*"], AllowWildcardActions = true, DeniedActions = ["s3:Delete*"] });
        Run(Req(actions: ["s3:GetObject"]), rule).IsValid.Should().BeTrue();
        Run(Req(actions: ["s3:DeleteObject"]), rule).Issues.Should().Contain(i => i.Code == "ActionDenied");
    }

    // ---- tags

    [Fact] public void Request_tags_are_accepted_when_valid() => Run(Req(tags: new() { ["App"] = "payments", ["cost-center"] = "42" })).IsValid.Should().BeTrue();

    [Theory]
    [InlineData("pio:uid", "ReservedTag")]
    [InlineData("aws:createdBy", "ReservedTag")]
    [InlineData("bad<key>", "InvalidTag")]
    [InlineData("has space", "InvalidTag")]
    public void Reserved_or_invalid_request_tags_are_rejected(string key, string code) =>
        Run(Req(tags: new() { [key] = "v" })).Issues.Should().Contain(i => i.Code == code);

    [Fact] public void Too_many_request_tags_are_rejected() =>
        Run(Req(tags: Enumerable.Range(0, 11).ToDictionary(i => $"k{i}", _ => "v"))).Issues.Should().Contain(i => i.Code == "TooManyTags");

    [Fact] public void Request_cannot_override_a_global_tag() =>
        Run(Req(tags: new() { ["CostCenter"] = "mine" }), null, new OperatorOptions { Tags = new() { ["CostCenter"] = "cc-1" } }).Issues.Should().Contain(i => i.Code == "TagConflict");

    [Fact] public void Tag_keys_that_do_not_fit_the_bookkeeping_tag_are_rejected() =>
        Run(Req(tags: Enumerable.Range(0, 10).ToDictionary(i => new string((char)('a' + i), 40), _ => "v"))).Issues.Should().Contain(i => i.Code == "TagKeysTooLong");
}

public class TagRuleTests
{
    [Fact] public void Stale_only_lists_keys_the_operator_applied_earlier()
    {
        var observed = new Dictionary<string, string> { [TagRules.ManagedKeysTag] = "A+B", ["A"] = "1", ["B"] = "2", ["foreign"] = "x" };
        TagRules.Stale(observed, new Dictionary<string, string> { ["A"] = "1", [TagRules.ManagedKeysTag] = "A" }).Should().Equal("B");
        TagRules.Stale(observed, new Dictionary<string, string>()).Should().BeEquivalentTo(["A", "B", TagRules.ManagedKeysTag]);
        TagRules.Stale(new Dictionary<string, string> { ["foreign"] = "x" }, new Dictionary<string, string>()).Should().BeEmpty();
    }

    [Fact] public void Differs_detects_missing_changed_and_stale_tags()
    {
        var want = new Dictionary<string, string> { ["A"] = "1" };
        TagRules.Differs(new Dictionary<string, string> { ["A"] = "1" }, want).Should().BeFalse();
        TagRules.Differs(new Dictionary<string, string> { ["A"] = "2" }, want).Should().BeTrue();
        TagRules.Differs(new Dictionary<string, string>(), want).Should().BeTrue();
        TagRules.Differs(new Dictionary<string, string> { ["A"] = "1", [TagRules.ManagedKeysTag] = "Z", ["Z"] = "9" }, want).Should().BeTrue();
        TagRules.Differs(new Dictionary<string, string> { ["A"] = "1", ["extra-from-someone-else"] = "9" }, want).Should().BeFalse();
    }

    [Fact] public void Built_tags_contain_ownership_global_and_request_tags_and_list_the_custom_keys()
    {
        var o = new OperatorOptions { OperatorId = "op", Tags = new() { ["CostCenter"] = "cc" } };
        var req = new AccessRequest { Namespace = "ns", ObjectName = "x", Uid = "u", ServiceAccount = "sa", Tags = new Dictionary<string, string> { ["App"] = "a" } };
        var tags = new ResourceOwnershipService(o).BuildTags(req, new ClusterContext("c", "111111111111", "aws", "us-east-1", "dev"));
        tags.Should().Contain(new KeyValuePair<string, string>("CostCenter", "cc")).And.Contain(new KeyValuePair<string, string>("App", "a"))
            .And.Contain(new KeyValuePair<string, string>(Tags.Namespace, "ns")).And.Contain(new KeyValuePair<string, string>(TagRules.ManagedKeysTag, "App+CostCenter"));
    }
}

public class PureLogicTests
{
    [Fact] public void Policy_json_ignores_formatting_order_and_scalar_vs_array()
    {
        var a = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:Get*","s3:List*"],"Resource":"*"},{"Effect":"Deny","Action":"s3:Delete*","Resource":"*"}]}""";
        var b = """{ "Statement": [ {"Resource":["*"],"Action":"s3:Delete*","Effect":"Deny"}, {"Resource":"*","Effect":"Allow","Action":["s3:List*","s3:Get*"]} ], "Version":"2012-10-17" }""";
        PolicyJson.Equivalent(a, b).Should().BeTrue();
        PolicyJson.Equivalent(a, a.Replace("s3:Get*", "s3:Put*")).Should().BeFalse();
    }

    [Fact] public void Policy_json_decodes_url_encoded_trust_policies() =>
        PolicyJson.Equivalent(Uri.EscapeDataString("""{"Version":"2012-10-17","Statement":[]}"""), """{"Version":"2012-10-17","Statement":[]}""").Should().BeTrue();

    [Fact] public void Role_names_are_stable_short_and_distinct()
    {
        var o = new OperatorOptions();
        var c = new ClusterContext(new string('c', 40), "111111111111", "aws", "us-east-1", "dev");
        AccessRequest R(string ns) => new() { Namespace = ns, ObjectName = "x", Uid = "u", ServiceAccount = new string('s', 60) };
        var n1 = Naming.RoleName(o, c, R(new string('n', 60) + "1"));
        var n2 = Naming.RoleName(o, c, R(new string('n', 60) + "2"));
        n1.Length.Should().BeLessOrEqualTo(64);
        n1.Should().NotBe(n2, "the hash covers the full, untruncated identity");
        n1.Should().Be(Naming.RoleName(o, c, R(new string('n', 60) + "1")));
    }

    [Fact] public void Backoff_grows_and_permanent_errors_wait_longer()
    {
        Backoff.Delay(ErrorKind.Transient, 0, () => 0).Should().BeLessThan(Backoff.Delay(ErrorKind.Transient, 5, () => 0));
        Backoff.Delay(ErrorKind.Permanent, 0, () => 0).Should().BeGreaterThan(Backoff.Delay(ErrorKind.Transient, 3, () => 1));
    }

    [Fact] public void Redactor_removes_credentials() =>
        Redactor.Redact("key AKIAABCDEFGHIJKLMNOP and aws_secret_access_key=abcdEFGH1234/xyz+").Should().NotContain("AKIAABCDEF").And.NotContain("abcdEFGH1234");

    [Fact] public void Planner_is_converged_when_aws_matches_and_sees_drift_otherwise()
    {
        var req = new AccessRequest { Namespace = "n", ObjectName = "x", Uid = "u", ServiceAccount = "sa" };
        var c = new ClusterContext("c", "111111111111", "aws", "us-east-1", "dev");
        var tags = new Dictionary<string, string> { ["k"] = "v" };
        var role = new DesiredRole("r", "arn:aws:iam::111111111111:role/r", """{"Statement":[]}""", "p", """{"a":1}""", [], null, tags, null);
        var d = new DesiredAccess(req, c, role);
        ObservedRole Seen(string? pol) => new(true, role.Arn, "/", """{"Statement":[]}""", tags, null, pol is null ? new Dictionary<string, string>() : new() { ["p"] = pol }, []);
        var assoc = new ObservedAssociation(true, "a-1", "arn:assoc", role.Arn, tags);
        var planner = new ReconciliationPlanner();
        planner.Plan(d, new ObservedState(Seen("""{"a":1}"""), assoc, null)).Converged.Should().BeTrue();
        planner.Plan(d, new ObservedState(Seen(null), assoc, null)).Steps.Should().ContainSingle(s => s.Kind == StepKind.EnsureInlinePolicy);
        planner.Plan(d, new ObservedState(Seen("""{"a":1}"""), assoc with { Tags = new Dictionary<string, string>() }, null)).Steps.Should().ContainSingle(s => s.Kind == StepKind.EnsureBinding);
    }
}

public class PolicyCrdTests
{
    [Fact] public async Task Policies_are_read_only_from_the_operator_namespace()
    {
        var client = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        V1Alpha1PodIdentityPolicy P(string ns, string name) => new() { Metadata = new() { Name = name, NamespaceProperty = ns }, Spec = new() { AllowedServices = ["s3"] } };
        // even if a team-namespace object were ever returned, it must be ignored
        client.ListAsync<V1Alpha1PodIdentityPolicy>("pio", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<V1Alpha1PodIdentityPolicy> { P("pio", "admin-policy"), P("team-a", "sneaky") });
        var source = new PolicyCrSource(client, new OperatorOptions { PolicyNamespace = "pio" }, TimeProvider.System, NullLogger<PolicyCrSource>.Instance);

        var rules = await source.ListAsync(default);

        rules.Select(r => r.Name).Should().Equal("admin-policy");
        await client.DidNotReceive().ListAsync<V1Alpha1PodIdentityPolicy>(Arg.Is<string?>(n => n != "pio"), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact] public void A_policy_governs_listed_namespaces_or_all_when_empty()
    {
        new PolicyRule { Name = "a" }.Governs("anything").Should().BeTrue();
        var scoped = new PolicyRule { Name = "b", Namespaces = ["payments"] };
        scoped.Governs("payments").Should().BeTrue();
        scoped.Governs("other").Should().BeFalse();
    }
}

public class DefaultPolicyTests
{
    static readonly ClusterContext Ctx = new("c1", "111111111111", "aws", "us-east-1", "dev");

    static ValidationResult Run(PolicyRule rule, params string[] actions) =>
        new PolicyValidator(new OperatorOptions()).Validate(new AccessRequest
        {
            Namespace = "payments", ObjectName = "x", Uid = "u", ServiceAccount = "api",
            Statements = [new PolicyStatement("S", "Allow", actions, ["arn:aws:s3:::b", "arn:aws:dynamodb:us-east-1:111111111111:table/t"])],
        }, [rule], Ctx);

    [Fact] public void A_default_policy_allows_every_service_and_action()
    {
        var rule = new PolicyRule { Name = "defaults" };
        rule.AllowedServices.Should().Equal("*");
        rule.AllowedActions.Should().Equal("*");
        Run(rule, "s3:GetObject", "dynamodb:Query", "sqs:SendMessage", "kms:Decrypt").IsValid.Should().BeTrue();
    }

    [Fact] public void An_empty_service_list_allows_no_service() =>
        Run(new PolicyRule { Name = "p", AllowedServices = [] }, "s3:GetObject").Issues.Should().Contain(i => i.Code == "ServiceNotAllowed");

    [Fact] public void An_empty_action_list_allows_no_action() =>
        Run(new PolicyRule { Name = "p", AllowedActions = [] }, "s3:GetObject").Issues.Should().Contain(i => i.Code == "ActionNotAllowed");

    [Theory]
    [InlineData("iam:CreateUser", "PrivilegeEscalation")]
    [InlineData("sts:AssumeRole", "PrivilegeEscalation")]
    [InlineData("s3:PutBucketPolicy", "PrivilegeEscalation")]
    [InlineData("s3:*", "WildcardAction")]
    public void Everything_allowed_still_blocks_escalation_and_wildcards(string action, string code) =>
        Run(new PolicyRule { Name = "defaults" }, action).Issues.Should().Contain(i => i.Code == code);

    [Fact] public void Wildcards_and_escalation_pass_only_when_explicitly_enabled() =>
        Run(new PolicyRule { Name = "p", AllowWildcardActions = true, AllowPrivilegeEscalationActions = true, AllowWildcardResources = true }, "iam:*").IsValid.Should().BeTrue();

    [Fact] public void The_chart_default_policy_allows_everything_via_star()
    {
        var spec = new EksDevopsOperator.Kubernetes.PodIdentityPolicySpec();
        spec.AllowedServices.Should().Equal("*");
        spec.AllowedActions.Should().Equal("*");
    }
}
