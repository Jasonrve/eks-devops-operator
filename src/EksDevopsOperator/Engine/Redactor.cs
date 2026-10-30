using System.Text.RegularExpressions;

namespace EksDevopsOperator.Core;

/// Removes anything that looks like an AWS credential or token from text before it reaches logs, status or events.
public static partial class Redactor
{
    public const string Mask = "[REDACTED]";

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = AccessKey().Replace(text, Mask);
        text = KeyValue().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mask}");
        return Jwt().Replace(text, Mask);
    }

    [GeneratedRegex(@"\b(AKIA|ASIA)[A-Z0-9]{16}\b")] private static partial Regex AccessKey();
    [GeneratedRegex(@"(?i)(secret_?access_?key|session_?token|aws_secret_access_key|authorization|x-amz-security-token|password|token)([""'\s:=]+)[A-Za-z0-9/+=_\-.]{8,}")] private static partial Regex KeyValue();
    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]*")] private static partial Regex Jwt();
}
