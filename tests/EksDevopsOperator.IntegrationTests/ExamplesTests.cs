using EksDevopsOperator.Core;
using EksDevopsOperator.Kubernetes;
using FluentAssertions;
using k8s;

namespace EksDevopsOperator.IntegrationTests;

// The examples are documentation people copy and apply, so they must parse against the real API classes and map to what they claim.
public class ExamplesTests
{
    static string ExamplesDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "examples"))) return Path.Combine(d.FullName, "examples");
        throw new DirectoryNotFoundException("examples/ not found above " + AppContext.BaseDirectory);
    }

    static string Read(string file) => File.ReadAllText(Path.Combine(ExamplesDir(), file));

    [Fact] public void The_pod_identity_example_maps_to_a_pod_identity_request()
    {
        var e = KubernetesYaml.Deserialize<V1Alpha1PodIdentityAccess>(Read("payments-s3.yaml"));
        var r = Mapping.ToRequest(e);
        (r.Mechanism, r.ServiceAccount, r.Namespace).Should().Be((Mechanism.PodIdentity, "payments-api", "payments"));
        r.Statements.Should().ContainSingle().Which.Actions.Should().Contain("s3:GetObject");
    }

    [Fact] public void The_irsa_example_maps_to_an_irsa_request()
    {
        var e = KubernetesYaml.Deserialize<V1Alpha1IrsaAccess>(Read("payments-irsa.yaml"));
        var r = Mapping.ToRequest(e);
        (r.Mechanism, r.ServiceAccount, r.Namespace, r.DeletionPolicy).Should().Be((Mechanism.Irsa, "payments-api", "payments", DeletionPolicy.Delete));
        r.Tags.Should().Contain("App", "payments");
    }

    [Fact] public void Both_examples_pass_the_default_guardrails_when_irsa_is_enabled()
    {
        var options = new OperatorOptions { ClusterName = "c", IrsaEnabled = true };
        var cluster = new ClusterContext("c", "111111111111", "aws", "us-east-1", "dev");
        var validator = new EksDevopsOperator.Policy.PolicyValidator(options);
        foreach (var file in new[] { "payments-s3.yaml", "payments-irsa.yaml" })
        {
            var request = file.Contains("irsa") ? Mapping.ToRequest(KubernetesYaml.Deserialize<V1Alpha1IrsaAccess>(Read(file))) : Mapping.ToRequest(KubernetesYaml.Deserialize<V1Alpha1PodIdentityAccess>(Read(file)));
            validator.Validate(request, [new PolicyRule { Name = "default" }], cluster).Issues.Should().BeEmpty(file);
        }
    }

    [Fact] public void The_annotated_service_account_example_yields_a_valid_spec()
    {
        var sa = KubernetesYaml.Deserialize<k8s.Models.V1ServiceAccount>(Read("payments-annotated-sa.yaml"));
        SaAnnotations.IsEnabled(sa).Should().BeTrue();
        var (spec, error) = SaAnnotations.Parse(sa);
        error.Should().BeNull();
        spec.Should().BeOfType<PodIdentityAccessSpec>();
    }

    [Fact] public void The_policy_example_parses_and_scopes_to_its_namespace()
    {
        var p = KubernetesYaml.Deserialize<V1Alpha1PodIdentityPolicy>(Read("policy.yaml"));
        var rule = EksDevopsOperator.Kubernetes.PolicyCrSource.ToRule(p);
        rule.Governs("payments").Should().BeTrue();
        rule.Governs("other").Should().BeFalse();
        rule.AllowedMechanisms.Should().Equal("*");
    }
}
