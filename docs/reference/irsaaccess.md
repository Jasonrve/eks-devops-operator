# IrsaAccess

`identity.platform.io/v1alpha1`, namespaced, short name `irsa`. Gives a ServiceAccount AWS permissions with **IRSA** (IAM Roles for Service Accounts). It is a separate kind from
[`PodIdentityAccess`](/reference/podidentityaccess) because it works differently, but its spec is the same: `serviceAccountName`, `roleName`, `tags`, `managedPolicies`, `statements`
and `deletionPolicy`, with the same meaning and the same guardrails.

```yaml
apiVersion: identity.platform.io/v1alpha1
kind: IrsaAccess
metadata:
  name: payments-s3-irsa
  namespace: payments
spec:
  serviceAccountName: payments-api
  statements:
    - actions: [s3:GetObject, s3:ListBucket]
      resources: [arn:aws:s3:::payments-invoices, arn:aws:s3:::payments-invoices/*]
```

Differences from `PodIdentityAccess`:

- The role trusts the cluster's **OIDC provider**, limited to this ServiceAccount, instead of `pods.eks.amazonaws.com`.
- The binding is the `eks.amazonaws.com/role-arn` annotation on the ServiceAccount (`status.associationId` shows `namespace/name`), not an EKS association. Annotations cannot carry tags, so tags are on the role only.
- The condition `ServiceAccountReady` replaces `AssociationReady`.
- It must be enabled by the platform team (`irsa.enabled`) and allowed by a policy (`allowedMechanisms`). See [IRSA](/guide/irsa).

A ServiceAccount has one binding: a `PodIdentityAccess` and an `IrsaAccess` for the same ServiceAccount conflict, and the oldest wins.
