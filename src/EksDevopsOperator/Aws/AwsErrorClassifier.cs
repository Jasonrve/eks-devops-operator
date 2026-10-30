using Amazon.Runtime;
using EksDevopsOperator.Core;

namespace EksDevopsOperator.Aws;

public sealed class AwsErrorClassifier : IErrorClassifier
{
    static readonly string[] Throttle = ["Throttl", "RequestLimitExceeded", "TooManyRequests", "SlowDown", "ProvisionedThroughput"];
    static readonly string[] TransientCodes = ["ServiceUnavailable", "ServiceFailure", "InternalFailure", "InternalError", "RequestTimeout", "ConcurrentModification", "NoSuchEntity", "ResourceNotFoundException"];
    static readonly string[] AuthCodes = ["AccessDenied", "UnauthorizedOperation", "AuthFailure", "InvalidClientTokenId", "ExpiredToken", "UnrecognizedClient", "SignatureDoesNotMatch", "InvalidIdentityToken"];
    static readonly string[] ConflictCodes = ["EntityAlreadyExists", "ResourceInUse", "ResourceConflict", "DeleteConflict"];

    public ErrorKind Classify(Exception e) => e switch
    {
        PodIdentityException p => p.Kind,
        AmazonServiceException a => ClassifyCode(a),
        OperationCanceledException or TimeoutException or HttpRequestException or IOException => ErrorKind.Transient,
        AmazonClientException => ErrorKind.Transient, // network / credential retrieval problems
        _ => ErrorKind.Transient,
    };

    static ErrorKind ClassifyCode(AmazonServiceException a)
    {
        var code = a.ErrorCode ?? "";
        if (Throttle.Any(code.Contains) || (int)a.StatusCode == 429 || (int)a.StatusCode >= 500) return ErrorKind.Transient;
        if (AuthCodes.Any(code.Contains)) return ErrorKind.Authorization;
        if (ConflictCodes.Any(code.Contains)) return ErrorKind.Conflict;
        if (TransientCodes.Any(code.Contains)) return ErrorKind.Transient; // e.g. NoSuchEntity right after create = eventual consistency
        return ErrorKind.Permanent; // MalformedPolicyDocument, LimitExceeded, InvalidInput, ...
    }

    /// Registers the SDK error code as the (bounded) metric label.
    public static void UseCodeLabels() => Telemetry.ErrorLabel = e => (e as AmazonServiceException)?.ErrorCode ?? e.GetType().Name;
}
