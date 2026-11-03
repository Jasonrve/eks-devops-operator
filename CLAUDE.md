# CLAUDE.md

Guidance for AI assistants and new contributors working in this repo. For user-facing docs see `README.md` and `docs/`.

## What this is

A Kubernetes operator for Amazon EKS (C# / .NET 10, [KubeOps](https://dotnet.github.io/dotnet-operator-sdk/) + AWS SDK for .NET). A team applies one custom resource (`PodIdentityAccess` or `IrsaAccess`); the operator creates the IAM role and the binding (an EKS Pod Identity association, or the ServiceAccount `role-arn` annotation for IRSA) that deliver AWS credentials. Platform admins constrain requests with `PodIdentityPolicy` guardrails. No database: Kubernetes holds desired state, AWS holds actual state.

## Build, test, run

```sh
dotnet build                      # or -c Release
dotnet test                       # needs Floci (AWS emulator) on localhost:4566
npm --prefix docs run dev         # preview the docs site
```

- **Tests need Floci.** Start it first: `docker run -d --name floci -p 4566:4566 floci/floci:latest`. The harness reads `FLOCI_ENDPOINT` and falls back to `http://localhost:4566` (see `tests/.../EngineHarness.cs`). CI pins Floci by digest; local runs use `:latest`.
- Pure-logic tests (e.g. `ValidatorTests`, `PureLogicTests`, `HttpTests`) don't touch AWS; filter with `dotnet test --filter "FullyQualifiedName~ValidatorTests"` to skip Floci.
- The operator entry point is `src/EksDevopsOperator/Program.cs` (a plain generic Worker host — **no ASP.NET / web framework**; probes and `/metrics` are a small `HttpListener` in `Http/ProbeServer.cs`).

## CRDs

- CRD YAML is **generated from `Kube/Entities.cs`**, not hand-edited:
  ```sh
  dotnet run --project src/EksDevopsOperator -- generate-crds "$PWD/charts/eks-devops-operator/crds"
  ```
- CI fails if the committed CRDs differ from the generated ones. Regenerate after any entity change.
- The Helm chart lives in `charts/eks-devops-operator/` (it was `deploy/helm/` — don't reintroduce that path). Helm applies `crds/` **only on install**, never on upgrade; upgrades must `kubectl apply` the CRDs manually.

## Layout and a gotcha

```
src/EksDevopsOperator/
  Engine/   validate, plan, reconcile, AWS-agnostic domain logic
  Aws/      AWS SDK calls (IAM, EKS, STS, OIDC)
  Kube/     CRD entities, controllers, finalizers, ServiceAccount sync
  Http/     ProbeServer + Prometheus text renderer
  Program.cs  host, DI wiring, generate-crds command
tests/      xUnit; engine tests run against Floci
charts/     Helm chart (CRDs in crds/)
docs/       VitePress site
```

**Folder names do not match C# namespaces.** `Engine/` holds `EksDevopsOperator.Core` and `EksDevopsOperator.Policy`; `Kube/` is `EksDevopsOperator.Kubernetes`; `Aws/` is `EksDevopsOperator.Aws`; `Http/` is `EksDevopsOperator.Http`. Go by the namespace, not the directory.

## Invariants — don't break these

1. **Validate before any AWS write.** `PolicyValidator` runs on every reconcile; nothing in AWS changes for an invalid request. A permissions boundary is an extra guardrail, never a substitute for validation.
2. **Deny by default.** A request needs a `PodIdentityPolicy` that governs its namespace and must satisfy *every* governing policy. No policy ⇒ nothing is allowed.
3. **Only touch what we own.** Before mutating or deleting any IAM role or binding, `ResourceOwnershipService.Check` must confirm the ownership tags (`pio:operator`, `pio:cluster`, `pio:namespace`, `pio:service-account`) and IAM path. Never modify a role the operator didn't create.
4. **Ready means verified.** The engine reads AWS back and re-plans until converged; `Ready` is never reported on the strength of a create call alone (`Engine/AccessEngine.cs`).
5. **Idempotent steps.** A crash at any point must re-plan safely from real AWS state on the next reconcile.
6. **Redact secrets.** Route anything that reaches logs, events, or status through `Redactor` (credentials/tokens/keys).
7. **Tags: only remove keys we applied.** Tag reconciliation tracks its own managed keys (`TagRules`); tags added by others are left alone.

## Logging convention

`Information` for each change made and outcomes worth human attention; `Warning` for rejections, conflicts, refusals, failures; `Debug` for routine reads/plans/"already in sync". Every line carries uid, namespace, name, ServiceAccount, mechanism (see the scope in `AccessEngine`).

## Configuration

User-facing Helm values: `docs/reference/helm-values.md` and `docs/guide/configuration.md`. Global tags are mounted as JSON at `/config/operator.json` (tag keys can contain characters invalid in env var names); everything else is `Operator__*` / `Aws__*` env vars.

Advanced `OperatorOptions` not exposed as Helm values are settable via `extraEnv` (e.g. `Operator__IamPath`, `Operator__OperatorId`, `Operator__ProtectedRoleNamePatterns`, `Operator__ConsistencyTimeout`, `Operator__EnforceSessionTagConditions`, `Operator__LeaderElection`, `Operator__WatchNamespace`). **`Operator__IamPath` defaults to `/eks-devops-operator/` and the operator IAM policy in `docs/guide/aws-setup.md` is scoped to that path** — change one and you must change the other or permissions break.

## Gotchas

- `JsonSerializerIsReflectionEnabledByDefault` must stay `true` in the csproj: the Kubernetes client and status writes use reflection-based `System.Text.Json`, and the framework is trimmed in the published image.
- `generate-crds <dir>` is the only CLI subcommand; without it the process runs as the operator host.
