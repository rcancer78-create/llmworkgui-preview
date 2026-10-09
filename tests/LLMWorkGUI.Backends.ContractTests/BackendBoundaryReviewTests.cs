using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Backends.OpenCode.Events;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class BackendBoundaryReviewTests
{
    [Fact]
    public void EndpointFormattingDoesNotExposeItsApiKey()
    {
        var endpoint = StarCliProxyEndpoint.Loopback(8300, "synthetic-private-value");
        Assert.DoesNotContain("synthetic-private-value", endpoint.ToString());
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("http://user:synthetic@127.0.0.1/")]
    public void EndpointRejectsUnsupportedSchemeAndEmbeddedCredentials(string url)
        => Assert.Throws<ArgumentException>(() => new StarCliProxyEndpoint(new Uri(url)));

    [Fact]
    public void MirasimExceptionDoesNotRetainUnredactedInnerDiagnostics()
    {
        var original = new InvalidOperationException("Bearer synthetic-private-value");
        var error = new MirasimClientException("Request failed", original);
        Assert.DoesNotContain("synthetic-private-value", error.ToString());
        Assert.DoesNotContain("synthetic-private-value", error.InnerException?.ToString() ?? "");
        Assert.Contains("synthetic-private-value", original.Message);
    }

    [Theory]
    [InlineData("../outside.yml")]
    [InlineData("..\\outside.yml")]
    [InlineData("folder/config.yml")]
    [InlineData("config.yml:alternate")]
    public void ProxyConfigNameCannotEscapeOrUseAlternateStreams(string name)
        => Assert.Throws<ArgumentException>(() => new StarCliProxyOptions { ConfigFileName = name }.Validate());

    [Theory]
    [InlineData("../outside.jsonl")]
    [InlineData("..\\outside.jsonl")]
    [InlineData("folder/event.jsonl")]
    [InlineData("event.jsonl:alternate")]
    public async Task EventSpoolNameCannotEscapeItsDirectory(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "spool-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                await using var spool = new OpenCodeEventSpool(Path.Combine(root, "spool"), 4, name);
            });
            Assert.IsType<ArgumentException>(error);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("session.idle", "{}")]
    [InlineData("session.idle", "{\"sessionID\":\" \"}")]
    [InlineData("session.updated", "{\"info\":{}}")]
    [InlineData("message.updated", "{\"info\":{\"id\":\"message\"}}")]
    [InlineData("message.part.updated", "{\"part\":{\"id\":\"part\",\"sessionID\":\"session\",\"messageID\":\"message\"}}")]
    public void MalformedTypedEventsAreNotAccepted(string type, string properties)
    {
        using var document = JsonDocument.Parse(properties);
        var envelope = new OpenCodeEventEnvelope(type, document.RootElement, properties, DateTime.UtcNow);
        var parsed = type switch
        {
            "session.idle" => SessionIdleEvent.TryParse(envelope, out _),
            "session.updated" => SessionUpdatedEvent.TryParse(envelope, out _),
            "message.updated" => MessageUpdatedEvent.TryParse(envelope, out _),
            _ => MessagePartUpdatedEvent.TryParse(envelope, out _)
        };
        Assert.False(parsed);
    }
}
