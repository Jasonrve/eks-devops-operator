using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EksDevopsOperator.Core;

public static class Naming
{
    public const int MaxRoleName = 64;
    static readonly Regex Requested = new(@"^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.Compiled);
    static readonly Regex Unsafe = new(@"[^a-z0-9-]+", RegexOptions.Compiled);

    public static bool IsValidRequestedName(string name) => Requested.IsMatch(name);

    public static string Hash(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))))[..8].ToLowerInvariant();

    /// Deterministic, collision-resistant role name: "{readable}-{hash8}" (<= 64 chars). The hash covers the cluster account, cluster,
    /// namespace and ServiceAccount, so truncating the readable part cannot cause collisions and a requested name can never
    /// address another team's role.
    public static string RoleName(OperatorOptions o, ClusterContext c, AccessRequest r)
    {
        var readable = r.RequestedRoleName ?? $"{o.RolePrefix}-{Sanitize(c.ClusterName)}-{Sanitize(r.Namespace)}-{Sanitize(r.ServiceAccount)}";
        var hash = Hash(c.AccountId, c.ClusterName, r.Namespace, r.ServiceAccount);
        var max = MaxRoleName - 1 - hash.Length;
        return readable[..Math.Min(readable.Length, max)].TrimEnd('-') + "-" + hash;
    }

    public static string Sanitize(string s) => Unsafe.Replace(s.ToLowerInvariant(), "-").Trim('-');
}
