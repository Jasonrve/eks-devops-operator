using System.Text.RegularExpressions;

namespace EksDevopsOperator.Core;

public sealed record Arn(string Partition, string Service, string Region, string AccountId, string Resource)
{
    static readonly Regex Account = new(@"^\d{12}$", RegexOptions.Compiled);

    public static bool TryParse(string? value, out Arn arn)
    {
        arn = null!;
        if (string.IsNullOrEmpty(value)) return false;
        var p = value.Split(':', 6);
        if (p.Length != 6 || p[0] != "arn" || p[1].Length == 0 || p[2].Length == 0) return false;
        arn = new Arn(p[1], p[2], p[3], p[4], p[5]);
        return true;
    }

    public static bool IsAccountId(string? s) => s is not null && Account.IsMatch(s);
    public override string ToString() => $"arn:{Partition}:{Service}:{Region}:{AccountId}:{Resource}";

    public static string Role(string partition, string account, string path, string name) => $"arn:{partition}:iam::{account}:role{NormalizePath(path)}{name}";
    public static string Policy(string partition, string account, string pathAndName) => $"arn:{partition}:iam::{account}:policy{(pathAndName.StartsWith('/') ? "" : "/")}{pathAndName}";

    public static string NormalizePath(string path)
    {
        if (path.Length == 0) return "/";
        if (!path.StartsWith('/')) path = "/" + path;
        return path.EndsWith('/') ? path : path + "/";
    }

    /// Glob match where '*' matches any run of characters (including none). Case-sensitive, like IAM resource ARNs.
    public static bool GlobMatch(string pattern, string value) =>
        Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.Singleline);
}
