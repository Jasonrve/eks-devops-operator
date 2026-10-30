namespace EksDevopsOperator.Core;

public enum ErrorKind { Transient, Permanent, Authorization, Conflict }

public sealed class PodIdentityException(ErrorKind kind, string reason, string message, Exception? inner = null) : Exception(message, inner)
{
    public ErrorKind Kind { get; } = kind;
    public string Reason { get; } = reason;
}

public static class Backoff
{
    /// Exponential backoff with jitter. Permanent/authorization errors use a much longer base so they never hot-loop.
    public static TimeSpan Delay(ErrorKind kind, int attempt, Func<double>? random = null)
    {
        var (baseSec, capSec) = kind switch
        {
            ErrorKind.Transient => (2.0, 120.0),
            ErrorKind.Authorization => (60.0, 900.0),
            _ => (300.0, 1800.0),
        };
        var ceiling = Math.Min(capSec, baseSec * Math.Pow(2, Math.Min(Math.Max(attempt, 0), 16)));
        return TimeSpan.FromSeconds(ceiling / 2 + (random ?? Random.Shared.NextDouble)() * ceiling / 2);
    }
}
