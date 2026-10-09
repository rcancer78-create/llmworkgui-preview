using System.Net;
using System.Text.RegularExpressions;

namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public sealed class MirasimClientException : Exception
{
    private static readonly Regex BearerCredentialPattern = new(
        @"(?<prefix>\bBearer\s+)\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public MirasimClientException(string message)
        : this(message, statusCode: null, errorCode: null, innerException: null)
    {
    }

    public MirasimClientException(string message, Exception innerException)
        : this(message, statusCode: null, errorCode: null, innerException: innerException)
    {
    }

    public MirasimClientException(string message, HttpStatusCode statusCode, string? errorCode = null)
        : this(message, statusCode, errorCode, innerException: null)
    {
    }

    private MirasimClientException(
        string message,
        HttpStatusCode? statusCode,
        string? errorCode,
        Exception? innerException)
        : base(Redact(message), innerException is null ? null : new Exception($"Underlying failure: {innerException.GetType().Name}."))
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ErrorCode { get; }

    internal static string Redact(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return BearerCredentialPattern.Replace(message, "${prefix}<redacted>");
    }
}
