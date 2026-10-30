# Configuration

Three things configure the operator, and **teams control none of them**:

| What | Where | Controlled by |
|---|---|---|
| What may be requested (services, actions, resources, ...) | `PodIdentityPolicy` objects **in the operator's namespace** | Platform admins (RBAC on that namespace) |
| Tags added to everything | Helm value `tags` | Platform admins |
| Cluster, boundary, intervals | Helm values | Platform admins |

## Guardrail policies

Guardrails are [`PodIdentityPolicy`](/reference/podidentitypolicy) objects. The operator reads them **only from its own namespace**; the same kind of object
created in a team namespace is ignored. The Helm chart renders one policy named `default` from the `policy:` values:

```yaml
policy:
  create: true
  namespaces: []                          # empty = all namespaces
  allowedServices: ["*"]                  # the part before the colon in an action; "*" (default) = every service
  allowedActions: ["*"]                   # "*" (default) = every action; list globs such as s3:Get* to restrict
  deniedActions: ["s3:Delete*"]
  allowedResourceArnPatterns: ["arn:aws:s3:::payments-*"]
  allowedManagedPolicyArns: []
  allowedDeletionPolicies: [Delete, Retain]
  requirePermissionsBoundary: true
  allowWildcardActions: false
  allowWildcardResources: false
  allowPrivilegeEscalationActions: false
```

| Setting | Effect |
|---|---|
| `namespaces` | Which namespaces the policy governs. Empty means all. |
| `allowedServices` | Actions for any service not listed are rejected. The default `["*"]` allows every service; `[]` allows none. |
| `allowedActions` | Only these action globs are accepted. The default `["*"]` allows every action; `[]` allows none. |
| `deniedActions` | These are always rejected, even if `allowedActions` matches. |
| `allowedResourceArnPatterns` | Every requested resource ARN must match a pattern (`*` matches any run of characters). The ARNs may be in any AWS account. Set this in production. |
| `allowWildcardActions` | When false, `s3:*` and `s3:Get*` are rejected. |
| `allowWildcardResources` | When false, `*` and overly broad ARNs such as `arn:aws:s3:::*` are rejected. |
| `allowPrivilegeEscalationActions` | When false, IAM/STS/Organizations/SSO/CloudFormation actions and known escalation paths (for example `s3:PutBucketPolicy`, `lambda:CreateFunction`, `ssm:SendCommand`) are rejected. |
| `allowedDeletionPolicies` | Which `deletionPolicy` values teams may use. |

A policy with the defaults (`"*"` for services and actions) is permissive about *which* services and actions, but the other guardrails still apply: wildcard actions, wildcard resources and privilege-escalation actions (IAM, STS, Organizations and similar) stay blocked unless you allow them explicitly, and requests need a policy that governs their namespace (with no policy, nothing is accepted).
Use several policies to give different namespaces different limits: a request must satisfy **all** policies that govern its namespace.

## Tags

Add tags to every AWS resource the operator creates (the IAM role and the Pod Identity association):

```yaml
tags:
  CostCenter: cc-1234
  ManagedBy: platform-team
```

Teams can add more per request with `spec.tags`. The two sets are merged, and a request cannot override a global key (it is rejected with `TagConflict`).

Rules for tag keys, global and per request:

- At most 10 tags each, letters, digits and `_ . : / = @ -` only (no spaces; this is stricter than AWS so the operator can track its keys).
- Keys starting with `pio:` or `aws:` are reserved.
- Values may be up to 256 characters.
- The combined key names must stay short (about 250 characters in total).

Changing `tags` updates every existing role and association at its next reconcile (within `driftInterval`), and removes keys you dropped.
The operator only ever removes keys it applied itself. Tags added by anyone else survive.

## Other settings

| Value | Default | Meaning |
|---|---|---|
| `cluster.name` | required | Used in role names, tags and the association call. Never taken from a request. |
| `cluster.partition` | `aws` | `aws`, `aws-cn` or `aws-us-gov` |
| `environment` | `dev` | Recorded in the `pio:environment` tag |
| `permissionsBoundary` | `/eks-devops-operator/standard-workload` | Path and name of the boundary policy attached to every role. |
| `driftInterval` | `00:10:00` | How often Ready resources are re-verified. Also how soon a changed policy or tag is applied to existing resources. |
| `maxConcurrentReconciles` | `4` | Parallel reconciles. Different resources are isolated by name and tags. |
| `replicaCount` | `2` | Leader election means one active reconciler. |
| `Operator__HttpPort` (via `extraEnv`) | `8080` | Port of `/health/live`, `/health/ready` and `/metrics`. In a pod it listens on every interface; on Windows only on `localhost`. |
| `logLevel` | `Information` | Logs are JSON, with the resource uid, namespace and an attempt id in each scope. |

## Local and test clusters

`extraEnv` can point the AWS SDK at an emulator: `Aws__ServiceUrl=http://floci:4566`. Leave it unset in production so the SDK uses the
default credential chain (EKS Pod Identity) and real AWS endpoints. When running the operator outside a cluster, set `Operator__PolicyNamespace` to
the namespace that holds your `PodIdentityPolicy` objects, and put global tags in a JSON file named by `OPERATOR_CONFIG_FILE`
(`{"Operator":{"Tags":{"CostCenter":"cc-1"}}}`); the chart does this with a ConfigMap mounted at `/config/operator.json`.
