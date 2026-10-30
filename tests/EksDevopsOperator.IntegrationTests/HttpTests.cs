using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using EksDevopsOperator.Core;
using EksDevopsOperator.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EksDevopsOperator.IntegrationTests;

// The probe and metrics endpoint is plain code (no web framework), so it is tested over real sockets with no AWS or Kubernetes.
public class MetricsRegistryTests
{
    static KeyValuePair<string, object?> Tag(string k, string v) => new(k, v);

    [Fact] public void Renders_counters_gauges_and_histograms_in_the_prometheus_text_format()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        var counter = meter.CreateCounter<long>("things_total", description: "Things done");
        var took = meter.CreateHistogram<double>("took_seconds");
        long level = 7;
        meter.CreateObservableGauge("level", () => level);
        using var registry = new MetricsRegistry(meter.Name, new Dictionary<string, double[]> { ["took_seconds"] = [0.1, 1] });

        counter.Add(2, Tag("kind", "a")); counter.Add(3, Tag("kind", "a")); counter.Add(1, Tag("kind", "b\"x\\y"));
        took.Record(0.05); took.Record(0.5); took.Record(5);
        level = 9; // read at scrape time, not when it was created

        var text = registry.Render();
        var lines = text.Split('\n');

        lines.Should().Contain("# HELP things_total Things done").And.Contain("# TYPE things_total counter");
        lines.Should().Contain("things_total{kind=\"a\"} 5").And.Contain("things_total{kind=\"b\\\"x\\\\y\"} 1", "label values are escaped");
        lines.Should().Contain("# TYPE level gauge").And.Contain("level 9");
        lines.Should().Contain("# TYPE took_seconds histogram");
        lines.Should().Contain("took_seconds_bucket{le=\"0.1\"} 1").And.Contain("took_seconds_bucket{le=\"1\"} 2", "buckets are cumulative")
            .And.Contain("took_seconds_bucket{le=\"+Inf\"} 3").And.Contain("took_seconds_count 3");
        lines.Should().Contain(l => l.StartsWith("took_seconds_sum 5.5"));
        text.Should().EndWith("\n");
    }

    [Fact] public void Histogram_series_with_labels_keep_them_next_to_le()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        var h = meter.CreateHistogram<double>("d_seconds");
        using var registry = new MetricsRegistry(meter.Name, new Dictionary<string, double[]> { ["d_seconds"] = [1] });
        h.Record(0.5, Tag("source", "x"));
        registry.Render().Split('\n').Should().Contain("d_seconds_bucket{source=\"x\",le=\"1\"} 1").And.Contain("d_seconds_count{source=\"x\"} 1");
    }

    [Fact] public void Metrics_of_other_meters_are_ignored()
    {
        using var mine = new Meter("test-" + Guid.NewGuid());
        using var other = new Meter("other-" + Guid.NewGuid());
        using var registry = new MetricsRegistry(mine.Name);
        other.CreateCounter<long>("not_mine_total").Add(1);
        mine.CreateCounter<long>("mine_total").Add(1);
        registry.Render().Should().Contain("mine_total 1").And.NotContain("not_mine_total");
    }

    [Fact] public void The_operator_instruments_are_visible_after_start()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Telemetry).TypeHandle);
        using var registry = new MetricsRegistry(Telemetry.Name);
        var text = registry.Render();
        text.Should().Contain("# TYPE pod_identity_managed_roles gauge").And.Contain("# TYPE pod_identity_managed_associations gauge");
    }
}

public class ProbeServerTests : IAsyncLifetime
{
    readonly int _port;
    readonly IAwsAccountResolver _aws = Substitute.For<IAwsAccountResolver>();
    readonly MetricsRegistry _metrics = new("probe-test-" + Guid.NewGuid());
    readonly ProbeServer _server;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public ProbeServerTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); _port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _server = new ProbeServer(new OperatorOptions { ClusterName = "c1", HttpHost = "localhost", HttpPort = _port }, _aws, _metrics, NullLogger<ProbeServer>.Instance);
    }

    string Url(string path) => $"http://localhost:{_port}{path}";
    public Task InitializeAsync() => _server.StartAsync(CancellationToken.None);
    public async Task DisposeAsync() { await _server.StopAsync(CancellationToken.None); _metrics.Dispose(); _http.Dispose(); }

    [Fact] public async Task Liveness_answers_without_touching_aws()
    {
        var r = await _http.GetAsync(Url("/health/live"));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).Should().Be("Healthy");
        await _aws.DidNotReceiveWithAnyArgs().GetClusterAccountIdAsync(default);
    }

    [Fact] public async Task Readiness_is_healthy_when_aws_answers()
    {
        _aws.GetClusterAccountIdAsync(Arg.Any<CancellationToken>()).Returns("111111111111");
        var r = await _http.GetAsync(Url("/health/ready"));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).Should().Contain("c1").And.Contain("111111111111");
    }

    [Fact] public async Task Readiness_is_unhealthy_and_redacted_when_aws_fails()
    {
        _aws.GetClusterAccountIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<string>(new InvalidOperationException("denied for AKIAABCDEFGHIJKLMNOP")));
        var r = await _http.GetAsync(Url("/health/ready"));
        r.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await r.Content.ReadAsStringAsync();
        body.Should().StartWith("Unhealthy").And.NotContain("AKIAABCDEFGHIJKLMNOP");
    }

    [Fact] public async Task Metrics_are_served_as_prometheus_text()
    {
        var r = await _http.GetAsync(Url("/metrics"));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.ToString().Should().Contain("text/plain").And.Contain("version=0.0.4");
    }

    [Fact] public async Task Unknown_paths_are_404_and_other_methods_405()
    {
        (await _http.GetAsync(Url("/nope"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _http.PostAsync(Url("/health/live"), new StringContent(""))).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await _http.GetAsync(Url("/health/live/"))).StatusCode.Should().Be(HttpStatusCode.OK, "a trailing slash is tolerated");
    }

    [Fact] public async Task Handles_many_requests_at_once()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => _http.GetAsync(Url("/health/live"))));
        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact] public async Task Stopping_the_server_frees_the_port()
    {
        await _server.StopAsync(CancellationToken.None);
        using var fresh = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; // a new connection: pooled keep-alive ones may outlive the listener
        var act = async () => await fresh.GetAsync(Url("/health/live"));
        await act.Should().ThrowAsync<Exception>(); // refused on Windows; on Linux the listener may still hold the socket, but nothing answers
    }
}

public class ProbeServerStartupTests
{
    [Fact] public async Task A_port_that_is_already_served_fails_the_start_instead_of_running_without_probes()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        using var metrics = new MetricsRegistry("x");
        ProbeServer New() => new(new OperatorOptions { ClusterName = "c", HttpHost = "localhost", HttpPort = port }, Substitute.For<IAwsAccountResolver>(), metrics, NullLogger<ProbeServer>.Instance);

        var first = New();
        await first.StartAsync(CancellationToken.None);
        try
        {
            var second = New();
            var act = async () => { await second.StartAsync(CancellationToken.None); await Task.Delay(500); };
            await act.Should().ThrowAsync<Exception>();
        }
        finally { await first.StopAsync(CancellationToken.None); }
    }
}
