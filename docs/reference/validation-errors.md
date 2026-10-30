# Validation errors

A rejected request has `phase: Invalid`; `status.message` lists every problem as `Code: explanation`. Nothing was created in AWS.

| Code | Meaning | Fix |
|---|---|---|
| `InvalidIdentity` | Namespace or ServiceAccount is not a valid Kubernetes name | Use a valid name |
| `NamespaceNotAuthorized` | No `PodIdentityPolicy` in the operator namespace governs your namespace | Ask the platform team to add the namespace to a policy |
| `MechanismNotAllowed` | No policy allows that kind (`PodIdentity` for `PodIdentityAccess`, `IRSA` for `IrsaAccess`) for the namespace | Use an allowed kind or ask the platform team |
| `IrsaDisabled` | IRSA is not enabled on this operator | Platform team sets `irsa.enabled` |
| `DeletionPolicyNotAllowed` | That `deletionPolicy` is not allowed | Use an allowed value |
| `InvalidAction` | Not in `service:Action` form | Fix the action |
| `ServiceNotAllowed` | The service is not in the policy `allowedServices` | Use an allowed service |
| `ActionNotAllowed` | The action is not on the policy `allowedActions` list | Use a listed action |
| `ActionDenied` | The action matches the policy `deniedActions` | Remove it |
| `WildcardAction` | A wildcard action such as `s3:*` | List exact actions |
| `PrivilegeEscalation` | IAM, STS, Organizations, SSO, CloudFormation, or a known escalation action | Remove it |
| `WildcardResource` | `*` or a too-broad ARN | Name the specific resources |
| `ResourceOutOfScope` | The ARN does not match `allowedResourceArnPatterns` | Use ARNs inside your approved scope |
| `InvalidResource` | Not an ARN | Fix it |
| `PartitionMismatch` | The ARN is in a different AWS partition | Use ARNs of the cluster's partition |
| `ManagedPolicyNotAllowed` | The ARN is not on the allow-list | Remove it or ask for approval |
| `InvalidRoleName` / `ProtectedRoleName` | `roleName` is malformed or looks like a protected role | Use lowercase letters, digits and `-` |
| `ReservedTag` | A tag key starts with `pio:` or `aws:` | Use another key |
| `InvalidTag` | A tag key or value has characters that are not allowed | Use letters, digits and `_ . : / = @ -` in keys |
| `TagConflict` | The key is set globally by the platform team | Use a different key |
| `TooManyTags` | More than 10 tags | Remove some |
| `TagKeysTooLong` | The tag keys are too long in total | Use fewer or shorter keys |
| `NoPermissions` / `EmptyStatement` | The request grants nothing or has an empty statement | Add actions and resources |
| `TooManyStatements` | More than 20 statements | Split the access into separate resources |
| `PolicyTooLarge` | The generated policy exceeds the IAM inline limit | Reduce statements or resources |
| `BoundaryRequired` | A policy requires a boundary but none is configured | Platform team sets `permissionsBoundary` |
