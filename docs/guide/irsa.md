# IRSA (IAM Roles for Service Accounts)

The operator can bind a role to a ServiceAccount in two ways, and each has its own kind. **`PodIdentityAccess`** uses EKS Pod Identity. **`IrsaAccess`** uses IRSA, the older mechanism, still used by
many clusters and tools. Everything else (guardrails, tags, drift repair, events, cleanup) works the same.

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

The spec is the same as [`PodIdentityAccess`](/reference/podidentityaccess); see [`IrsaAccess`](/reference/irsaaccess). A ready-to-apply copy is in `examples/payments-irsa.yaml`.
A ServiceAccount can also get an `IrsaAccess` from [annotations](/guide/annotations) (`identity.platform.io/mechanism: IRSA`).

## What the operator creates

| | EKS Pod Identity | IRSA |
|---|---|---|
| IAM role | trusts `pods.eks.amazonaws.com`, limited to this cluster, namespace and ServiceAccount | trusts the cluster's **OIDC provider** (`sts:AssumeRoleWithWebIdentity`), limited to this ServiceAccount and the `sts.amazonaws.com` audience |
| Binding | an EKS association (`status.associationId`) | the `eks.amazonaws.com/role-arn` annotation on the ServiceAccount (`status.associationId` shows `namespace/name`) |
| Tags | on the role and the association | on the role only (an annotation cannot hold tags) |
| Ready condition | `AssociationReady` | `ServiceAccountReady` |
| Permissions boundary, inline policy, managed policies | yes | yes |

The IRSA trust policy, built by the operator and never by a team:

```json
{
  "Effect": "Allow",
  "Principal": { "Federated": "arn:aws:iam::111111111111:oidc-provider/oidc.eks.eu-west-1.amazonaws.com/id/EXAMPLED539D4633E53DE1B71EXAMPLE" },
  "Action": "sts:AssumeRoleWithWebIdentity",
  "Condition": { "StringEquals": {
    "oidc.eks.eu-west-1.amazonaws.com/id/EXAMPLED539D4633E53DE1B71EXAMPLE:sub": "system:serviceaccount:payments:payments-api",
    "oidc.eks.eu-west-1.amazonaws.com/id/EXAMPLED539D4633E53DE1B71EXAMPLE:aud": "sts.amazonaws.com"
  } }
}
```

Only the one annotation is ever written, with a JSON merge patch. Other annotations, labels and fields of the ServiceAccount are never touched, and an annotation that points at a role this operator
does not own is never overwritten (`ServiceAccountAnnotationNotOwned`).

## Turning it on (platform team)

IRSA is **off by default**, because the operator needs permission to change ServiceAccounts.

1. **The cluster's IAM OIDC provider must exist.** This is a one-time setup per cluster. The operator checks for it and tells you if it is missing, but never creates it:

   ```sh
   eksctl utils associate-iam-oidc-provider --cluster my-eks --approve
   ```

   or in Terraform, the `aws_iam_openid_connect_provider` resource.
2. **Enable it in the chart:**

   ```yaml
   irsa:
     enabled: true
     # oidcIssuer: oidc.eks.eu-west-1.amazonaws.com/id/EXAMPLE   # optional: found automatically from the cluster
   ```

   This adds one RBAC rule: `get` and `patch` on `serviceaccounts` (cluster-wide, since a rule cannot be limited to the namespaces teams use). Without `irsa.enabled`, that rule does not exist
   and IRSA requests are rejected with `IrsaDisabled`.
3. **Add two permissions to the operator's IAM role** (see [AWS setup](/guide/aws-setup)): `eks:DescribeCluster` (to find the issuer) and `iam:GetOpenIDConnectProvider`.
4. **Decide who may use it.** A [`PodIdentityPolicy`](/reference/podidentitypolicy) controls the mechanisms per namespace: `allowedMechanisms: [PodIdentity]` allows only `PodIdentityAccess`,
   `[IRSA]` only `IrsaAccess`, and the default `["*"]` allows both.

## Moving a workload between mechanisms

Delete one resource and create the other. The old binding and role are cleaned up by the deletion (unless `deletionPolicy: Retain`), then the new kind creates its own. A `PodIdentityAccess` and an
`IrsaAccess` for the same ServiceAccount at the same time conflict: the oldest wins. Restart the pods afterwards, since running pods keep the credentials they started with.

## Behaviour to know

- **The ServiceAccount must exist.** If it does not yet, the role is created and the resource reports `ServiceAccountNotFound` (retried with backoff). The annotation is added as soon as the ServiceAccount appears.
- **Drift is repaired on the drift interval**, not instantly. The operator watches `IrsaAccess` resources, not ServiceAccounts, so an annotation removed by hand comes back
  within `driftInterval` (10 minutes by default), with a `ServiceAccountAnnotationRepaired` warning event.
- **Pods must restart** to pick up a new or changed annotation. This is standard IRSA behaviour.
- **Cross-account access** works as with Pod Identity: name the other account's resources, and let its resource policy allow the role.

## Failures you may see

| Reason (in `status` and events) | Meaning | Fix |
|---|---|---|
| `IrsaDisabled` | The operator was installed without `irsa.enabled` | Platform team enables it |
| `MechanismNotAllowed` | No `PodIdentityPolicy` allows IRSA for your namespace | Ask the platform team |
| `OidcProviderMissing` | The cluster's IAM OIDC provider does not exist | Create it once (see above) |
| `OidcIssuerUnknown` | The cluster reports no OIDC issuer | Enable the cluster's OIDC issuer, or set `irsa.oidcIssuer` |
| `ServiceAccountNotFound` | The ServiceAccount does not exist yet | Create it; the operator retries |
| `ServiceAccountAnnotationNotOwned` | The ServiceAccount already has a `role-arn` annotation for a role this operator did not create | Remove the annotation, or use another ServiceAccount |
