# Getting started

Run the whole thing on your laptop with **no AWS account**. [Floci](https://github.com/floci-io/floci) emulates AWS (IAM, STS, EKS) and k3s provides a real Kubernetes API.

You need Docker, the .NET 10 SDK and a POSIX shell (Git Bash or WSL on Windows).

::: warning Emulator limits
Floci does not enforce IAM trust policies and does not inject credentials into pods. This setup proves the **management plane**
(roles, policies, tags and associations are created, repaired and removed). It does not prove that a pod can assume the role. Use a real EKS cluster for that.
Floci also cannot tag Pod Identity associations in place, so a tag change recreates the association there. AWS tags it in place.
:::

::: tip Using a real AWS account?
This page is the local walkthrough. The role and policy the operator needs on real AWS are in [Running on a real EKS cluster](#running-on-a-real-eks-cluster) at the end.
:::

## 1. Start Floci and Kubernetes

```sh
docker run -d --name floci -p 4566:4566 floci/floci:latest
docker run -d --name k3s --privileged -p 6443:6443 rancher/k3s:v1.31.5-k3s1 \
  server --disable=traefik --disable=metrics-server --tls-san=127.0.0.1

# wait for the node, then export a kubeconfig
until docker exec k3s kubectl get nodes 2>/dev/null | grep -q " Ready"; do sleep 3; done
docker exec k3s cat /etc/rancher/k3s/k3s.yaml > /tmp/pio-kubeconfig
```

## 2. Seed the fake AWS

This creates what a real environment already has: an EKS cluster named `e2e` and the permissions boundary policy, in account `111111111111`.

```sh
sh local/seed-floci.sh
```

## 3. Install the CRDs, a policy, and run the operator

```sh
for f in deploy/helm/eks-devops-operator/crds/*.yaml; do docker exec -i k3s kubectl apply -f - < $f; done

docker exec k3s kubectl create namespace pio     # the operator namespace: the only place PodIdentityPolicy objects are read
docker exec -i k3s kubectl apply -f - <<'EOF'
apiVersion: identity.platform.io/v1alpha1
kind: PodIdentityPolicy
metadata: {name: default, namespace: pio}
spec:
  allowedServices: [s3]
  allowedActions: [s3:GetObject, s3:ListBucket]
  allowedResourceArnPatterns: ["arn:aws:s3:::payments-*"]
  allowedDeletionPolicies: [Delete, Retain]
EOF

# global tags: a JSON file here, a ConfigMap in the Helm chart
echo '{"Operator":{"Tags":{"CostCenter":"cc-1234"}}}' > /tmp/pio-operator.json

export KUBECONFIG=/tmp/pio-kubeconfig OPERATOR_CONFIG_FILE=/tmp/pio-operator.json
export AWS_ACCESS_KEY_ID=111111111111 AWS_SECRET_ACCESS_KEY=test AWS_REGION=us-east-1   # Floci: the key id selects the account
export Aws__ServiceUrl=http://localhost:4566 Operator__ClusterName=e2e Operator__LeaderElection=false
export Operator__PolicyNamespace=pio          # where PodIdentityPolicy objects are read (in a cluster: the operator pod's own namespace)

dotnet run --project src/EksDevopsOperator
```

The first seconds may log watcher retries while k3s finishes starting. That is normal.

## 4. Request access

In another terminal:

```sh
docker exec k3s kubectl create namespace payments
docker exec k3s kubectl -n payments create serviceaccount payments-api
docker exec -i k3s kubectl apply -f - < examples/payments-s3.yaml

docker exec k3s kubectl -n payments get podidentityaccesses
```

```text
NAME          PHASE   SERVICEACCOUNT   ROLE
payments-s3   Ready   payments-api     arn:aws:iam::111111111111:role/eks-devops-operator/pio-e2e-payments-payments-api-267dc7e3
```

See what the operator did, and what it created in the fake AWS:

```sh
docker exec k3s kubectl -n payments get events --field-selector involvedObject.name=payments-s3

aws() { docker run --rm --add-host host.docker.internal:host-gateway \
  -e AWS_ACCESS_KEY_ID=111111111111 -e AWS_SECRET_ACCESS_KEY=test -e AWS_DEFAULT_REGION=us-east-1 \
  amazon/aws-cli --endpoint-url http://host.docker.internal:4566 "$@"; }

aws eks list-pod-identity-associations --cluster-name e2e
aws iam list-roles --path-prefix /eks-devops-operator/
aws iam list-role-tags --role-name <role name from status>      # CostCenter (global), App (request) and the pio: ownership tags
```

## 5. Change tags, then clean up

```sh
docker exec k3s kubectl -n payments patch podidentityaccess payments-s3 --type merge -p '{"spec":{"tags":{"App":"billing"}}}'
# the role and association tags are updated; look at the events again

docker exec k3s kubectl -n payments delete podidentityaccess payments-s3   # role and association are removed
docker rm -f floci k3s
```

## Try rejected requests

Nothing is created in AWS and the reason is in the status:

```sh
docker exec -i k3s kubectl apply -f - <<'EOF'
apiVersion: identity.platform.io/v1alpha1
kind: PodIdentityAccess
metadata: {name: too-much, namespace: payments}
spec:
  serviceAccountName: other
  tags: {CostCenter: mine}          # a global tag: cannot be overridden
  statements:
    - actions: ["iam:*"]
      resources: ["*"]
EOF
docker exec k3s kubectl -n payments get podidentityaccess too-much -o jsonpath='{.status.message}'
```

## Running on a real EKS cluster

Floci accepts anything, so the local run needs no IAM setup. On real AWS, the operator needs the following, in the **account and cluster it runs in**. The full explanation and the cross-account notes are in [AWS setup](/guide/aws-setup).

**1. A permissions boundary policy.** Every role the operator creates is capped by it. Create it at `/eks-devops-operator/standard-workload` (or change `permissionsBoundary` in the chart) and allow only the services teams may use:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": ["s3:*", "sqs:*", "dynamodb:*", "secretsmanager:GetSecretValue"], "Resource": "*" }
  ]
}
```

**2. An IAM role for the operator**, assumed through EKS Pod Identity (no static keys). Replace `ACCOUNT`, `REGION` and `CLUSTER_NAME`.

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

Permissions policy:

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

Only if you turn on IRSA (`irsa.enabled`), add one more statement:

```json
{
  "Sid": "IrsaOnly",
  "Effect": "Allow",
  "Action": ["eks:DescribeCluster", "iam:GetOpenIDConnectProvider"],
  "Resource": ["arn:aws:eks:REGION:ACCOUNT:cluster/CLUSTER_NAME", "arn:aws:iam::ACCOUNT:oidc-provider/*"]
}
```

**3. Cluster add-ons and associations.** The cluster needs the **EKS Pod Identity Agent** add-on (for the operator and for Pod Identity workloads), and the operator's ServiceAccount must be associated with the role from step 2:

```sh
aws eks create-addon --cluster-name CLUSTER_NAME --addon-name eks-pod-identity-agent
aws eks create-pod-identity-association --cluster-name CLUSTER_NAME   --namespace pio --service-account eks-devops-operator --role-arn arn:aws:iam::ACCOUNT:role/eks-devops-operator-controller
```

For IRSA only, the cluster's IAM OIDC provider must also exist: `eksctl utils associate-iam-oidc-provider --cluster CLUSTER_NAME --approve`.

**4. Install the chart** in the namespace used above, with `cluster.name` set. See [Installation](/guide/installation) for the values.

Never give the operator role `iam:*`, access to roles outside `/eks-devops-operator/`, or the ability to change the boundary policy itself. Check the identity from a pod running as the operator with `aws sts get-caller-identity`.
