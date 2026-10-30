using System.Text.Json.Nodes;

namespace EksDevopsOperator.Core;

/// Builds the IAM documents the operator owns. Teams never supply trust policy JSON.
public static class PolicyDocuments
{
    public const string Version = "2012-10-17";
    public const string PodsPrincipal = "pods.eks.amazonaws.com";

    public static string Permissions(IEnumerable<PolicyStatement> statements)
    {
        var arr = new JsonArray();
        foreach (var s in statements)
        {
            var o = new JsonObject();
            if (!string.IsNullOrEmpty(s.Sid)) o["Sid"] = s.Sid;
            o["Effect"] = s.Effect;
            o["Action"] = new JsonArray(s.Actions.Select(a => (JsonNode)a).ToArray());
            o["Resource"] = new JsonArray(s.Resources.Select(a => (JsonNode)a).ToArray());
            arr.Add(o);
        }
        return new JsonObject { ["Version"] = Version, ["Statement"] = arr }.ToJsonString();
    }

    /// Only the EKS Pod Identity service may assume the role, only for this cluster and account, and (when enforced) only for this
    /// namespace and ServiceAccount. Session tags stay enabled.
    /// IRSA: only web identities issued by this cluster's OIDC provider, for exactly this ServiceAccount, with the STS audience.
    public static string IrsaTrust(OidcInfo oidc, string ns, string sa)
    {
        var statement = new JsonObject
        {
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Federated"] = oidc.ProviderArn },
            ["Action"] = "sts:AssumeRoleWithWebIdentity",
            ["Condition"] = new JsonObject
            {
                ["StringEquals"] = new JsonObject
                {
                    [$"{oidc.Issuer}:sub"] = $"system:serviceaccount:{ns}:{sa}",
                    [$"{oidc.Issuer}:aud"] = "sts.amazonaws.com",
                },
            },
        };
        return new JsonObject { ["Version"] = Version, ["Statement"] = new JsonArray(statement) }.ToJsonString();
    }

    public static string Trust(ClusterContext c, string ns, string sa, bool enforceTagConditions)
    {
        var equals = new JsonObject { ["aws:SourceAccount"] = c.AccountId };
        if (enforceTagConditions)
        {
            equals["aws:RequestTag/kubernetes-namespace"] = ns;
            equals["aws:RequestTag/kubernetes-service-account"] = sa;
            equals["aws:RequestTag/eks-cluster-name"] = c.ClusterName;
        }
        var statement = new JsonObject
        {
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = PodsPrincipal },
            ["Action"] = new JsonArray("sts:AssumeRole", "sts:TagSession"),
            ["Condition"] = new JsonObject { ["StringEquals"] = equals, ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = c.ClusterArn } },
        };
        return new JsonObject { ["Version"] = Version, ["Statement"] = new JsonArray(statement) }.ToJsonString();
    }
}
