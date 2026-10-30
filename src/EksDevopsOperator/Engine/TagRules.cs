using System.Text.RegularExpressions;

namespace EksDevopsOperator.Core;

/// Tags the operator puts on every AWS resource it creates: ownership tags, global tags (admin), and per-request tags.
public static partial class TagRules
{
    /// Lists the custom (global + request) tag keys the operator applied, so keys that are later removed from the
    /// configuration or the request can be removed from AWS without ever touching tags added by someone else.
    public const string ManagedKeysTag = "pio:managed-tag-keys";
    public const int MaxCustomTags = 10;
    const int MaxManagedKeysLength = 250; // IAM tag values are limited to 256 characters

    // custom keys use a stricter set than AWS allows (no spaces and no '+'), so '+' can separate them in the bookkeeping tag value: IAM values may not contain ','
    [GeneratedRegex(@"^[A-Za-z0-9_.:/=@\-]{1,128}$")] private static partial Regex KeyRegex();
    [GeneratedRegex(@"^[\p{L}\p{Z}\p{N}_.:/=+\-@]{0,256}$")] private static partial Regex ValueRegex();

    public static bool IsReserved(string key) => key.StartsWith("pio:", StringComparison.OrdinalIgnoreCase) || key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase);

    /// Validation problems as (code, message). requestTags are checked against the admin's global tags.
    public static List<(string Code, string Message)> Validate(IReadOnlyDictionary<string, string> tags, IReadOnlyDictionary<string, string> global, bool isGlobal)
    {
        var errors = new List<(string, string)>();
        var label = isGlobal ? "global tag" : "tag";
        if (tags.Count > MaxCustomTags) errors.Add(("TooManyTags", $"at most {MaxCustomTags} {label}s are allowed"));
        foreach (var (k, v) in tags)
        {
            if (IsReserved(k)) errors.Add(("ReservedTag", $"{label} '{k}' uses a reserved prefix (pio: or aws:)"));
            else if (!KeyRegex().IsMatch(k)) errors.Add(("InvalidTag", $"{label} key '{k}' is not allowed (use letters, digits and _ . : / = @ -)"));
            else if (!ValueRegex().IsMatch(v ?? "")) errors.Add(("InvalidTag", $"{label} '{k}' has an invalid value"));
            if (!isGlobal && global.ContainsKey(k)) errors.Add(("TagConflict", $"tag '{k}' is set globally by the platform team and cannot be overridden"));
        }
        var keys = string.Join("+", tags.Keys.Concat(isGlobal ? [] : global.Keys));
        if (keys.Length > MaxManagedKeysLength) errors.Add(("TagKeysTooLong", "the tag keys are too long in total; use fewer or shorter keys"));
        return errors;
    }

    public static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> global, IReadOnlyDictionary<string, string> request)
    {
        var all = new Dictionary<string, string>(global);
        foreach (var (k, v) in request) all[k] = v;
        return all;
    }

    /// Adds the bookkeeping tag listing which keys are custom.
    public static Dictionary<string, string> WithManagedKeys(Dictionary<string, string> tags, IEnumerable<string> customKeys)
    {
        var keys = customKeys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (keys.Count > 0) tags[ManagedKeysTag] = string.Join("+", keys);
        return tags;
    }

    /// Tag keys the operator applied earlier that are no longer wanted. Tags the operator never listed are never removed.
    public static List<string> Stale(IReadOnlyDictionary<string, string> observed, IReadOnlyDictionary<string, string> desired)
    {
        var stale = new List<string>();
        if (observed.TryGetValue(ManagedKeysTag, out var listed))
            stale.AddRange(listed.Split('+', StringSplitOptions.RemoveEmptyEntries).Where(k => !desired.ContainsKey(k) && observed.ContainsKey(k)));
        if (observed.ContainsKey(ManagedKeysTag) && !desired.ContainsKey(ManagedKeysTag)) stale.Add(ManagedKeysTag);
        return stale;
    }

    /// True when the observed tags differ from the desired ones (missing, changed, or stale).
    public static bool Differs(IReadOnlyDictionary<string, string> observed, IReadOnlyDictionary<string, string> desired) =>
        desired.Any(kv => !observed.TryGetValue(kv.Key, out var v) || v != kv.Value) || Stale(observed, desired).Count > 0;
}
