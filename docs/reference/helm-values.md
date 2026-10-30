# Helm values

The chart is `deploy/helm/eks-devops-operator`. Defaults are in its `values.yaml`.

| Value | Default | Description |
|---|---|---|
| `image.repository` | `ghcr.io/jasonrve/eks-devops-operator` | Image to run |
| `image.tag` | chart `appVersion` | Image tag |
| `image.pullPolicy` | `IfNotPresent` | |
| `replicaCount` | `2` | Replicas. Leader election keeps one active. |
| `cluster.name` | **required** | EKS cluster name |
| `cluster.region` | `us-east-1` | AWS region of the cluster |
| `cluster.partition` | `aws` | `aws`, `aws-cn` or `aws-us-gov` |
| `environment` | `dev` | Recorded in the `pio:environment` tag |
| `serviceAccount.name` | `eks-devops-operator` | ServiceAccount the operator's Pod Identity association must target |
| `irsa.enabled` | `false` | Allow `IrsaAccess`. Adds one RBAC rule (get and patch on serviceaccounts). See [IRSA](/guide/irsa). |
| `serviceAccountAnnotations.enabled` | `false` | Create access resources from annotated ServiceAccounts. Adds RBAC: list and patch serviceaccounts, create and delete the two access kinds. See [Annotations](/guide/annotations). |
| `serviceAccountAnnotations.interval` | `00:00:30` | How often ServiceAccounts are checked (hh:mm:ss). |
| `irsa.oidcIssuer` | empty | OIDC issuer of the cluster. Empty = discovered with `eks:DescribeCluster`. |
| `policy.allowedMechanisms` | `["*"]` | Mechanisms the default policy allows: `PodIdentity`, `IRSA`. |
| `tags` | `{}` | Tags added to every role and association. Max 10; keys use letters, digits and `_ . : / = @ -`. Changing them updates existing resources. |
| `policy.create` | `true` | Render the `default` PodIdentityPolicy in the release namespace. The operator reads policies only from that namespace. |
| `policy.namespaces` | `[]` (all) | Namespaces the default policy governs |
| `policy.allowedServices` | `["*"]` | IAM service prefixes allowed. `"*"` = every service, `[]` = none |
| `policy.allowedActions` | `["*"]` | Action globs teams may use. `"*"` = every action, `[]` = none |
| `policy.deniedActions` | `[]` | Action globs always refused |
| `policy.allowedResourceArnPatterns` | `[]` | ARN globs every resource must match |
| `policy.allowedManagedPolicyArns` | `[]` | Managed policy ARN globs |
| `policy.allowedDeletionPolicies` | `[Delete, Retain]` | |
| `policy.requirePermissionsBoundary` | `true` | |
| `policy.allowWildcardActions` | `false` | |
| `policy.allowWildcardResources` | `false` | |
| `policy.allowPrivilegeEscalationActions` | `false` | |
| `permissionsBoundary` | `/eks-devops-operator/standard-workload` | Boundary policy path and name |
| `driftInterval` | `00:10:00` | Re-check interval for Ready resources |
| `maxConcurrentReconciles` | `4` | |
| `logLevel` | `Information` | Level of the operator's own logs (`Debug`, `Information`, `Warning`) |
| `libraryLogLevel` | `Warning` | Level of KubeOps. `Information` adds a line per reconcile and per watch event |
| `extraEnv` | `[]` | Extra container env (e.g. `Aws__ServiceUrl` for an emulator) |
| `resources` | requests 50m / 128Mi, limits 1 CPU / 256Mi | Measured suggestion, see [Image and sizing](/guide/sizing) |
| `podDisruptionBudget.enabled` / `.minAvailable` | `true` / `1` | Created only when `replicaCount > 1` |
| `serviceMonitor.enabled` / `.interval` / `.labels` | `false` / `30s` / `{}` | Prometheus Operator |
| `nodeSelector`, `tolerations`, `affinity`, `podAnnotations` | empty | Scheduling and pod metadata |
