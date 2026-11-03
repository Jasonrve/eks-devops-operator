# IdentityPolicy

`identity.platform.io/v1alpha1`, kind `IdentityPolicy`, namespaced, short name `pip`.

An `IdentityPolicy` is a guardrail: it says what teams may request through a `PodIdentityAccess` or an `IrsaAccess`. Teams never write
policies. Platform administrators do, and the operator enforces them on every request.

Policies are read **only from the operator's own namespace** (the namespace the operator is deployed in, for example `eks-devops-operator`).
A policy in any other namespace is ignored. A team that can create objects in its own namespace therefore cannot widen what is allowed.
Give write access to the operator namespace only to platform administrators.

## Minimal example

```yaml
apiVersion: identity.platform.io/v1alpha1
kind: IdentityPolicy
metadata:
  name: payments
  namespace: eks-devops-operator    # the operator's namespace
spec:
  namespaces: [payments]
  allowedServices: [s3]
  allowedActions: [s3:GetObject, s3:ListBucket]
  allowedResourceArnPatterns: ["arn:aws:s3:::payments-*"]
```

This lets namespace `payments` request read access to buckets named `payments-*`, and nothing else. Every other field keeps its default.

## Full example

Every field, set to a value. Most policies need only a few of these.

```yaml
apiVersion: identity.platform.io/v1alpha1
kind: IdentityPolicy
metadata:
  name: payments-strict
  namespace: eks-devops-operator
spec:
  namespaces: [payments]                         # which namespaces this policy governs
  allowedMechanisms: [PodIdentity]               # no IRSA for these namespaces
  allowedServices: [s3, sqs]                     # IAM service prefixes
  allowedActions: [s3:GetObject, s3:ListBucket, sqs:SendMessage]
  deniedActions: ["s3:Delete*", "sqs:Purge*"]    # always refused
  allowedResourceArnPatterns: ["arn:aws:s3:::payments-*", "arn:aws:sqs:*:*:payments-*"]
  allowedManagedPolicyArns: []                   # no managed policies
  allowedDeletionPolicies: [Delete]              # teams may not choose Retain
  requirePermissionsBoundary: true               # a boundary must be configured
  allowWildcardActions: false
  allowWildcardResources: false
  allowPrivilegeEscalationActions: false
```

## How a request is decided

Every `PodIdentityAccess` and `IrsaAccess` is checked on each reconcile, before anything is written to AWS:

1. **Namespace coverage.** The policies that govern the request's namespace are collected. A policy governs a namespace when its
   `namespaces` list is empty (all namespaces) or contains that namespace. If none governs it, the request is rejected with
   `NamespaceNotAuthorized`. This is deny by default.
2. **Every governing policy must accept the request.** Each policy is checked on its own. A request that passes one policy and fails
   another is rejected. Adding a policy can only make requests stricter, never looser.
3. **Common checks** run once, whatever the policies say: the names are valid Kubernetes names, a requested `roleName` is well formed and not
   a protected pattern, the tags are allowed, and there are at most 20 statements, none empty, and the request grants something.
4. **All problems are reported together.** A rejected request has `phase: Invalid`, and `status.message` lists every failing check as
   `Code: explanation`. See [validation errors](/reference/validation-errors).

A rejected request writes nothing to AWS. A resource that was valid and becomes invalid after a policy change keeps its existing AWS role
and association. Its status changes to `Invalid`, and nothing is deleted.

Policies are cached for up to 5 seconds, so a change takes effect within seconds, at the next reconcile.

## Matching rules

- **Wildcards:** `*` matches any run of characters. The operator does not support `?` in these lists.
- **Namespaces** (`namespaces`) are matched exactly.
- **Services** (`allowedServices`) are matched case-insensitively against the part of the action before the colon.
- **Actions** (`allowedActions`, `deniedActions`) are matched case-insensitively.
- **ARNs** (`allowedResourceArnPatterns`, `allowedManagedPolicyArns`) are matched against the whole ARN string.
- **Denied beats allowed.** An action that matches `deniedActions` is refused even if it also matches `allowedActions`.

## Field reference

### `namespaces`
- **Type:** list of namespace names. **Default:** `[]` (all namespaces).
- Which namespaces this policy governs. An empty list governs every namespace. Remember that a policy with an empty list
  also applies to namespaces you add later.
- Refused with `NamespaceNotAuthorized` when no policy governs the request's namespace.

### `allowedMechanisms`
- **Type:** list of `PodIdentity`, `IRSA`, or `*`. **Default:** `["*"]` (both).
- Which kinds of access the namespace may request. `PodIdentity` covers `PodIdentityAccess`; `IRSA` covers `IrsaAccess`.
  `[]` allows neither.
- IRSA also needs `irsa.enabled=true` on the operator. Without it, IRSA requests fail with `IrsaDisabled`, whatever this field says.
- Refused with `MechanismNotAllowed`.

### `allowedServices`
- **Type:** list of IAM service prefixes, such as `s3`, `sqs`, `dynamodb`. **Default:** `["*"]` (every service).
- The service part of each requested action must be on the list. `[]` allows no service.
- Refused with `ServiceNotAllowed`.

### `allowedActions`
- **Type:** list of action globs, such as `s3:GetObject` or `s3:Get*`. **Default:** `["*"]` (every action).
- Each requested action must match at least one entry. `[]` allows no action.
- The default `*` still refuses wildcard actions like `s3:*` unless `allowWildcardActions` is true (see below).
- Refused with `ActionNotAllowed`.

### `deniedActions`
- **Type:** list of action globs. **Default:** `[]`.
- Actions that are always refused, even when they also match `allowedActions`.
- A requested wildcard is also checked against this list in the other direction, so `s3:*` is refused when `deniedActions` contains `s3:Delete*`.
- Refused with `ActionDenied`.

### `allowedResourceArnPatterns`
- **Type:** list of ARN globs. **Default:** `[]` (any ARN).
- Every requested resource must match at least one pattern when the list is not empty. A resource in another AWS account is allowed
  if it matches a pattern, for example a bucket in a partner account that trusts the role.
- Refused with `ResourceOutOfScope`. Resources must also be in the cluster's partition (`PartitionMismatch`) and be real ARNs (`InvalidResource`).

### `allowedManagedPolicyArns`
- **Type:** list of managed policy ARN globs. **Default:** `[]` (none).
- The managed policies a team may attach to its role through `managedPolicies`. Empty means no managed policies are allowed.
- Refused with `ManagedPolicyNotAllowed`.

### `allowedDeletionPolicies`
- **Type:** list of `Delete` and/or `Retain`. **Default:** `[Delete]`.
- `Delete` removes the role and association when the request is deleted. `Retain` removes only the association and leaves the role.
- Refused with `DeletionPolicyNotAllowed` when the request's `deletionPolicy` is not on the list. The default allows only `Delete`, so a team
  that wants `Retain` needs it added here.

### `requirePermissionsBoundary`
- **Type:** boolean. **Default:** `true`.
- When `true`, the operator must have a permissions boundary configured (the Helm value `permissionsBoundary`). If none is configured,
  the request is refused with `BoundaryRequired`.
- The boundary itself is attached to every role the operator creates whenever `permissionsBoundary` is set, whatever this flag says. This flag
  only checks that a boundary exists. See [permissions boundary](#permissions-boundary) below.

### `allowWildcardActions`
- **Type:** boolean. **Default:** `false`.
- `true` permits wildcard actions such as `s3:*` or `s3:Get*` in a request. Without it, any `*` or `?` in an action is refused with
  `WildcardAction`.

### `allowWildcardResources`
- **Type:** boolean. **Default:** `false`.
- `true` permits `*` as a resource and ARNs that are too broad to be useful (empty or fully wildcard resource names). Without it they are refused
  with `WildcardResource`.

### `allowPrivilegeEscalationActions`
- **Type:** boolean. **Default:** `false`.
- Actions that can grant more access than they appear to are refused with `PrivilegeEscalation` unless this is `true`, or unless a policy
  names the action exactly (see [explicit grants](#explicit-grants-for-escalating-actions) below). The check covers:
  - every action of the services `iam`, `sts`, `organizations`, `account`, `sso`, `sso-admin`, `identitystore`, `cloudformation`
  - any action in the service `*`
  - these specific actions: `ec2:AssociateIamInstanceProfile`, `ec2:ReplaceIamInstanceProfileAssociation`, `lambda:CreateFunction`,
    `lambda:UpdateFunctionCode`, `lambda:UpdateFunctionConfiguration`, `kms:CreateGrant`, `kms:PutKeyPolicy`, `s3:PutBucketPolicy`,
    `s3:PutBucketAcl`, `secretsmanager:PutResourcePolicy`, `sqs:AddPermission`, `sns:AddPermission`, `ssm:SendCommand`, `ssm:StartSession`
  - `sts:GetCallerIdentity` is always allowed

## Explicit grants for escalating actions

`allowPrivilegeEscalationActions: true` lets a policy allow every escalating action, which is broad. A narrower route is an explicit grant:
an escalating action is allowed for a request when **a governing policy names that exact action** (no `*` in it) and that policy's
`allowedResourceArnPatterns` cover **every** resource of the statement.

- The granting policy does not need `allowPrivilegeEscalationActions`. The grant is per action and per resource pattern.
- The other governing policies still apply. A default policy with `allowedActions: ["*"]` accepts the action, so the grant is enough.
- A wildcard such as `sts:*` does not count as a grant, and neither does an action with no resource patterns.

This is how the chart's `crossAccountPolicy` works (see below).

## Cross-account role assumption

A pod can assume a role in another AWS account. The role the operator creates (in the cluster account) is the one the pod gets from Pod
Identity. It then calls `sts:AssumeRole` on the role in the other account. Two things must allow it:

1. **The IdentityPolicy** for the namespace grants `sts:AssumeRole` and `sts:TagSession` on that exact role ARN. `sts:AssumeRole` is an
   escalating action, so the grant must be explicit (see above).
2. **The target role's trust policy** (in the other account) trusts the operator-created role. Account owners set this; the operator cannot.

The Helm chart can render the grant for you:

```yaml
# values.yaml
crossAccountPolicy:
  namespaces: [payments]
  roleArns:
    - arn:aws:iam::634107240400:role/temp-test-s3-bucket-list-reader
```

This renders an `IdentityPolicy` named `cross-account`, which allows only `sts:AssumeRole` and `sts:TagSession` on those ARNs, for those
namespaces. Namespaces are required when `roleArns` is set, so the grant cannot apply to every namespace.

The team's `PodIdentityAccess` then asks for the assume-role permission, and the operator creates the role and association:

```yaml
apiVersion: identity.platform.io/v1alpha1
kind: PodIdentityAccess
metadata:
  name: payments-s3-list-other-account
  namespace: payments
spec:
  serviceAccountName: payments-s3-list
  roleName: payments-s3-list
  statements:
    - sid: AssumeS3ListReader
      actions: [sts:AssumeRole, sts:TagSession]
      resources: [arn:aws:iam::634107240400:role/temp-test-s3-bucket-list-reader]
```

The trust policy the other account needs for this example (the principal is the role the operator created):

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

Inside the pod, the AWS SDK or CLI calls `aws sts assume-role` with the target ARN and uses the returned credentials. Trying it with the
CLI:

```sh
CREDS=$(aws sts assume-role --role-arn arn:aws:iam::634107240400:role/temp-test-s3-bucket-list-reader \
  --role-session-name payments --query "Credentials.[AccessKeyId,SecretAccessKey,SessionToken]" --output text)
set -- $CREDS; export AWS_ACCESS_KEY_ID=$1 AWS_SECRET_ACCESS_KEY=$2 AWS_SESSION_TOKEN=$3
aws s3api list-buckets
```

The role in the other account needs its own permissions (for example `s3:ListAllMyBuckets`). The operator grants nothing there.

## Permissions boundary

The permissions boundary is a ceiling on every role the operator creates. It is set by the Helm value `permissionsBoundary`, not by
`IdentityPolicy`, and it is attached to every role whenever that value is set. It does not decide who can request what.
An `IdentityPolicy` is still required: with no policy, every request is refused, even with a boundary set.

## Combining policies

Several policies can govern the same namespace. A request must pass **every** one of them, so the strictest rule wins.

```yaml
# every namespace may read logs, and deny deleting anything
apiVersion: identity.platform.io/v1alpha1
kind: IdentityPolicy
metadata: {name: platform-logs, namespace: eks-devops-operator}
spec:
  allowedServices: [logs]
  allowedActions: [logs:GetLogEvents, logs:FilterLogEvents]
---
# payments may additionally read invoices, and nothing else from S3
apiVersion: identity.platform.io/v1alpha1
kind: IdentityPolicy
metadata: {name: payments-s3, namespace: eks-devops-operator}
spec:
  namespaces: [payments]
  allowedServices: [s3]
  allowedActions: [s3:GetObject]
  allowedResourceArnPatterns: ["arn:aws:s3:::payments-invoices/*"]
```

The first policy governs every namespace, and the second governs only `payments`. For a `payments` request:

- `logs:GetLogEvents` passes the first policy. The second policy does not list `logs`, so the request is refused.
- `s3:GetObject` on `arn:aws:s3:::payments-invoices/2026-01.pdf` fails the first policy, which allows only `logs`, so it is refused as well.

A `payments` request therefore needs an action that both policies allow, which here is none. Combining policies narrows access, never
widens it. A namespace that only the first policy governs, such as `orders`, may request `logs` actions and nothing else.

## Worked examples

**Read-only S3 for one namespace, Delete never allowed:**

```yaml
spec:
  namespaces: [reports]
  allowedServices: [s3]
  allowedActions: ["s3:Get*", "s3:List*"]
  deniedActions: ["s3:Delete*"]
  allowedResourceArnPatterns: ["arn:aws:s3:::reports-*"]
```

**A namespace that may use IRSA only:**

```yaml
spec:
  namespaces: [ci-builds]
  allowedMechanisms: [IRSA]
  allowedServices: [ecr]
  allowedActions: [ecr:GetAuthorizationToken, ecr:BatchGetImage]
```

**A queue consumer that may retain its role when it is deleted:**

```yaml
spec:
  namespaces: [orders]
  allowedServices: [sqs]
  allowedActions: [sqs:ReceiveMessage, sqs:DeleteMessage, sqs:GetQueueAttributes]
  allowedResourceArnPatterns: ["arn:aws:sqs:*:*:orders-*"]
  allowedDeletionPolicies: [Delete, Retain]
```

**Not recommended:** a policy that allows everything for a namespace removes the guardrail entirely. Use it only for a platform namespace
that administrators run.

```yaml
spec:
  namespaces: [platform-admin]
  allowWildcardActions: true
  allowWildcardResources: true
  allowPrivilegeEscalationActions: true
```

## Common mistakes

- **The policy is in the wrong namespace.** Policies outside the operator namespace are ignored, so the request is refused with
  `NamespaceNotAuthorized`.
- **Forgetting the namespace in `namespaces`.** A policy with `namespaces: [payments]` does not govern `reports`.
- **Using `s3:*`.** Wildcard actions are refused by default. List the exact actions, or set `allowWildcardActions: true` for one policy.
- **Expecting `deniedActions` to be ignored when an action is allowed.** It is not: denied beats allowed.
- **An empty `allowedServices`, `allowedActions` or `allowedMechanisms`.** An empty list allows nothing, unlike the default `["*"]`.
- **Requesting `Retain` with the default policy.** `allowedDeletionPolicies` defaults to `[Delete]`.

## Changing a policy

- The operator picks up a policy change within 5 seconds. Each resource is evaluated against the new policy at its next reconcile.
- Tightening a policy makes matching resources `Invalid` when they reconcile. Their existing AWS roles and associations are left in place and are not deleted.
- Loosening a policy lets refused requests succeed at their next reconcile.

## Installing

The Helm chart creates one policy named `default`, from the `policy:` values. Turn that off with `policy.create=false` if you manage all policies
yourself. Add more at any time with `kubectl apply`, in the operator namespace:

```sh
kubectl apply -f examples/policy.yaml
kubectl -n eks-devops-operator get identitypolicies
```

The operator reads policies with a namespaced Role in its own namespace, so it cannot read policies in other namespaces even if asked.
