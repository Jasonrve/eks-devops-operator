# Examples

Each example below is a scenario we tested. The manifests are in [`examples/`](https://github.com/Jasonrve/eks-devops-operator/tree/main/examples),
and every one can be applied as shown. Replace the account IDs, role names and namespaces with your own.

Check any resource with:

```sh
kubectl -n <namespace> get podidentityaccess,irsaaccess <name> -o jsonpath='{.status.phase}{"\n"}{.status.roleArn}{"\n"}{.status.message}{"\n"}'
```

`phase: Ready` means AWS was read back and matches the request. `Invalid` means a guardrail refused it (nothing was written to AWS).
`Failed` means AWS refused it or failed (see the message).

## 1. Read S3 with Pod Identity

The basic case: a ServiceAccount gets read access to buckets through a `PodIdentityAccess`.

Policy needed (the default policy allows it when no `allowedActions` are narrowed):

```yaml
# examples/policy.yaml
apiVersion: identity.platform.io/v1alpha1
kind: IdentityPolicy
metadata:
  name: payments
  namespace: eks-devops-operator
spec:
  namespaces: [payments]
  allowedServices: [s3]
  allowedActions: [s3:GetObject, s3:ListBucket]
  allowedResourceArnPatterns: ["arn:aws:s3:::payments-*"]
```

Request, from [`examples/payments-s3.yaml`](https://github.com/Jasonrve/eks-devops-operator/blob/main/examples/payments-s3.yaml):

```sh
kubectl apply -f examples/payments-s3.yaml
kubectl -n payments get podidentityaccess payments-s3 -o jsonpath='{.status.phase} {.status.roleArn}{"\n"}'
```

Expected: `Ready arn:aws:iam::<cluster-account>:role/eks-devops-operator/<generated-name>`. The role is in the cluster account, under
`/eks-devops-operator/`, with the permissions boundary attached.

## 2. IRSA

The same kind of access through IRSA, with a separate kind, `IrsaAccess`. The chart needs `irsa.enabled=true` and the cluster needs an IAM
OIDC provider (see [IRSA](/guide/irsa)).

```sh
kubectl apply -f examples/payments-irsa.yaml
kubectl -n payments get irsaaccess payments-s3-irsa -o jsonpath='{.status.phase} {.status.roleArn}{"\n"}'
kubectl -n payments get serviceaccount payments-api -o jsonpath='{.metadata.annotations.eks\.amazonaws\.com/role-arn}{"\n"}'
```

Expected: `Ready`, and the ServiceAccount carries the `eks.amazonaws.com/role-arn` annotation. Pods started after that receive the role through
IRSA.

Tested result: the IRSA role trusts the cluster's OIDC provider, sits under `/eks-devops-operator/`, and has the approved boundary.

## 3. Friendly role name and Retain

Set the role name yourself, and keep the role when the resource is deleted:

```sh
kubectl apply -f examples/payments-custom-role-name.yaml
```

The role is named exactly `payments-reports-reader`. There's no hash or prefix. `deletionPolicy: Retain` removes only the association when the
resource is deleted, and leaves the role in place. The chart's default policy allows `Retain`. A policy that allows only `Delete` refuses it
with `DeletionPolicyNotAllowed`.

**Name already taken (`RoleNameTaken`).** If a role named `payments-reports-reader` already exists and was not created for this resource,
the request fails with `RoleNameTaken`, and the existing role is not touched:

```sh
kubectl -n payments get podidentityaccess payments-reports-reader -o jsonpath='{.status.phase} {.status.message}{"\n"}'
# Failed IAM role arn:aws:iam::...:role/eks-devops-operator/payments-reports-reader already exists and was not created for this resource; choose another roleName
```

Choose another `roleName`, or delete the old role if nothing uses it.

## 4. Cross-account role assumption: one namespace

A pod in namespace `payments` lists the buckets of account `634107240400` by assuming `temp-test-s3-bucket-list-reader`. This is the
full test we ran.

**Step 1: the platform team enables the grant** with [`examples/values-cross-account.yaml`](https://github.com/Jasonrve/eks-devops-operator/blob/main/examples/values-cross-account.yaml)
(option 1: one namespace, one role):

```sh
helm upgrade eks-devops-operator charts/eks-devops-operator -n eks-devops-operator --reuse-values -f examples/values-cross-account.yaml
```

This renders an `IdentityPolicy` named `cross-account`, which allows only `sts:AssumeRole` and `sts:TagSession` on that role ARN, for
namespace `payments`.

**Step 2: the target account trusts the operator's role.** The role `temp-test-s3-bucket-list-reader` in account `634107240400` gets this
trust policy. The principal is the role the operator creates for the request, which is named by `roleName` in the request below:

```json
{
    "Version": "2012-10-17",
    "Statement": [{
        "Effect": "Allow",
        "Principal": {"AWS": "arn:aws:iam::622515864211:role/eks-devops-operator/payments-s3-list"},
        "Action": ["sts:AssumeRole", "sts:TagSession"]
    }]
}
```

Both actions are needed. Pod Identity sessions carry session tags, so the CLI sends `sts:TagSession` as well. Without it the assume step
fails with `AccessDenied`.

**Step 3: the team applies the request.** [`examples/cross-account-assume-role.yaml`](https://github.com/Jasonrve/eks-devops-operator/blob/main/examples/cross-account-assume-role.yaml)
contains the ServiceAccount, the `PodIdentityAccess` and a verification pod:

```sh
kubectl apply -f examples/cross-account-assume-role.yaml
kubectl -n payments get podidentityaccess payments-s3-list -o jsonpath='{.status.phase} {.status.roleArn}{"\n"}'
```

Expected: `Ready arn:aws:iam::622515864211:role/eks-devops-operator/payments-s3-list`.

**Step 4: check the result** from the verification pod's logs:

```sh
kubectl -n payments wait --for=jsonpath='{.status.phase}'=Succeeded pod/s3-list-test --timeout=120s
kubectl -n payments logs s3-list-test
```

Expected output: the pod's identity (`arn:aws:sts::622515864211:assumed-role/payments-s3-list/...`), then the assumed identity
(`arn:aws:sts::634107240400:assumed-role/temp-test-s3-bucket-list-reader/...`), then the bucket names of account `634107240400`.

The verification pod uses the public AWS CLI image. On an airgapped cluster, mirror `public.ecr.aws/aws-cli/aws-cli` into your registry and
change the image.

**Why each part is needed:**
- The `cross-account` policy allows `sts:AssumeRole` explicitly. `sts` is an escalating service, and an explicit grant is the only way
  past the check (see [explicit grants](/reference/identitypolicy#explicit-grants-for-escalating-actions)).
- The workload role needs both actions in its own permissions, not only the target trust. The statement in the request lists both.
- The target role's trust must name the operator-created role, which is `/eks-devops-operator/<roleName>`.

## 5. Cross-account: every namespace, one role

Let every namespace assume the same role. Use option 2 from [`examples/values-cross-account.yaml`](https://github.com/Jasonrve/eks-devops-operator/blob/main/examples/values-cross-account.yaml):

```yaml
crossAccountPolicy:
  namespaces: ["*"]
  roleArns:
    - arn:aws:iam::634107240400:role/temp-test-s3-bucket-list-reader
```

`["*"]` renders the `cross-account` policy with an empty namespace list, which means every namespace. Each namespace still needs its own
`PodIdentityAccess` (as in section 4), and the target role's trust must name each namespace's operator role. List explicit namespaces
instead when you don't want every namespace.

## 6. Cross-account: every namespace, any account and any role

For a broad grant, use a pattern. `*` matches any part of the ARN:

```yaml
crossAccountPolicy:
  namespaces: ["*"]
  roleArns:
    - "arn:aws:iam::*:role/*"
```

Any namespace may now assume any role in any account, provided that role's trust policy trusts the operator's role. This is broad, so
keep it to a platform namespace or use a narrower pattern such as `arn:aws:iam::*:role/reports-*`.

## 7. Permissions boundary

Every role the operator creates gets the boundary configured as `permissionsBoundary`. Two failures are reported clearly:

- **Boundary missing (`PermissionsBoundaryNotFound`).** Set the value to a policy that doesn't exist, for example
  `--set permissionsBoundary=/no-such-boundary`. A request then fails with:

  ```text
  Failed PermissionsBoundaryNotFound: role <name> not changed: permissions boundary arn:aws:iam::...:policy/no-such-boundary does not exist in this account.
  ```

- **Boundary refused (`PermissionsBoundaryDenied`).** IAM refuses a boundary when the operator role's `iam:CreateRole` condition doesn't
  allow it, or when the account doesn't permit it. The message names the boundary and the condition to check.

Nothing is created when the boundary check fails. Fix the boundary, and the request succeeds at its next reconcile.

## 8. Refused requests

A policy refuses what it doesn't allow, and reports every problem at once. For example, with a policy that denies deletes:

```yaml
spec:
  namespaces: [reports]
  allowedServices: [s3]
  allowedActions: ["s3:Get*", "s3:List*", "s3:DeleteObject"]
  deniedActions: ["s3:Delete*"]
  allowedResourceArnPatterns: ["arn:aws:s3:::reports-*"]
```

a `PodIdentityAccess` in namespace `reports` that requests `s3:DeleteObject` is refused. Its status shows:

```text
Invalid ActionDenied: action 's3:DeleteObject' is denied by policy 'reports-read'
```

Nothing is written to AWS. The full list of codes is in [validation errors](/reference/validation-errors).

## 9. Escalating actions need an explicit grant

`sts:AssumeRole` is an escalating action. The default policy alone refuses it:

```text
Invalid PrivilegeEscalation: action 'sts:AssumeRole' can escalate privileges
```

It's allowed only when a governing policy names the exact action, and that policy's resource patterns cover the resources. The chart's
`crossAccountPolicy` does this for you (sections 4 to 6). A wildcard such as `sts:*` is not an explicit grant.
