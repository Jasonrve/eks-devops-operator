# PodIdentityPolicy

`identity.platform.io/v1alpha1`, kind `PodIdentityPolicy`, namespaced. Short name `pip`.

A PodIdentityPolicy says what teams are allowed to request. **The operator reads these objects only from its own namespace** (the namespace it is
deployed in, for example `pio`). A policy created in any other namespace is ignored, so teams cannot write or widen their own guardrails.
Give only platform administrators write access to the operator namespace.

```yaml
apiVersion: identity.platform.io/v1alpha1
kind: PodIdentityPolicy
metadata:
  name: payments
  namespace: pio              # the operator's namespace
spec:
  namespaces: [payments]
  allowedServices: [s3]
  allowedActions: [s3:GetObject, s3:ListBucket]
  deniedActions: ["s3:Delete*"]
  allowedResourceArnPatterns: ["arn:aws:s3:::payments-*"]
  allowedDeletionPolicies: [Delete, Retain]
```

## How policies combine

- A `PodIdentityAccess` is checked against **every** policy whose `namespaces` include its namespace (an empty list means all namespaces).
  It must satisfy all of them, so adding a policy can only make things stricter.
- If **no** policy governs the namespace, the request is rejected. This is the deny-by-default rule.
- Policies are re-read every few seconds and applied at the next reconcile. A resource that no longer satisfies the policies becomes `Invalid`;
  its existing AWS resources are left as they are, not deleted.

## Spec

| Field | Default | Description |
|---|---|---|
| `namespaces` | all | Namespaces this policy governs |
| `allowedMechanisms` | `["*"]` (both) | `PodIdentity` (the `PodIdentityAccess` kind) and/or `IRSA` (the `IrsaAccess` kind). `[]` allows none. IRSA also needs `irsa.enabled` on the operator. |
| `allowedServices` | `["*"]` (every service) | IAM service prefixes (`s3`, `sqs`, ...). `[]` allows none. |
| `allowedActions` | `["*"]` (every action) | Action globs teams may use, for example `s3:Get*`. `[]` allows none. |
| `deniedActions` | none | Action globs that are always refused, even if allowed above |
| `allowedResourceArnPatterns` | any ARN | Globs (`*` matches any run of characters) that every requested resource must match. Resources may be in any AWS account. |
| `allowedManagedPolicyArns` | none | Managed policy ARN globs teams may attach |
| `allowedDeletionPolicies` | `[Delete]` | `Delete` and/or `Retain` |
| `requirePermissionsBoundary` | `true` | Attach the configured permissions boundary to the roles |
| `allowWildcardActions` | `false` | Permit `s3:*` and `s3:Get*` style requests |
| `allowWildcardResources` | `false` | Permit `*` and overly broad ARNs |
| `allowPrivilegeEscalationActions` | `false` | Permit IAM/STS/Organizations/SSO/CloudFormation and known escalation actions |

## Installing

The Helm chart creates one policy named `default` from the `policy:` values (turn it off with `policy.create=false` and manage your own).
Add more at any time:

```sh
kubectl apply -f examples/policy.yaml     # must be in the operator namespace
```

RBAC: the operator reads policies with a namespaced Role in its own namespace, so it could not read other namespaces even if asked.
