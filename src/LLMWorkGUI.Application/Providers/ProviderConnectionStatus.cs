namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Classified status resulting from a provider connection health check.
/// </summary>
public enum ProviderConnectionStatus
{
    /// <summary>
    /// Successfully reached endpoint and received a valid models list.
    /// </summary>
    Success,

    /// <summary>
    /// URL format is invalid or cannot be parsed.
    /// </summary>
    InvalidUrlFormat,

    /// <summary>
    /// Base URL uses insecure HTTP for an external non-loopback host.
    /// </summary>
    InsecureRemoteHttp,

    /// <summary>
    /// TLS/SSL handshake failed (untrusted cert, expired cert, or hostname mismatch).
    /// </summary>
    SslHandshakeError,

    /// <summary>
    /// Provider rejected credentials (HTTP 401 or 403).
    /// </summary>
    AuthenticationFailed,

    /// <summary>
    /// The models endpoint returned HTTP 404 Not Found.
    /// </summary>
    EndpointNotFound,

    /// <summary>
    /// Provider returned a 5xx server error.
    /// </summary>
    RemoteServerError,

    /// <summary>
    /// Remote host refused connection or is not listening on the specified port.
    /// </summary>
    ConnectionRefused,

    /// <summary>
    /// Connection or response timed out.
    /// </summary>
    TimedOut,

    /// <summary>
    /// A secret reference is configured for this provider but its value could not be resolved, because
    /// the reference is <c>Missing</c> or <c>Revoked</c> or its payload cannot be read. No request was
    /// sent: proceeding without credentials would test the wrong thing and hide the reason
    /// (ADR-0005 §5.2).
    /// </summary>
    SecretUnavailable,

    /// <summary>
    /// An unexpected error occurred.
    /// </summary>
    UnknownError
}
