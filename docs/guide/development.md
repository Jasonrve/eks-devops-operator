# Development and testing

## Layout

```text
src/EksDevopsOperator/
  Engine/    validation, planning, reconcile + cleanup, policy documents, naming, ownership (no AWS or Kubernetes SDK calls)
  Aws/       IAM, STS and EKS calls with the AWS SDK for .NET (endpoint overridable for Floci)
  Kube/      the CRD, controller, finalizer, conflict detection (KubeOps)
  Http/      probe and metrics endpoint: a small HttpListener plus a Prometheus text renderer (no web framework)
  Program.cs host, DI, CRD generation
tests/EksDevopsOperator.IntegrationTests/   unit tests + engine tests against Floci
deploy/helm/eks-devops-operator/    chart (CRD in crds/)
docs/                                 this site (VitePress)
```

## Build and test

```sh
dotnet build
docker run -d --name floci -p 4566:4566 floci/floci:latest
dotnet test tests/EksDevopsOperator.IntegrationTests
```

| Tests | Needs |
|---|---|
| `ValidatorTests`, `PureLogicTests`, `MetricsRegistryTests`, `ProbeServerTests` | nothing |
| `EngineFlociTests`, `FlociCapabilityTests` | Floci on `localhost:4566` (override with `FLOCI_ENDPOINT`) |

`EngineFlociTests` run the real engine and real AWS SDK clients against Floci: role and association provisioning, idempotency, drift repair, tag handling (global,
per request, change, removal, foreign tags kept), role rename, deletion policies, rejected requests (no AWS writes), ownership refusal and parallel requests.
They use unique names per test so they can run in parallel and repeatedly.

::: warning What the emulator does not prove
Floci does not enforce IAM trust or permission policies, does not inject credentials into pods, and cannot tag Pod Identity associations in place (the operator recreates the association there; AWS tags it in place). Tests prove the roles, trust documents, policies and associations
are created correctly, not that AWS would let a pod assume the role.
:::

## Running against a cluster

See [Getting started](/guide/getting-started) for k3s + Floci with the real controller.

## CRD generation

The CRD is generated from `Kube/Entities.cs`. After changing it:

```sh
dotnet run --project src/EksDevopsOperator -- generate-crds "$PWD/deploy/helm/eks-devops-operator/crds"
```

CI fails if the committed CRD differs from the generated one.

## Docs

```sh
cd docs && npm install && npm run dev
```

## Conventions

- Every AWS call goes through `Telemetry.AwsCall` for metrics and traces.
- New validation rules need a test in `ValidatorTests` for both the rejected and accepted case.
- The engine must stay idempotent: every step is safe to run twice, and the plan is recomputed from real AWS state each time.
