# Image and sizing

## Image size

The image is a self-contained, trimmed build on Microsoft's chiseled (distroless) `runtime-deps` base. It is **more than 5 times smaller** than the usual `aspnet` image plus the app.

| Image | Base layers | App | Total (compressed) |
|---|---|---|---|
| `aspnet:10.0` + app (the usual way) | 93 MB | 4 MB | **~97 MB** |
| chiseled `aspnet` + app | 54 MB | 4 MB | ~58 MB |
| **This image**: `runtime-deps` chiseled + trimmed self-contained app | 5.7 MB | 12.8 MB | **~18.5 MB** (amd64), ~18.1 MB (arm64) |

Compressed sizes are what registries store and nodes pull, so they drive pull time and storage. The base numbers are the layer sizes Microsoft publishes for each tag;
the app numbers come from compressing the output of the same `dotnet publish` settings as the Dockerfile (36 MB of files on disk).
CI builds the image on every change, prints its size, and fails if it grows past 80 MB on disk.

### How it is built

- **No web framework.** The operator is a plain .NET worker host. The three endpoints Kubernetes and Prometheus need (`/health/live`, `/health/ready`, `/metrics`) are served by a small
  built-in HTTP listener, so the whole ASP.NET Core framework (Kestrel and its dependencies) is not in the image. This was the single biggest saving (about 6 MB compressed).
- **Self-contained, framework trimmed.** The .NET runtime ships inside the image, with unused framework code removed (`TrimMode=partial`). That lets it sit on a base of only 5.7 MB.
  KubeOps, the AWS SDK and the Kubernetes client are left whole, because they use reflection that a trimmer cannot see.
- **Chiseled base.** No shell, no package manager, runs as non-root (UID 1654). It still has OpenSSL and the CA bundle, so TLS to AWS and the Kubernetes API works.
- **Unused runtime parts removed.** The debugger and crash-dump libraries, the standalone GC builds and the LTTng trace provider (about 7 MB on disk) are deleted. None is loaded unless a `DOTNET_*`
  variable asks for it, and `DOTNET_EnableDiagnostics=0` switches the diagnostic socket off.
- **Lean runtime settings** (in the project file): workstation, non-concurrent garbage collection (one heap instead of one per CPU core), invariant globalization (no ICU),
  no profile-guided tiering, English resources only.
- **Multi-architecture.** The build stage always runs on the build machine and cross-publishes, so `linux/amd64` and `linux/arm64` (Graviton) images build natively with no emulation.
- **Fast builds.** The restore step has its own layer, NuGet packages are kept in a BuildKit cache, and the Docker build context contains only `global.json` and `src/`.

```sh
docker build -t eks-devops-operator .
docker buildx build --platform linux/amd64,linux/arm64 -t <registry>/eks-devops-operator --push .
```

### What you give up

- **No shell in the image.** To debug, attach an ephemeral container: `kubectl debug -it <pod> --image=busybox --target=operator`.
- **No crash dumps or `dotnet-dump` / `dotnet-trace`.** Diagnostics are off and the dump libraries are removed. Logs, events, metrics and traces are unaffected. If you need a dump for a bug,
  build a one-off image from the same source without the `rm` line and with `DOTNET_EnableDiagnostics=1`.
- **Metrics come from the built-in listener**, in the standard Prometheus text format. They carry no `otel_scope_name` label. Traces still use OpenTelemetry (OTLP) when you configure an endpoint.

### Why not smaller

What is left is the .NET runtime itself (about 12 MB on disk) and the Kubernetes client with all its API models (11.7 MB on disk, 2.7 MB compressed). Two further ideas were tried and rejected on evidence:

- **Trimming KubeOps and the Kubernetes client too** (`TrimMode=full`) would save about 5 MB compressed, but the build **fails to start**: the Kubernetes client creates its kubeconfig model
  types by reflection, and the trimmer removes their constructors (`MissingMethodException` on `K8SConfiguration`).
- **Native AOT** compiles, but the compiler flags both KubeOps and the Kubernetes client as not AOT-compatible (the same reflection, and AOT is stricter than trimming). It could not be linked or run in the
  environment used for this work, so it is unproven, and the best case is only a few MB smaller. The risk is not worth it.

Layer compression with zstd instead of gzip may shave a further 10-20% off pull size if your registry and nodes support it; this was not measured.

## Suggested requests and limits

```yaml
resources:
  requests: {cpu: 50m, memory: 128Mi}
  limits:   {cpu: "1", memory: 256Mi}
```

These are the chart defaults. They come from running the production build configuration against a stand-in for the Kubernetes and AWS APIs, with up to 300 `PodIdentityAccess` resources:

| What was measured | Result |
|---|---|
| Memory right after start | ~43 MB |
| **Peak memory** while 300 resources reconcile at once | **~108 MB** |
| Memory after the load settles | ~100 MB |
| CPU while those 300 resources reconcile (90 s) | ~0.45 core on average, then near zero |

::: warning How to read these numbers
They were measured on Windows (working set), not in a Linux container, because the comparison had to run without a container runtime. On Linux the figure is normally the same or lower.
Treat the defaults as a starting point and check them against your cluster with `kubectl top pod -n pio` and the `container_memory_working_set_bytes` metric. The reconcile loop in this test
was artificially busy (every resource reconciled repeatedly), so real steady-state load is lighter.
:::

How the values were chosen:

- **Memory request 128Mi** covers normal use. **Limit 256Mi** is well over twice the measured peak. .NET keeps its heap under 75% of the container limit (192 MB here), so it collects garbage
  before the kernel would kill the pod. On tight clusters you can lower the limit to `192Mi`.
- **CPU request 50m.** The operator is idle most of the time: each resource is re-checked only every `driftInterval` (10 minutes by default), and only changes cause work.
- **CPU limit 1.** Start-up compilation and a large first reconcile burst briefly. A limit keeps a runaway from starving the node. Remove it (`limits: {memory: 256Mi}`) if you prefer no throttling.
- **Startup probe.** The chart allows up to 60 seconds to start, so a tight CPU limit cannot get the pod killed while it warms up.

### Scaling up

| Resources (`PodIdentityAccess`) | Suggested change |
|---|---|
| Up to ~500 | the defaults |
| Thousands | memory limit `384Mi`-`512Mi`, CPU request `100m` |
| Slow start or small nodes | lower `maxConcurrentReconciles` from 4 to 2 to reduce the burst |

With the default two replicas only the leader reconciles; the standby holds its idle footprint, so budget roughly 2 × the request in total.
