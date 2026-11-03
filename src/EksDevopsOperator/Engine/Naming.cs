using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EksDevopsOperator.Core;

public static class Naming
{
    public const int MaxRoleName = 64;
    static readonly Regex RoleNameFormat = new(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    static readonly Regex Unsafe = new(@"[^a-z0-9-]+", RegexOptions.Compiled);

    public static bool IsValidRequestedName(string name) => RoleNameFormat.IsMatch(name);

    public static string Token(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)))).ToLowerInvariant();

    /// The requested name exactly, or "{cluster}-{namespace}-{serviceaccount}" when none is requested (truncated to 64).
    /// The engine refuses a name that is already taken, so the default needs no hash.
    public static string RoleName(ClusterContext c, AccessRequest r)
    {
        if (r.RequestedRoleName is not null) return r.RequestedRoleName;
        var name = $"{Sanitize(c.ClusterName)}-{Sanitize(r.Namespace)}-{Sanitize(r.ServiceAccount)}";
        return name[..Math.Min(name.Length, MaxRoleName)].TrimEnd('-');
    }

    public static string Sanitize(string s) => Unsafe.Replace(s.ToLowerInvariant(), "-").Trim('-');
}
