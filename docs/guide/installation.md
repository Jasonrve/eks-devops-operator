# Installing with Helm

Prerequisites: an EKS cluster with the **EKS Pod Identity Agent** add-on, and the [AWS setup](/guide/aws-setup) done.

## Install

Create a values file with your cluster, tags and guardrails:

```yaml
# my-values.yaml
image:
  repository: ghcr.io/jasonrve/eks-devops-operator
cluster:
  name: prod-eks-1
  region: eu-west-1
environment: prod                       # recorded in the pio:environment tag

# added to every role and association the operator creates
tags:
  CostCenter: cc-1234
  ManagedBy: platform-team

# rendered as a PodIdentityPolicy named "default" in the release namespace
policy:
  allowedServices: [s3, sqs]
  allowedActions: [s3:GetObject, s3:ListBucket, sqs:SendMessage]
  allowedResourceArnPatterns: ["arn:aws:s3:::payments-*", "arn:aws:sqs:eu-west-1:*:payments-*"]
```

```sh
helm install pio oci://ghcr.io/jasonrve/charts/eks-devops-operator --version 0.1.0 \
  --namespace pio --create-namespace -f my-values.yaml
```

Guardrails are `PodIdentityPolicy` objects. The chart creates `default` from `policy:`; add more with `kubectl apply` **in the same namespace as the operator**
(`pio` here). The operator ignores policies in any other namespace, so grant write access to `pio` to platform admins only. See [PodIdentityPolicy](/reference/podidentitypolicy).

Make sure the operator's Pod Identity association exists for `pio/eks-devops-operator` (see [AWS setup](/guide/aws-setup)).
The pods become ready only after they can call STS, so `kubectl -n pio get pods` tells you if the association works.

## Upgrade

```sh
helm upgrade pio oci://ghcr.io/jasonrve/charts/eks-devops-operator --version 0.1.0 -n pio -f my-values.yaml
```

Changing `tags` rolls the operator pods (the config checksum changes) and updates the tags on existing roles and associations at their next reconcile.

::: warning CRDs
Helm installs files from the chart's `crds/` folder on **install only**; it never updates them on upgrade. When the CRD changes, apply it yourself first:

```sh
kubectl apply -f deploy/helm/eks-devops-operator/crds/
```
:::

The API is `v1alpha1`. Expect breaking changes between releases until it is promoted; they are listed in the release notes.

## Uninstall

```sh
kubectl delete podidentityaccess -A --all      # lets the operator clean up AWS first
helm uninstall pio -n pio
```

Delete the resources **before** uninstalling. Without the operator running, their finalizers cannot be processed and the AWS roles stay behind.
The CRDs are kept by Helm; remove them with `kubectl delete crd podidentityaccesses.identity.platform.io podidentitypolicies.identity.platform.io` once nothing uses them.

## High availability

The default is two replicas with leader election: one reconciles, the other takes over within the lease duration if it dies.
A PodDisruptionBudget keeps one pod during node drains. The container is non-root, with a read-only root filesystem, all capabilities dropped and a `RuntimeDefault` seccomp profile.
