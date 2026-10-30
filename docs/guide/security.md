# Security model

## Who can do what

| Actor | Can | Cannot |
|---|---|---|
| Application team | Create a `PodIdentityAccess` in their namespace | Supply trust policy JSON, pick the role name outright, exceed the guardrails, override global tags |
| Platform admin | Write `PodIdentityPolicy` objects in the operator namespace; set global tags, the boundary and the cluster identity (Helm values); create IAM roles | |
| Operator | Create, update and delete roles it owns under `/eks-devops-operator/` in its own account | Anything outside that path, or any role it did not create |

Use Kubernetes RBAC to give teams `create/get/list/update/delete` on `podidentityaccesses` in their own namespaces only, and no access to the operator namespace.

## Controls in the code

1. **Validate before any AWS write.** Invalid requests are rejected with a precise reason and `phase: Invalid`. Every reconcile re-validates, so editing a resource cannot bypass a check. If you tighten the guardrails later, existing resources become `Invalid` at their next reconcile and stop being changed. Their AWS resources are left as they are, not deleted.
2. **Guardrails live where teams cannot write.** `PodIdentityPolicy` objects are read only from the operator's own namespace (enforced in code and by a namespaced Role). A policy created in a team namespace is ignored. Restrict write access to the operator namespace to platform admins.
3. **Deny by default.** No policy governing the namespace, or an unmatched action or resource, means rejection.
4. **No role-name hijacking.** Role names are generated as `readable-part + hash(account, cluster, namespace, serviceaccount)`.
   A requested `roleName` is only a prefix, it must be lowercase and short, and protected patterns (`*Admin*`, `aws-*`, `eks-*`, ...) are refused.
5. **Ownership checks.** Before modifying or deleting a role, the operator reads its tags and path. A role not tagged by this operator and cluster, or tagged for another namespace or ServiceAccount, is **never touched**: the request fails with `RoleNotOwned`.
   An existing association that the operator does not own is never overwritten (`AssociationNotOwned`).
6. **Strict trust policy.** The role trusts only `pods.eks.amazonaws.com`, scoped to this account and cluster. Session tags stay on, and the trust requires the expected namespace, ServiceAccount and cluster name. No account root, no wildcard principal.
7. **Permissions boundary** on every role as a ceiling, in addition to (never instead of) request validation.
8. **Tags cannot be abused.** Global tags are admin-controlled and cannot be overridden. Keys starting with `pio:` (ownership) or `aws:` are reserved, so a team cannot forge ownership tags. The operator removes only tag keys it applied itself.
9. **Safe cleanup.** Deleting a resource never deletes a role the operator does not own, and never removes a previous role it did not create.
10. **No secrets stored.** Credentials stay in the AWS SDK. Messages written to status, events and logs pass through a redactor for access keys, tokens and secrets.
11. **Hardened runtime.** Non-root, read-only root filesystem, all capabilities dropped, seccomp `RuntimeDefault`, resource limits, graceful shutdown, leader election.

## IRSA

IRSA is **off by default** and adds the only extra Kubernetes permission the operator can have:

- **One RBAC rule, only when enabled:** `get` and `patch` on `serviceaccounts`. A Kubernetes rule cannot be limited to the namespaces teams use, so this is cluster-wide. Without `irsa.enabled` the rule does not exist and IRSA requests are rejected.
- **One annotation:** the operator writes only `eks.amazonaws.com/role-arn` (a JSON merge patch of that one key). It never changes any other field or annotation, and never overwrites an annotation that points at a role it does not own.
- **A strict trust policy:** the role trusts only the cluster's OIDC provider, for exactly one ServiceAccount (`:sub`) and the `sts.amazonaws.com` audience (`:aud`). Teams cannot influence it.
- **The OIDC provider is checked, never created.** The operator does not get `iam:CreateOpenIDConnectProvider`.
- **A policy decides who may use it:** `allowedMechanisms` per namespace, on top of `irsa.enabled`.

Remember that anyone who can edit a ServiceAccount can also remove or change the annotation by hand. The operator repairs it on the next drift check, but a team that can edit its own ServiceAccount
cannot gain more than the role the annotation names, and the role's trust policy only allows that one ServiceAccount.

## Conflicts

EKS allows one Pod Identity association per `namespace/serviceAccount`. If two `PodIdentityAccess` resources name the same ServiceAccount,
the **oldest wins** and the others report `phase: Conflict` listing the winner. Permissions are never merged.

## Residual risks

- **The guardrails are only as good as your configuration.** Use `allowedResourceArnPatterns` per environment, and keep `allowedServices` and `allowedActions` small.
- **The operator's IAM role must match the design.** Follow [AWS setup](/guide/aws-setup): path-scoped, boundary required. The IAM boundary condition is enforced by IAM, not by this operator.
- **Resources in other accounts need that account's consent.** A team can name an outside ARN, but access works only if the other account's resource policy allows the role. Treat the role ARN as a principal in those policies.
- Anyone who can edit the operator's Helm values, the operator namespace or the operator's IAM role is a platform admin by definition.

Report vulnerabilities privately. See `SECURITY.md` in the repository.
