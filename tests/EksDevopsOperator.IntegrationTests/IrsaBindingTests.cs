using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;
using EksDevopsOperator.Aws;
using EksDevopsOperator.Core;
using EksDevopsOperator.Kubernetes;
using FluentAssertions;
using k8s.Autorest;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EksDevopsOperator.IntegrationTests;

public class IrsaBindingManagerTests
{
    const string Role = "arn:aws:iam::111111111111:role/eks-devops-operator/pio-c1-payments-api-1a2b3c4d";

    static V1ServiceAccount Account(Dictionary<string, string>? annotations = null) =>
        new() { Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "payments", Annotations = annotations } };

    static (IKubernetesClient Client, IrsaBindingManager Manager) Make(V1ServiceAccount? sa)
    {
        var client = Substitute.For<IKubernetesClient>();
        client.GetAsync<V1ServiceAccount>("api", "payments", Arg.Any<CancellationToken>()).Returns(sa);
        return (client, new IrsaBindingManager(client));
    }

    static IEnumerable<string> Patches(IKubernetesClient client) =>
        client.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IKubernetesClient.PatchAsync))
            .SelectMany(c => c.GetArguments().OfType<V1Patch>()).Select(p => (string)p.Content);

    [Fact] public async Task Reading_returns_the_role_from_the_annotation()
    {
        var (_, m) = Make(Account(new() { [IrsaBindingManager.RoleArnAnnotation] = Role }));
        var o = await m.ReadAsync("c1", "payments", "api", default);
        (o.Exists, o.RoleArn, o.Id).Should().Be((true, Role, "payments/api"));
        o.Tags.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false)] // no annotations at all
    [InlineData(true, false)]  // other annotations only
    public async Task Reading_a_service_account_without_the_annotation_or_a_missing_one_is_absent(bool otherAnnotations, bool _)
    {
        (await Make(Account(otherAnnotations ? new() { ["team"] = "x" } : null)).Manager.ReadAsync("c1", "payments", "api", default)).Exists.Should().BeFalse();
        (await Make(null).Manager.ReadAsync("c1", "payments", "api", default)).Exists.Should().BeFalse();
    }

    [Fact] public async Task Ensuring_writes_only_the_one_annotation_and_nothing_else()
    {
        var (client, m) = Make(Account(new() { ["team"] = "payments", ["other"] = "keep" }));
        var id = await m.EnsureAsync("c1", "payments", "api", Role, new Dictionary<string, string>(), default);
        id.Should().Be("payments/api");
        var patch = Patches(client).Should().ContainSingle().Subject;
        patch.Should().Contain(IrsaBindingManager.RoleArnAnnotation).And.Contain(Role).And.NotContain("team").And.NotContain("keep", "unrelated annotations are never rewritten");
        patch.Should().NotContain("labels").And.NotContain("secrets").And.NotContain("imagePullSecrets");
    }

    [Fact] public async Task Ensuring_an_already_correct_annotation_writes_nothing()
    {
        var (client, m) = Make(Account(new() { [IrsaBindingManager.RoleArnAnnotation] = Role }));
        await m.EnsureAsync("c1", "payments", "api", Role, new Dictionary<string, string>(), default);
        Patches(client).Should().BeEmpty();
    }

    [Fact] public async Task Ensuring_replaces_a_different_value()
    {
        var (client, m) = Make(Account(new() { [IrsaBindingManager.RoleArnAnnotation] = "arn:aws:iam::111111111111:role/old" }));
        await m.EnsureAsync("c1", "payments", "api", Role, new Dictionary<string, string>(), default);
        Patches(client).Should().ContainSingle().Which.Should().Contain(Role);
    }

    [Fact] public async Task A_missing_service_account_is_a_transient_error_with_a_clear_reason()
    {
        var (client, m) = Make(null);
        var act = async () => await m.EnsureAsync("c1", "payments", "api", Role, new Dictionary<string, string>(), default);
        var e = (await act.Should().ThrowAsync<PodIdentityException>()).Which;
        (e.Kind, e.Reason).Should().Be((ErrorKind.Transient, "ServiceAccountNotFound"));
        Patches(client).Should().BeEmpty();
    }

    [Fact] public async Task Deleting_removes_just_the_annotation_with_a_null_merge_patch()
    {
        var (client, m) = Make(Account(new() { [IrsaBindingManager.RoleArnAnnotation] = Role, ["keep"] = "me" }));
        await m.DeleteAsync("c1", "payments", "api", "payments/api", default);
        var patch = Patches(client).Should().ContainSingle().Subject;
        patch.Should().Contain(IrsaBindingManager.RoleArnAnnotation).And.Contain("null").And.NotContain("keep");
    }

    [Fact] public async Task Deleting_when_the_service_account_is_already_gone_is_fine()
    {
        var client = Substitute.For<IKubernetesClient>();
        var gone = new HttpOperationException { Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.NotFound), "") };
        client.PatchAsync<V1ServiceAccount>(Arg.Any<V1Patch>(), "api", "payments", Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<V1ServiceAccount>(gone));
        await new IrsaBindingManager(client).DeleteAsync("c1", "payments", "api", "payments/api", default);
    }
}

/// The real OIDC resolver and the real AWS SDK, talking to a tiny in-process AWS (IAM and EKS).
public sealed class OidcResolverTests : IDisposable
{
    const string Issuer = "oidc.eks.us-east-1.amazonaws.com/id/ABCDEF123456";
    static readonly ClusterContext Cluster = new("c1", "111111111111", "aws", "us-east-1", "dev");

    readonly HttpListener _listener = new();
    readonly List<string> _calls = [];
    readonly AwsClients _aws;
    public bool ProviderExists = true;
    public string? ReportedIssuer = "https://" + Issuer;

    public OidcResolverTests()
    {
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIAFAKEFAKEFAKE0000");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "fake");
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(Serve);
        _aws = new AwsClients(new AwsOptions { ServiceUrl = $"http://localhost:{port}", Region = "us-east-1" });
    }

    OidcResolver Resolver(string? configuredIssuer = null) => new(_aws, new OperatorOptions { OidcIssuer = configuredIssuer }, NullLogger<OidcResolver>.Instance);

    async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); } catch { return; }
            var body = new StreamReader(c.Request.InputStream).ReadToEnd();
            string text; var status = 200; var type = "text/xml";
            if (c.Request.Url!.AbsolutePath.StartsWith("/clusters/"))
            {
                lock (_calls) _calls.Add("DescribeCluster");
                type = "application/json";
                text = ReportedIssuer is null ? "{\"cluster\":{\"name\":\"c1\"}}" : "{\"cluster\":{\"name\":\"c1\",\"identity\":{\"oidc\":{\"issuer\":\"" + ReportedIssuer + "\"}}}}";
            }
            else
            {
                var form = HttpUtility.ParseQueryString(body);
                lock (_calls) _calls.Add($"{form["Action"]} {form["OpenIDConnectProviderArn"]}");
                if (ProviderExists) text = "<GetOpenIDConnectProviderResponse xmlns=\"https://iam.amazonaws.com/doc/2010-05-08/\"><GetOpenIDConnectProviderResult><Url>x</Url><ClientIDList><member>sts.amazonaws.com</member></ClientIDList><ThumbprintList/></GetOpenIDConnectProviderResult><ResponseMetadata><RequestId>1</RequestId></ResponseMetadata></GetOpenIDConnectProviderResponse>";
                else { status = 404; text = "<ErrorResponse xmlns=\"https://iam.amazonaws.com/doc/2010-05-08/\"><Error><Type>Sender</Type><Code>NoSuchEntity</Code><Message>not found</Message></Error><RequestId>1</RequestId></ErrorResponse>"; }
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            c.Response.StatusCode = status; c.Response.ContentType = type; c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
        }
    }

    public void Dispose() { _listener.Close(); }

    [Fact] public async Task The_issuer_is_discovered_from_the_cluster_and_the_provider_arn_is_built_from_it()
    {
        var info = await Resolver().ResolveAsync(Cluster, default);
        info.Issuer.Should().Be(Issuer, "the scheme is removed: IAM condition keys and provider ARNs use host/path");
        info.ProviderArn.Should().Be($"arn:aws:iam::111111111111:oidc-provider/{Issuer}");
        _calls.Should().Contain($"GetOpenIDConnectProvider {info.ProviderArn}");
    }

    [Fact] public async Task The_issuer_is_remembered_but_the_provider_is_checked_every_time()
    {
        var r = Resolver();
        await r.ResolveAsync(Cluster, default);
        await r.ResolveAsync(Cluster, default);
        _calls.Count(c => c == "DescribeCluster").Should().Be(1);
        _calls.Count(c => c.StartsWith("GetOpenIDConnectProvider")).Should().Be(2, "an admin may delete or create the provider at any time");
    }

    [Fact] public async Task A_configured_issuer_is_used_without_asking_the_cluster()
    {
        var info = await Resolver("https://oidc.example.com/id/XYZ/").ResolveAsync(Cluster, default);
        info.Issuer.Should().Be("oidc.example.com/id/XYZ");
        _calls.Should().NotContain("DescribeCluster");
    }

    [Fact] public async Task A_missing_provider_explains_how_to_create_it()
    {
        ProviderExists = false;
        var e = (await ((Func<Task>)(() => Resolver().ResolveAsync(Cluster, default))).Should().ThrowAsync<PodIdentityException>()).Which;
        (e.Kind, e.Reason).Should().Be((ErrorKind.Permanent, "OidcProviderMissing"));
        e.Message.Should().Contain("eksctl utils associate-iam-oidc-provider").And.Contain("aws_iam_openid_connect_provider");
    }

    [Fact] public async Task A_cluster_without_an_oidc_issuer_says_how_to_set_one()
    {
        ReportedIssuer = null;
        var e = (await ((Func<Task>)(() => Resolver().ResolveAsync(Cluster, default))).Should().ThrowAsync<PodIdentityException>()).Which;
        e.Reason.Should().Be("OidcIssuerUnknown");
        e.Message.Should().Contain("irsa.oidcIssuer");
    }
}
