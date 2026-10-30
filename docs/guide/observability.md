# Observability

## Endpoints

The operator listens on port 8080 (`Operator__HttpPort`), served by a small built-in HTTP listener rather than a web framework:

| Path | Purpose |
|---|---|
| `/health/live` | Process is up |
| `/health/ready` | Operator can call STS and knows its AWS account (credentials work) |
| `/metrics` | Prometheus metrics |

Enable scraping with the Prometheus Operator: `--set serviceMonitor.enabled=true`.

## Metrics

| Metric | Labels | Meaning |
|---|---|---|
| `pod_identity_reconcile_total` | `outcome` | Reconciles by result (`Ready`, `Failed`, `Invalid`, `Conflict`) |
| `pod_identity_reconcile_duration_seconds` | none | Reconcile time |
| `pod_identity_reconcile_errors_total` | `kind` | Failed reconciles by error kind |
| `pod_identity_policy_validation_failures_total` | `code` | Rejections by validation code |
| `pod_identity_aws_api_requests_total` | `service`, `operation` | AWS calls |
| `pod_identity_aws_api_errors_total` | `service`, `operation`, `code` | AWS errors by SDK error code |
| `pod_identity_drift_detected_total` | none | Drift found and repaired |
| `pod_identity_cleanup_failures_total` | none | Failed cleanups |
| `pod_identity_managed_roles` | none | Roles of Ready resources |
| `pod_identity_managed_associations` | none | Ready associations |

Metrics are rendered in the standard Prometheus text format straight from the operator's .NET metrics, so there is no `otel_scope_name` label. No label holds a namespace, name or ARN, so cardinality stays bounded. A counter only appears after its first event, so `pod_identity_cleanup_failures_total` is absent until a cleanup fails.
Existence checks (`NoSuchEntity` on `GetRole` and similar) are not counted as errors in `pod_identity_aws_api_errors_total`.
The reconcile duration histogram uses second-scale buckets from 50 ms to 60 s.

Example alerts:

```yaml
- alert: PodIdentityReconcileFailing
  expr: sum(rate(pod_identity_reconcile_errors_total[15m])) > 0
  for: 30m
- alert: PodIdentityAwsAccessDenied
  expr: sum(rate(pod_identity_aws_api_errors_total{code=~"AccessDenied.*"}[10m])) > 0
```

## Status

```sh
kubectl get podidentityaccesses -A
kubectl describe podidentityaccess payments-s3 -n payments
```

Status has `phase`, `sourceRoleArn`, `targetRoleArn`, `associationId`, `message`, and conditions
(`Validated`, `RoleReady`, `AssociationReady`, `Ready`). **`Ready` is only set after the operator has read the AWS state back and compared it.**

## Kubernetes events

Every create, update, repair and delete the operator performs is recorded as an event on the `PodIdentityAccess`, so `kubectl describe` tells the whole story:

```sh
kubectl -n payments get events --field-selector involvedObject.name=payments-s3
```

| When | Reason | Type |
|---|---|---|
| First reconcile | `Reconciling` | Normal |
| Spec edited | `SpecChanged` | Normal |
| Role, policy created | `RoleCreated`, `PolicyCreated`, `ManagedPoliciesCreated` | Normal |
| Binding created | `AssociationCreated` (Pod Identity) or `ServiceAccountAnnotationCreated` (IRSA) | Normal |
| Changed because the spec changed, including tag edits | `RoleUpdated`, `PolicyUpdated`, `ManagedPoliciesUpdated`, `AssociationUpdated`, `ServiceAccountAnnotationUpdated` | Normal |
| Changed back after someone edited or removed it outside the operator | the same names with `Repaired`, for example `PolicyRepaired`, `AssociationRepaired` | **Warning** |
| ServiceAccount annotations created, updated or rejected a generated resource (events on the ServiceAccount) | `AccessResourceCreated`, `AccessResourceUpdated`, `AnnotationsInvalid`, `GeneratedResourceConflict` | Normal, Warning for the last two |
| Role name changed, old role removed | `PreviousRoleDeleted` | Normal |
| Whole resource is ready | `Reconciled` | Normal |
| Rejected by guardrails | `ValidationFailed` | Warning |
| Another resource owns the ServiceAccount | `OwnershipConflict` | Warning |
| AWS or cluster error (each time the failure changes) | `ReconcileFailed` | Warning |
| Resource deleted | `Deleting`, then `AssociationDeleted`, `ServiceAccountAnnotationRemoved` or `RoleDeleted` per object, `ResourceRetained` per kept object, `CleanupFailed` on error, `Deleted` at the end | Normal / Warning |

What a resource's life looks like, in order:

```text
Reconciling                 creating PodIdentity access for ServiceAccount payments-api
RoleCreated                 created role arn:aws:iam::111111111111:role/eks-devops-operator/pio-prod-payments-payments-api-267dc7e3 (role missing)
PolicyCreated               created permissions policy on pio-prod-payments-payments-api-267dc7e3 (inline policy for new role)
AssociationCreated          created Pod Identity association a-0123 for payments/payments-api (binding missing)
Reconciled                  role arn:... and association a-0123 are ready
SpecChanged                 spec changed (generation 1 -> 2), reconciling
PolicyUpdated               updated permissions policy on pio-prod-payments-payments-api-267dc7e3 (inline policy differs)
PolicyRepaired   (Warning)  repaired permissions policy on ... (inline policy missing)      <- someone deleted it in AWS
Deleting                    resource deleted; cleaning up with deletionPolicy Delete
AssociationDeleted          deleted association a-0123
RoleDeleted                 deleted arn:aws:iam::111111111111:role/eks-devops-operator/pio-prod-...
Deleted                     cleanup complete: 2 removed, 0 retained
```

A converged reconcile changes nothing, so it emits no events. Messages never contain credentials (they pass through the redactor).

## Logs

Logs are JSON on stdout, one object per line. Set the level with the chart value `logLevel` (`Debug`, `Information`, `Warning`).

| Level | What is logged |
|---|---|
| **Information** | The startup summary (version, cluster, region, policy namespace, IRSA, tags), the AWS account in use, the policies loaded, **every change made** (same text as the event), phase changes, finalizer added, cleanup start and finish |
| **Warning** | Requests rejected by the guardrails, ownership conflicts, refusals (`RoleNotOwned` and similar), failures with their kind (Transient, Permanent, Authorization), drift repairs, cleanup that must be retried, no `PodIdentityPolicy` found, an overridden AWS endpoint |
| **Debug** | Routine detail: "in sync", the plan, waiting for AWS to converge, every AWS call with its duration, status writes, retry schedule, full exception details |

A healthy, idle operator logs nothing above Debug. KubeOps itself is set to `Warning` by default (`libraryLogLevel`), because at `Information` it logs a line per reconcile and per watch event; its cache library is always `Warning`. Each line carries the identifiers needed to follow one resource: `uid`, `namespace`, `name`, `serviceAccount`, `mechanism`, `cluster`
and an `attempt` id that is the same for every line of one reconcile. They appear in the line's `Scopes`:

```sh
kubectl -n pio logs deploy/eks-devops-operator | jq -c 'select(.Scopes[]?.uid == "11111111-2222-3333-4444-555555555555") | [.LogLevel, .Message]'
```

```text
["Information","added finalizer identity.platform.io/finalizer so AWS resources are cleaned up on deletion"]
["Information","loaded 1 PodIdentityPolicy object(s) from namespace pio: default"]
["Information","RoleCreated: created role arn:aws:iam::111111111111:role/eks-devops-operator/pio-e2e-payments-payments-api-267dc7e3 (role missing)"]
["Information","AssociationCreated: created Pod Identity association a-1 for payments/payments-api (binding missing)"]
["Information","reconciled in 1204 ms with 3 change(s): role arn:aws:..., binding a-1"]
["Warning","request rejected by the guardrails: PrivilegeEscalation: action 'iam:*' can escalate privileges"]
["Warning","reconcile failed (Permanent): IAM role arn:aws:... already exists and is not owned by this request (Foreign); the operator will not modify it"]
```

Credentials never reach the log: messages from AWS errors pass through the same redactor as events and status.

## Traces

Traces use OpenTelemetry and are exported over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set through `extraEnv`; each reconcile and each AWS call is a span.
