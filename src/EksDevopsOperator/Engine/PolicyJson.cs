using System.Text.Json.Nodes;

namespace EksDevopsOperator.Core;

/// Canonical form of IAM policy documents, so formatting, key order, URL-encoding, scalar-vs-array and element
/// order never produce spurious diffs (IAM arrays and statements carry no ordering semantics).
public static class PolicyJson
{
    public static string Normalize(string? document)
    {
        if (string.IsNullOrWhiteSpace(document)) return "";
        var text = document.TrimStart().StartsWith('%') ? Uri.UnescapeDataString(document) : document; // IAM returns trust policies URL-encoded
        return Canonical(JsonNode.Parse(text), null)!.ToJsonString();
    }

    public static bool Equivalent(string? a, string? b) => Normalize(a) == Normalize(b);

    // Keys whose value may be a scalar or a single-element array.
    static readonly HashSet<string> SetLike = ["Action", "NotAction", "Resource", "NotResource", "Service", "AWS", "Federated", "CanonicalUser", "Statement"];

    static JsonNode? Canonical(JsonNode? node, string? key, bool inCondition = false)
    {
        switch (node)
        {
            case JsonObject o:
                var res = new JsonObject();
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                    res[kv.Key] = Canonical(kv.Value, kv.Key, inCondition || kv.Key == "Condition" || (inCondition && true));
                return res;
            case JsonArray a:
                return new JsonArray(a.Select(x => Canonical(x, null, inCondition)).OrderBy(i => i?.ToJsonString(), StringComparer.Ordinal).ToArray());
            case JsonValue v when key is not null && (SetLike.Contains(key) || (inCondition && key != "Condition")):
                return new JsonArray(v.DeepClone());
            default:
                return node?.DeepClone();
        }
    }
}
