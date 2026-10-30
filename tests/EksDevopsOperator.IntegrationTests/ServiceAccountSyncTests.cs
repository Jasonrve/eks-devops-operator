using EksDevopsOperator.Core;
using EksDevopsOperator.Kubernetes;
using FluentAssertions;
using k8s.Models;
using KubeOps.Abstractions.Events;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EksDevopsOperator.IntegrationTests;

public class SaAnnotationsTests
{
    static V1ServiceAccount Sa(params (string, string)[] a) =>
        new() { Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "payments", Uid = "u1", Annotations = a.ToDictionary(x => x.Item1, x => x.Item2) } };

    [Fact] public void A_template_and_arns_become_a_pod_identity_spec_with_expanded_resources()
    {
        var (spec, error) = SaAnnotations.Parse(Sa((SaAnnotations.Enabled, "true"), (SaAnnotations.PolicyTemplate, "s3-read"),
            (SaAnnotations.ResourceArns, "arn:aws:s3:::inv, arn:aws:s3:::logs/*"), (SaAnnotations.Tags, "App=pay,Team=x"), (SaAnnotations.DeletionPolicy, "Retain")));
        error.Should().BeNull();
        spec.Should().BeOfType<PodIdentityAccessSpec>();
        spec!.ServiceAccountName.Should().Be("api");
        spec.Statements.Single().Resources.Should().Equal("arn:aws:s3:::inv", "arn:aws:s3:::inv/*", "arn:aws:s3:::logs/*");
        spec.Statements.Single().Actions.Should().Contain("s3:GetObject").And.NotContain("s3:PutObject");
        (spec.Tags["App"], spec.DeletionPolicy).Should().Be(("pay", "Retain"));
    }

    [Fact] public void The_mechanism_annotation_chooses_the_irsa_kind()
    {
        var (spec, _) = SaAnnotations.Parse(Sa((SaAnnotations.Mechanism, "irsa"), (SaAnnotations.PolicyTemplate, "sqs-send"), (SaAnnotations.ResourceArns, "arn:aws:sqs:us-east-1:111111111111:q")));
        spec.Should().BeOfType<IrsaAccessSpec>();
    }

    [Theory]
    [InlineData("nope", "s3-read", "arn:aws:s3:::b", "must be PodIdentity or IRSA")]
    [InlineData("PodIdentity", null, "arn:aws:s3:::b", "policy-template is required")]
    [InlineData("PodIdentity", "admin", "arn:aws:s3:::b", "unknown")]
    [InlineData("PodIdentity", "s3-read", null, "resource-arns is required")]
    public void Bad_annotations_are_rejected_with_a_reason(string mech, string? template, string? arns, string expected)
    {
        var a = new List<(string, string)> { (SaAnnotations.Mechanism, mech) };
        if (template is not null) a.Add((SaAnnotations.PolicyTemplate, template));
        if (arns is not null) a.Add((SaAnnotations.ResourceArns, arns));
        var (spec, error) = SaAnnotations.Parse(Sa([.. a]));
        spec.Should().BeNull();
        error.Should().Contain(expected);
    }

    [Fact] public void Malformed_tags_are_rejected()
    {
        var (spec, error) = SaAnnotations.Parse(Sa((SaAnnotations.PolicyTemplate, "sns-publish"), (SaAnnotations.ResourceArns, "arn:aws:sns:us-east-1:1:t"), (SaAnnotations.Tags, "novalue")));
        (spec, error).Should().Be((null, error));
        error.Should().Contain("Key=Value");
    }

    [Fact] public void Only_true_enables()
    {
        SaAnnotations.IsEnabled(Sa((SaAnnotations.Enabled, "TRUE"))).Should().BeTrue();
        SaAnnotations.IsEnabled(Sa((SaAnnotations.Enabled, "yes"))).Should().BeFalse();
        SaAnnotations.IsEnabled(Sa()).Should().BeFalse();
    }
}

public class ServiceAccountSyncTests
{
    static V1ServiceAccount Annotated(string mech = "PodIdentity") => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = "api", NamespaceProperty = "payments", Uid = "u1",
            Annotations = new Dictionary<string, string>
            {
                [SaAnnotations.Enabled] = "true", [SaAnnotations.Mechanism] = mech, [SaAnnotations.PolicyTemplate] = "s3-read", [SaAnnotations.ResourceArns] = "arn:aws:s3:::inv",
            },
        },
    };

    static (ServiceAccountSync Sync, IKubernetesClient Client) Make(V1ServiceAccount? sa, List<V1Alpha1PodIdentityAccess>? pia = null, List<V1Alpha1IrsaAccess>? irsa = null)
    {
        var client = Substitute.For<IKubernetesClient>();
        client.ListAsync<V1ServiceAccount>(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(sa is null ? [] : [sa]);
        client.ListAsync<V1Alpha1PodIdentityAccess>(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(pia ?? []);
        client.ListAsync<V1Alpha1IrsaAccess>(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(irsa ?? []);
        EventPublisher events = (_, _, _, _, _) => Task.CompletedTask;
        return (new ServiceAccountSync(client, events, new OperatorOptions(), null, NullLogger<ServiceAccountSync>.Instance), client);
    }

    static int Calls(IKubernetesClient c, string method) => c.ReceivedCalls().Count(x => x.GetMethodInfo().Name == method);

    static V1Alpha1PodIdentityAccess Generated(string name = "sa-api") => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "payments", Labels = new Dictionary<string, string> { [SaAnnotations.GeneratedLabel] = "serviceaccount" } },
        Spec = (PodIdentityAccessSpec)SaAnnotations.Parse(Annotated()).Spec!,
    };

    [Fact] public async Task An_annotated_service_account_gets_a_generated_resource_owned_by_it()
    {
        var (sync, client) = Make(Annotated());
        await sync.SyncOnceAsync(default);
        await client.Received(1).CreateAsync(Arg.Is<V1Alpha1PodIdentityAccess>(c =>
            c.Name() == "sa-api" && c.Namespace() == "payments" && c.Metadata.Labels.ContainsKey(SaAnnotations.GeneratedLabel) && c.Metadata.OwnerReferences[0].Uid == "u1"), Arg.Any<CancellationToken>());
    }

    [Fact] public async Task A_resource_with_the_same_name_that_was_not_generated_is_never_overwritten()
    {
        var (sync, client) = Make(Annotated());
        client.GetAsync<V1Alpha1PodIdentityAccess>("sa-api", "payments", Arg.Any<CancellationToken>()).Returns(new V1Alpha1PodIdentityAccess());
        await sync.SyncOnceAsync(default);
        Calls(client, "CreateAsync").Should().Be(0);
        Calls(client, "UpdateAsync").Should().Be(0);
    }

    [Fact] public async Task Changed_annotations_update_the_generated_resource_and_unchanged_ones_do_nothing()
    {
        var existing = Generated();
        var (sync, client) = Make(Annotated(), [existing]);
        await sync.SyncOnceAsync(default);
        Calls(client, "UpdateAsync").Should().Be(0);

        existing.Spec.Statements[0].Resources = ["arn:aws:s3:::old"];
        await sync.SyncOnceAsync(default);
        await client.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
        existing.Spec.Statements[0].Resources.Should().Contain("arn:aws:s3:::inv");
    }

    [Fact] public async Task Removing_the_annotations_deletes_the_generated_resource()
    {
        var existing = Generated();
        var (sync, client) = Make(null, [existing]);
        await sync.SyncOnceAsync(default);
        await client.Received(1).DeleteAsync(existing, Arg.Any<CancellationToken>());
    }

    [Fact] public async Task Switching_the_mechanism_removes_the_old_kind_before_creating_the_new_one()
    {
        var existing = Generated();
        var (sync, client) = Make(Annotated("IRSA"), [existing]);
        await sync.SyncOnceAsync(default);
        await client.Received(1).DeleteAsync(existing, Arg.Any<CancellationToken>());
        Calls(client, "CreateAsync").Should().Be(0);
    }

    [Fact] public async Task Invalid_annotations_create_nothing_and_report_on_the_service_account()
    {
        var sa = Annotated(); sa.Metadata.Annotations[SaAnnotations.PolicyTemplate] = "bogus";
        var (sync, client) = Make(sa);
        await sync.SyncOnceAsync(default);
        Calls(client, "CreateAsync").Should().Be(0);
        var patches = client.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IKubernetesClient.PatchAsync)).SelectMany(c => c.GetArguments().OfType<V1Patch>()).Select(p => (string)p.Content);
        patches.Should().ContainSingle().Which.Should().Contain("Invalid").And.Contain("unknown");
    }

    [Fact] public async Task The_generated_resources_status_is_mirrored_onto_the_service_account_only_when_it_changes()
    {
        var existing = Generated();
        existing.Status.Phase = "Ready"; existing.Status.RoleArn = "arn:aws:iam::111111111111:role/r";
        var sa = Annotated();
        var (sync, client) = Make(sa, [existing]);
        await sync.SyncOnceAsync(default);
        Calls(client, "PatchAsync").Should().Be(1);

        sa.Metadata.Annotations[SaAnnotations.Status] = "Ready"; sa.Metadata.Annotations[SaAnnotations.StatusRoleArn] = existing.Status.RoleArn;
        client.ClearReceivedCalls();
        await sync.SyncOnceAsync(default);
        Calls(client, "PatchAsync").Should().Be(0);
    }
}
