# AWS setup

Two kinds of IAM access exist, and they are never mixed:

- **Operator access** (this page): what the operator itself may do. Tight and administered by the platform team.
- **Workload access**: what an application is granted. It comes from a `PodIdentityAccess` and is checked against your `PodIdentityPolicy` guardrails.

Everything is created in the **AWS account and EKS cluster the operator runs in**. You need to set up two things once, on a cluster that has the
**EKS Pod Identity Agent** add-on (`aws eks create-addon --cluster-name CLUSTER_NAME --addon-name eks-pod-identity-agent`).

## 1. The permissions boundary

Every role the operator creates gets this boundary. It is a ceiling: even if a request slipped past validation, the role cannot exceed it.
Create it in the cluster account at the path and name configured in `permissionsBoundary`
(default `/eks-devops-operator/standard-workload`). Allow only the services you intend teams to use:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": ["s3:*", "sqs:*", "dynamodb:*", "secretsmanager:GetSecretValue"], "Resource": "*" }
  ]
}
```

## 2. The operator role

The operator runs with **EKS Pod Identity, not static keys**. Create a role the operator pods can assume and associate it with the operator ServiceAccount.

Trust policy:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Service": "pods.eks.amazonaws.com" },
    "Action": ["sts:AssumeRole", "sts:TagSession"]
  }]
}
```

Permissions (replace `ACCOUNT`, `CLUSTER_NAME` and `REGION`):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ManageOwnRoles",
      "Effect": "Allow",
      "Action": ["iam:GetRole", "iam:UpdateAssumeRolePolicy", "iam:PutRolePolicy", "iam:DeleteRolePolicy", "iam:GetRolePolicy",
                 "iam:ListRolePolicies", "iam:AttachRolePolicy", "iam:DetachRolePolicy", "iam:ListAttachedRolePolicies",
                 "iam:TagRole", "iam:UntagRole", "iam:ListRoleTags", "iam:DeleteRole", "iam:DeleteRolePermissionsBoundary"],
      "Resource": "arn:aws:iam::ACCOUNT:role/eks-devops-operator/*"
    },
    {
      "Sid": "CreateRolesOnlyWithBoundary",
      "Effect": "Allow",
      "Action": ["iam:CreateRole", "iam:PutRolePermissionsBoundary"],
      "Resource": "arn:aws:iam::ACCOUNT:role/eks-devops-operator/*",
      "Condition": { "StringEquals": { "iam:PermissionsBoundary": "arn:aws:iam::ACCOUNT:policy/eks-devops-operator/standard-workload" } }
    },
    {
      "Sid": "PassOnlyToPodIdentity",
      "Effect": "Allow",
      "Action": "iam:PassRole",
      "Resource": "arn:aws:iam::ACCOUNT:role/eks-devops-operator/*",
      "Condition": { "StringEquals": { "iam:PassedToService": "pods.eks.amazonaws.com" } }
    },
    {
      "Sid": "PodIdentityAssociations",
      "Effect": "Allow",
      "Action": ["eks:CreatePodIdentityAssociation", "eks:DescribePodIdentityAssociation", "eks:ListPodIdentityAssociations",
                 "eks:UpdatePodIdentityAssociation", "eks:DeletePodIdentityAssociation", "eks:TagResource", "eks:UntagResource", "eks:ListTagsForResource"],
      "Resource": ["arn:aws:eks:REGION:ACCOUNT:cluster/CLUSTER_NAME", "arn:aws:eks:REGION:ACCOUNT:podidentityassociation/CLUSTER_NAME/*"]
    }
  ]
}
```

::: tip If you allow managed policies
`iam:AttachRolePolicy` can attach any policy to roles under the path. If your `PodIdentityPolicy` allows `allowedManagedPolicyArns`, add a condition on `iam:PolicyARN` that lists
only those ARNs, so the IAM permission matches the operator guardrail. Without managed policies, you can drop `AttachRolePolicy` and `DetachRolePolicy`.
:::

Then associate the role with the operator ServiceAccount (Terraform):

```hcl
resource "aws_eks_pod_identity_association" "operator" {
  cluster_name    = var.cluster_name
  namespace       = "pio"
  service_account = "eks-devops-operator"
  role_arn        = aws_iam_role.operator.arn
}
```

Never give the operator role `iam:*`, access to roles outside `/eks-devops-operator/`, or the ability to change the boundary policy itself.

### Extra permissions for IRSA

Only if you enable IRSA (`irsa.enabled`), add this statement to the operator role. The operator reads the cluster's OIDC issuer and checks that the IAM OIDC provider exists; it never creates the provider.

```json
{
  "Sid": "IrsaOnly",
  "Effect": "Allow",
  "Action": ["eks:DescribeCluster", "iam:GetOpenIDConnectProvider"],
  "Resource": ["arn:aws:eks:REGION:ACCOUNT:cluster/CLUSTER_NAME", "arn:aws:iam::ACCOUNT:oidc-provider/*"]
}
```

The provider itself is a one-time setup per cluster: `eksctl utils associate-iam-oidc-provider --cluster CLUSTER_NAME --approve`, or the Terraform resource `aws_iam_openid_connect_provider`. See [IRSA](/guide/irsa).

## Giving a workload access to another AWS account

The role always lives in the cluster account. To reach a resource in another account, name that resource's ARN in the request and let the other account allow the role:

```yaml
spec:
  serviceAccountName: reports
  statements:
    - actions: [s3:GetObject]
      resources: ["arn:aws:s3:::partner-shared-reports/*"]     # bucket owned by account 999999999999
```

```json
// bucket policy in account 999999999999
{
  "Effect": "Allow",
  "Principal": { "AWS": "arn:aws:iam::ACCOUNT:role/eks-devops-operator/pio-prod-reports-reports-1a2b3c4d" },
  "Action": "s3:GetObject",
  "Resource": "arn:aws:s3:::partner-shared-reports/*"
}
```

Use `allowedResourceArnPatterns` in the `PodIdentityPolicy` to decide which outside resources teams may name.
The role name is in `status.roleArn`. It is stable, so the bucket policy keeps working across reconciles.

## Verify

```sh
# From a pod running as the operator ServiceAccount:
aws sts get-caller-identity
```

The operator also checks its identity at startup (`/health/ready` stays unhealthy until STS answers).
