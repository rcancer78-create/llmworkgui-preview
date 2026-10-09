using System.Net;
using System.Text;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
    }

    public List<(string Method, string Path)> Requests { get; } = new();

    public static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add((request.Method.Method, request.RequestUri?.AbsolutePath ?? string.Empty));

        return _handler(request, cancellationToken);
    }
}
