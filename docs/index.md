---
title: What it does
---

# EKS DevOps Operator

Application teams ask for AWS permissions with one Kubernetes resource. The operator builds the IAM role and the EKS Pod Identity association.

```yaml
apiVersion: identity.platform.io/v1alpha1
kind: PodIdentityAccess
metadata:
  name: payments-s3
  namespace: payments
spec:
  serviceAccountName: payments-api
  tags: {App: payments}
  statements:
    - sid: ReadInvoices
      actions: [s3:GetObject, s3:ListBucket]
      resources: [arn:aws:s3:::payments-invoices, arn:aws:s3:::payments-invoices/*]
```

From that, the operator creates and keeps correct, in the AWS account and EKS cluster it runs in:

| What | Details |
|---|---|
| An **IAM role** | Holds exactly the permissions requested, behind a permissions boundary. Its trust policy allows only EKS Pod Identity, only for this cluster, namespace and ServiceAccount. |
| A **Pod Identity association** | `payments/payments-api` → the role. |
| **Tags** | The platform team's global tags plus the request's own tags, on the role and the association. |

Pods using the `payments-api` ServiceAccount then get the role's credentials through the normal AWS SDK credential chain.
**No application code changes.** Resources can belong to other AWS accounts too: put the account's ARNs in `resources`, and let that account's resource policy
(for example an S3 bucket policy) allow the role.

## Why use it

- Teams never need IAM permissions.
- Platform admins set the guardrails once, as `PodIdentityPolicy` objects that only they can write. Every request is checked **before** anything is written to AWS.
- The operator only edits roles it created. It will not take over or delete anything else.
- Drift is repaired: edited trust policies, removed policies, deleted roles and deleted associations come back.
- Every AWS change is recorded as a Kubernetes event, logs follow one resource from request to cleanup, and tags are managed for you.
- Bind the role with **EKS Pod Identity** or **IRSA**, per resource, and switch between them safely ([IRSA](/guide/irsa)).

## Where next

- [Getting started](/guide/getting-started): try it locally with no AWS account.
- [How it works](/guide/how-it-works): the exact sequence and trust design.
- [AWS setup](/guide/aws-setup): the IAM role the operator needs in production.
