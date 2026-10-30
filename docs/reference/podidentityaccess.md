# PodIdentityAccess

`identity.platform.io/v1alpha1`, kind `PodIdentityAccess`, namespaced. Short name `pia`.

```yaml
apiVersion: identity.platform.io/v1alpha1
kind: PodIdentityAccess
metadata:
  name: payments-s3
  namespace: payments
spec:
  serviceAccountName: payments-api
  roleName: payments-s3
  tags:
    App: payments
    Env: prod
  managedPolicies: []
  statements:
    - sid: ReadInvoices
      actions: [s3:GetObject, s3:ListBucket]
      resources: [arn:aws:s3:::payments-invoices, arn:aws:s3:::payments-invoices/*]
  deletionPolicy: Delete
```

The operator creates an IAM role and binds it to the ServiceAccount, in the AWS account and EKS cluster it runs in: with a Pod Identity association. For IRSA use the separate [`IrsaAccess`](/reference/irsaaccess) kind, which has the same fields.

## Spec

| Field | Type | Required | Description |
|---|---|---|---|
| `serviceAccountName` | string | yes | ServiceAccount in the same namespace that receives the credentials. Changing it moves the association. |
| `roleName` | string | no | Prefix for the generated role name: lowercase letters, digits and `-`, max 40 characters. A stable hash is always appended. Changing it creates a new role and removes the old one. |
| `tags` | map | no | AWS tags for the role and the association, added to the platform team's global tags. At most 10. Keys use letters, digits and `_ . : / = @ -`. Keys starting with `pio:` or `aws:` are reserved, and keys set globally are rejected. |
| `managedPolicies` | string[] | no | Managed policy ARNs to attach. Must match the administrator's allow-list. |
| `statements` | list | no | Permissions for the role (see below). At least one of `statements` or `managedPolicies` is required. |
| `deletionPolicy` | `Delete` \| `Retain` | no | Default `Delete`. `Retain` removes only the association and leaves the role. May be restricted by the administrator. |

### `statements[]`

| Field | Description |
|---|---|
| `sid` | Optional statement id (letters and digits). |
| `actions` | IAM actions, such as `s3:GetObject`. |
| `resources` | Resource ARNs, in any AWS account. Wildcards are rejected unless the administrator allows them. For another account's resources, that account must also allow the role. |

Statements are always `Allow`. At most 20 statements per resource.

## Status

| Field | Description |
|---|---|
| `phase` | `Pending`, `Ready`, `Failed`, `Invalid` or `Conflict` |
| `observedGeneration` | Generation of the spec last reconciled successfully |
| `roleArn` | The IAM role the ServiceAccount assumes |
| `associationId` | EKS Pod Identity association id |
| `message` | Reason when not `Ready` |
| `conditions` | `Validated`, `RoleReady`, `AssociationReady`, `Ready` |

Example when ready:

```yaml
status:
  phase: Ready
  observedGeneration: 1
  roleArn: arn:aws:iam::111111111111:role/eks-devops-operator/pio-prod-payments-payments-api-7a185804
  associationId: a-0123456789abcdef0
  conditions:
    - {type: Validated, status: "True", reason: PolicyAccepted}
    - {type: RoleReady, status: "True", reason: RoleConfigured}
    - {type: AssociationReady, status: "True", reason: AssociationConfigured}
    - {type: Ready, status: "True", reason: ReconciliationSucceeded}
```

## Behaviour notes

- Use the ServiceAccount named in `serviceAccountName` in your pod spec. Restart pods created before the association existed.
- Two resources for the same ServiceAccount: the oldest wins, the other reports `Conflict`.
- Every AWS change is recorded as a Kubernetes event on the resource. See [Observability](/guide/observability).
