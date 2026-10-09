using System.Net;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode;

public sealed class OpenCodeClientException : Exception
{
    public OpenCodeClientException(string message)
        : base(message)
    {
    }

    public OpenCodeClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OpenCodeClientException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}
