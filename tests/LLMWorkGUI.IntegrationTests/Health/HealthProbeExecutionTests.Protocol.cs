using System.Net;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.MockServers;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed partial class HealthProbeExecutionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("length")]
    [InlineData("content_filter")]
    public async Task UnfinishedOrUnknownProbeTurnDoesNotConfirmRecovery(string? finishReason)
    {
        await using var server = new LocalMockAiServer();
        server.ChatCompletionRawResponseOverride = JsonSerializer.Serialize(new
        {
            model = "model", choices = new[] { new { message = new { role = "assistant", content = "partial" }, finish_reason = finishReason } }
        });
        await server.StartAsync();
        var result = await new ProviderModelProbeExecutor().ExecuteAsync(new ModelProbeRequest
        { Scope = HealthScope.ForModelRoute(AccountId, "model"), ProviderProfileId = ProviderProfileId, BaseUrl = server.BaseUrl, ModelId = "model" });
        Assert.Equal(ModelProbeOutcome.Failed, result.Outcome);
        Assert.Equal(HealthErrorClass.MalformedProtocolEvent, result.FailureClass);
    }

    [Theory]
    [InlineData("{\"role\":\"assistant\",\"content\":[null]}", "stop")]
    [InlineData("{\"role\":\"assistant\",\"tool_calls\":[{}]}", "tool_calls")]
    [InlineData("{\"role\":\"assistant\",\"content\":\"ok\",\"tool_calls\":[{}]}", "stop")]
    [InlineData("{\"role\":\"assistant\",\"content\":\"ok\"}", "tool_calls")]
    [InlineData("{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"1\",\"type\":\"function\",\"function\":{\"name\":\"probe\",\"arguments\":\"broken\"}}]}", "tool_calls")]
    public async Task NonemptyButMalformedPayloadDoesNotConfirmRecovery(string message, string finishReason)
    {
        await using var server = new LocalMockAiServer();
        server.ChatCompletionRawResponseOverride = "{\"model\":\"model\",\"choices\":[{\"message\":" + message + ",\"finish_reason\":\"" + finishReason + "\"}]}";
        await server.StartAsync();
        var result = await new ProviderModelProbeExecutor().ExecuteAsync(new ModelProbeRequest
        { Scope = HealthScope.ForModelRoute(AccountId, "model"), ProviderProfileId = ProviderProfileId, BaseUrl = server.BaseUrl, ModelId = "model" });
        Assert.Equal(ModelProbeOutcome.Failed, result.Outcome);
        Assert.Equal(HealthErrorClass.MalformedProtocolEvent, result.FailureClass);
    }

    [Fact]
    public async Task ProbeRequestDeadlineIncludesResponseBody()
    {
        using var body = new StalledProbeContent();
        using var http = new HttpClient(new ProbeBodyHandler(body)) { Timeout = Timeout.InfiniteTimeSpan };
        using var caller = new CancellationTokenSource();
        var operation = new ProviderModelProbeExecutor(httpClient: http).ExecuteAsync(new ModelProbeRequest
        { Scope = HealthScope.ForModelRoute(AccountId, "model"), ProviderProfileId = ProviderProfileId,
            BaseUrl = "https://synthetic.invalid", ModelId = "model" }, caller.Token);
        try
        {
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(ModelProbeOutcome.Failed, result.Outcome);
            Assert.Equal(HealthErrorClass.NetworkOrTimeout, result.FailureClass);
            Assert.False(caller.IsCancellationRequested);
            Assert.True(body.WasDisposed);
        }
        finally
        {
            caller.Cancel();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(65536, true, true)]
    [InlineData(65537, true, false)]
    [InlineData(65536, false, true)]
    [InlineData(65537, false, false)]
    public async Task ProbeResponseSizeIsBoundedWithOrWithoutContentLength(int size, bool knownLength, bool succeeds)
    {
        const string json = "{\"model\":\"model\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]},\"finish_reason\":\"stop\"}]}";
        using var body = new SizedProbeContent(Encoding.UTF8.GetBytes(json.PadRight(size)), knownLength);
        using var http = new HttpClient(new ProbeBodyHandler(body));
        var result = await new ProviderModelProbeExecutor(httpClient: http).ExecuteAsync(new ModelProbeRequest
        { Scope = HealthScope.ForModelRoute(AccountId, "model"), ProviderProfileId = ProviderProfileId,
            BaseUrl = "https://synthetic.invalid", ModelId = "model" });
        Assert.Equal(succeeds ? ModelProbeOutcome.Succeeded : ModelProbeOutcome.Failed, result.Outcome);
        if (!succeeds) Assert.Equal(HealthErrorClass.MalformedProtocolEvent, result.FailureClass);
        Assert.True(body.WasDisposed);
    }

    [Fact]
    public async Task CallerCancellationDuringBodyReadRemainsCancellationAndDisposesResponse()
    {
        using var body = new StalledProbeContent();
        using var http = new HttpClient(new ProbeBodyHandler(body)) { Timeout = Timeout.InfiniteTimeSpan };
        using var caller = new CancellationTokenSource();
        var operation = new ProviderModelProbeExecutor(httpClient: http).ExecuteAsync(new ModelProbeRequest
        { Scope = HealthScope.ForModelRoute(AccountId, "model"), ProviderProfileId = ProviderProfileId,
            BaseUrl = "https://synthetic.invalid", ModelId = "model" }, caller.Token);
        await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(body.WasDisposed);
    }

    private sealed class SizedProbeContent(byte[] bytes, bool knownLength) : HttpContent
    {
        public bool WasDisposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return knownLength; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(bytes).AsTask();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class ProbeBodyHandler(HttpContent body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
    }

    private sealed class StalledProbeContent : HttpContent
    {
        public bool WasDisposed { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
