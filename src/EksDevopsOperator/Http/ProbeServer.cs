using System.Net;
using System.Text;
using EksDevopsOperator.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EksDevopsOperator.Http;

/// A deliberately tiny HTTP endpoint (no web framework) for the three things Kubernetes and Prometheus need:
///   /health/live  the process is up
///   /health/ready the operator can reach AWS (STS answers), i.e. its credentials work
///   /metrics      Prometheus text format
public sealed class ProbeServer(OperatorOptions opt, IAwsAccountResolver aws, MetricsRegistry metrics, ILogger<ProbeServer> log) : BackgroundService
{
    static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);

    readonly HttpListener _listener = new();

    /// The listener starts here, not in ExecuteAsync, so a port conflict or missing permission fails the host start with a clear
    /// error instead of the operator running on without probes.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Prefixes.Add($"http://{opt.HttpHost}:{opt.HttpPort}/");
        _listener.Start();
        log.LogInformation("probes and metrics on http://{Host}:{Port}", opt.HttpHost, opt.HttpPort);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var stop = stoppingToken.Register(() => { try { _listener.Stop(); } catch (ObjectDisposedException) { } });
        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; } // listener stopped
            _ = Task.Run(() => HandleAsync(context, stoppingToken), CancellationToken.None);
        }
    }

    public override void Dispose()
    {
        _listener.Close();
        base.Dispose();
    }

    async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var method = context.Request.HttpMethod;
            if (method is not ("GET" or "HEAD")) { await WriteAsync(context, 405, "method not allowed"); return; }
            switch (context.Request.Url?.AbsolutePath.TrimEnd('/'))
            {
                case "/health/live": await WriteAsync(context, 200, "Healthy"); break;
                case "/health/ready": await ReadyAsync(context, ct); break;
                case "/metrics": await WriteAsync(context, 200, metrics.Render(), "text/plain; version=0.0.4; charset=utf-8"); break;
                default: await WriteAsync(context, 404, "not found"); break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogDebug("probe request failed: {Error}", e.Message); // client went away mid-response
            try { context.Response.Abort(); } catch { }
        }
    }

    async Task ReadyAsync(HttpListenerContext context, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadyTimeout);
        try
        {
            var account = await aws.GetClusterAccountIdAsync(timeout.Token);
            await WriteAsync(context, 200, $"Healthy: cluster {opt.ClusterName} in account {account}");
        }
        catch (Exception e)
        {
            await WriteAsync(context, 503, "Unhealthy: " + Redactor.Redact(e.Message));
        }
    }

    static async Task WriteAsync(HttpListenerContext context, int status, string body, string contentType = "text/plain; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        if (context.Request.HttpMethod != "HEAD") await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}
