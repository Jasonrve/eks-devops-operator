# ServiceAccount annotations

A team can ask for access on the ServiceAccount itself, with annotations, instead of writing a `PodIdentityAccess` or `IrsaAccess`. The operator creates that resource for them. It then
behaves exactly like one written by hand: same guardrails ([`PodIdentityPolicy`](/reference/podidentitypolicy)), same engine, same events.

```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: payments-api
  namespace: payments
  annotations:
    identity.platform.io/enabled: "true"
    identity.platform.io/policy-template: s3-read
    identity.platform.io/resource-arns: arn:aws:s3:::payments-invoices
```

Within about 30 seconds a `PodIdentityAccess` named `sa-payments-api` appears in the namespace. Add `identity.platform.io/mechanism: IRSA` and an `IrsaAccess` is created instead.
There is a ready-to-apply copy in `examples/payments-annotated-sa.yaml`.

## Annotations you set

| Annotation | Required | Description |
|---|---|---|
| `identity.platform.io/enabled` | yes | `"true"` turns it on. |
| `identity.platform.io/policy-template` | yes | A built-in set of permissions, see below. Annotations never carry policy JSON. |
| `identity.platform.io/resource-arns` | yes | Comma separated ARNs the template applies to. |
| `identity.platform.io/mechanism` | no | `PodIdentity` (default) or `IRSA`. Chooses which kind is created. |
| `identity.platform.io/role-name` | no | Role name prefix, as `spec.roleName`. |
| `identity.platform.io/deletion-policy` | no | `Delete` (default) or `Retain`. |
| `identity.platform.io/tags` | no | `Key=Value,Key2=Value2`, as `spec.tags`. |

| Template | Allows | ARNs also get |
|---|---|---|
| `s3-read` | GetObject, GetObjectVersion, ListBucket | `/*` (the objects of a bucket) |
| `s3-readwrite` | the above plus PutObject, DeleteObject, AbortMultipartUpload | `/*` |
| `sqs-consume` | ReceiveMessage, DeleteMessage, ChangeMessageVisibility, GetQueueAttributes, GetQueueUrl | |
| `sqs-send` | SendMessage, GetQueueAttributes, GetQueueUrl | |
| `sns-publish` | Publish | |
| `dynamodb-read` | GetItem, BatchGetItem, Query, Scan, DescribeTable | `/index/*` |
| `dynamodb-readwrite` | the above plus PutItem, UpdateItem, DeleteItem, BatchWriteItem | `/index/*` |
| `secretsmanager-read` | GetSecretValue, DescribeSecret | |
| `ssm-parameter-read` | GetParameter, GetParameters, GetParametersByPath | |

## Status, written back by the operator

| Annotation | Meaning |
|---|---|
| `identity.platform.io/status` | `Pending`, `Ready`, `Failed`, `Invalid` or `Conflict` |
| `identity.platform.io/role-arn` | The role, once there is one |
| `identity.platform.io/message` | Why it is not `Ready` |

Do not edit these; they are overwritten. Events are raised on the ServiceAccount too (`AccessResourceCreated`, `AccessResourceUpdated`, `AnnotationsInvalid`, `GeneratedResourceConflict`).

## Rules

- **The generated resource follows the annotations.** Edit them and the resource is updated. Remove them, or set `enabled` to anything but `true`, and it is deleted, which runs the normal cleanup of the role and binding.
  Deleting the ServiceAccount does the same, through the owner reference.
- **Changing `mechanism`** deletes the old kind first and creates the new one on the next pass, so the two never share a role.
- **Never overwritten:** a resource named `sa-<serviceaccount>` that was not generated from the ServiceAccount is left alone and reported as `GeneratedResourceConflict`.
- **Not a way around the guardrails.** A template that a policy does not allow is rejected like any other request, with the reason in `identity.platform.io/message`.
- **A hand-written resource for the same ServiceAccount** conflicts with the generated one: the oldest wins.

## Turning it on (platform team)

Off by default, because the operator needs more RBAC: list and patch ServiceAccounts, and create and delete the two access kinds, in every namespace.

```yaml
serviceAccountAnnotations:
  enabled: true
  interval: "00:00:30"
```

Anyone who can annotate a ServiceAccount can then request access, so combine it with a `PodIdentityPolicy` that limits services, actions and resource ARNs for the namespaces involved.
