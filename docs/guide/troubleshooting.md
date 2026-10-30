# Troubleshooting

Start here:

```sh
kubectl describe podidentityaccess <name> -n <ns>      # conditions, message, events
kubectl -n pio logs deploy/pio | grep <name>           # JSON logs
```

## Phases

| Phase | Meaning | What to do |
|---|---|---|
| `Pending` | Not reconciled yet or in progress | Wait. If it stays, check the operator is running and leader. |
| `Ready` | AWS state was read back and matches | Nothing. |
| `Invalid` | Rejected by validation. **Nothing was changed in AWS.** | Read `status.message`, fix the spec (see [validation errors](/reference/validation-errors)). |
| `Conflict` | Another resource claims the same ServiceAccount | Delete or change the newer one. The message names the winner. |
| `Failed` | An AWS or cluster error | See below. Retried with backoff. |

## Common causes

**`NamespaceNotAuthorized`**: no `PodIdentityPolicy` in the operator namespace governs your namespace. Ask the platform team. Policies created anywhere else are ignored by design.

**`TagConflict`**: the tag is set globally by the platform team. Use a different key.

**`RoleNotOwned`**: an IAM role with the generated name already exists but was not created by this operator for this workload. The operator will not modify it.
Rename your `roleName` prefix, or delete the stale role if you are sure it is unused.

**`AssociationNotOwned`**: a Pod Identity association for that ServiceAccount already exists (created by hand, Terraform or another tool). Remove it or use another ServiceAccount.

**`AccessDenied`** on `iam:CreateRole`: the operator role requires the permissions boundary in its condition, and the boundary policy does not exist at the configured path,
or `permissionsBoundary` does not match the one in the IAM condition.

**`AccessDenied`** on `eks:TagResource`, or on `iam:TagRole`: add the tagging actions from [AWS setup](/guide/aws-setup) to the operator role.

**IRSA reasons** (`IrsaDisabled`, `MechanismNotAllowed`, `OidcProviderMissing`, `OidcIssuerUnknown`, `ServiceAccountNotFound`, `ServiceAccountAnnotationNotOwned`): see the table in [IRSA](/guide/irsa#failures-you-may-see).
The most common is `OidcProviderMissing`: create the cluster's IAM OIDC provider once with `eksctl utils associate-iam-oidc-provider --cluster <name> --approve`.

**My pods did not pick up a new binding**: running pods keep the credentials they started with. Restart them after an IRSA annotation or Pod Identity association is created or changed.

**My annotated ServiceAccount shows `identity.platform.io/status: Invalid`**: the `identity.platform.io/message` annotation says why (unknown template, missing `resource-arns`, ...). See [Annotations](/guide/annotations).

**`/health/ready` failing, pods restarting**: the operator cannot call STS. Check the operator's Pod Identity association (namespace `pio`, ServiceAccount `eks-devops-operator`) and that the Pod Identity Agent add-on is installed.

**`NotConverged`**: after changes, AWS did not show the desired state within 30 seconds (IAM eventual consistency). It retries automatically.

**A resource is stuck in `Terminating`**: the finalizer is waiting for cleanup, often because AWS access is failing. Check the `CleanupFailed` event. Fix access and it completes.
As a last resort you can remove the finalizer `identity.platform.io/finalizer`, but then the AWS roles stay and must be removed by hand (look for the tag `pio:operator`).

**My pod still has no credentials**: confirm `kubectl -n <ns> get sa <name>` exists, the pod was started **after** the association was created (restart the pod), and the Pod Identity Agent add-on runs on the node.

**Access to a bucket in another account is denied**: the other account's resource policy must allow the role ARN from `status.roleArn`. See [AWS setup](/guide/aws-setup).

## Finding leftover AWS resources

```sh
aws iam list-roles --path-prefix /eks-devops-operator/
aws iam list-role-tags --role-name <name>     # pio:namespace, pio:service-account, pio:uid ...
```
