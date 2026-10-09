using LLMGateway.Core;
using LLMGateway.Native;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Reviews;
using LLMWorkGUI.Infrastructure.GrokBot;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class GrokBotProviderTests
{
    private static AccountProfile Account() => new() { Id = "grokbot-default", DisplayName = "Grok Bot", Provider = ProviderKind.GrokBot, IsActive = true };
    private static ChatRequest Request() => new() { Model = "grokbot/grokbot-default/grok-bot", Messages = [ChatMessage.User("Проверь: 2+2=5")] };

    [Fact]
    public async Task CompositionCatalogIsLimitedWithoutClaimingIdentityOrOpeningSession()
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Account()]), [adapter], new());
        var provider = Assert.Single(await gateway.GetProvidersAsync());
        Assert.True(provider.Installed); Assert.True(provider.Capabilities.IsLimited);
        Assert.False(provider.Capabilities.SupportsReasoningEffort);
        Assert.False(provider.Capabilities.SupportsAutomaticRouting);
        Assert.False(provider.Capabilities.SupportsToolCalling);
        var models = await gateway.GetModelsAsync();
        Assert.Equal("grok-bot", Assert.Single(models).NativeModel);
        var snapshot = new GatewayCatalogMapper(new SensitiveDataFilter()).Map([provider], await gateway.GetAccountsAsync(), models, [], DateTimeOffset.UtcNow);
        Assert.Equal(GrokBotRestrictions.ProviderProfileId, Assert.Single(snapshot.Providers).Id);
        Assert.False(snapshot.Providers[0].IsEnabled);
        Assert.Null(snapshot.Providers[0].GatewayNativeId);
        Assert.Equal(0, transport.Calls); Assert.Equal(0, transport.Probes);
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("schema")]
    [InlineData("effort")]
    [InlineData("tool-history")]
    public async Task UnsupportedFeaturesRefuseBeforeStartedOrTransport(string feature)
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Account()]), [adapter], new());
        var request = Request();
        if (feature == "tools") request.Tools = [new("write_file", null, "{}")];
        if (feature == "schema") request.ResponseFormat = new(ResponseFormatKind.JsonSchema, "result", "{}");
        if (feature == "effort") request.ReasoningEffort = "high";
        if (feature == "tool-history") request.Messages.Add(new(ChatRole.Tool, "result") { ToolCallId = "call" });
        await using var stream = gateway.StreamAsync(request).GetAsyncEnumerator();
        await Assert.ThrowsAsync<GatewayException>(async () => await stream.MoveNextAsync());
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task AutomaticFallbackCannotSelectGrokBotButExplicitReviewSucceeds()
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Account()]), [adapter], new());
        var automatic = Request(); automatic.Model = "auto";
        await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(automatic));
        Assert.Equal(0, transport.Calls);
        var response = await gateway.CompleteAsync(Request());
        Assert.Contains("замечание", response.Content); Assert.True(response.Usage.Estimated);
        Assert.Equal("grok-bot", response.NativeModel); Assert.Equal(1, transport.Calls);
        Assert.Contains("Не изменяй файлы", transport.Prompt);
    }

    [Fact]
    public async Task AliasedSecondAccountAndOverridesAreRefusedBeforePersistOrSend()
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Account()]), [adapter], new());
        var extra = Account(); extra.Id = "second";
        await Assert.ThrowsAsync<GatewayException>(() => gateway.AddAccountAsync(extra));
        extra = Account(); extra.ConfigDirectory = "C:/another";
        await Assert.ThrowsAsync<GatewayException>(() => gateway.UpdateAccountAsync(extra));
        Assert.Single(await gateway.GetAccountsAsync()); Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task ConcurrentGatewayRequestRefusesBeforeStartedAndCancellationReleasesPreparation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport { Action = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return ("unreachable", false); } };
        var adapter = new GrokBotProviderAdapter(transport, new());
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Account()]), [adapter], new());
        using var cancel = new CancellationTokenSource();
        await using var first = gateway.StreamAsync(Request(), cancel.Token).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync()); Assert.Equal(ChatUpdateKind.Started, first.Current.Kind);
        var pending = first.MoveNextAsync().AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var second = gateway.StreamAsync(Request()).GetAsyncEnumerator();
        await Assert.ThrowsAsync<GatewayException>(async () => await second.MoveNextAsync());
        Assert.Equal(1, transport.Calls); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        transport.Action = _ => Task.FromResult(("review", true));
        var third = await gateway.CompleteAsync(Request()); Assert.Contains("очистка", third.Content);
        Assert.Equal(2, transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidModelOrOversizedUnicodeRefusesBeforeStarted(bool oversized)
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Account()]), [adapter], new());
        var request = Request();
        if (oversized) request.Messages = [ChatMessage.User(new string('\u4e2d', 70_000))];
        else request.Model = "grokbot/grokbot-default/unavailable-model";
        await using var stream = gateway.StreamAsync(request).GetAsyncEnumerator();
        await Assert.ThrowsAsync<GatewayException>(async () => await stream.MoveNextAsync());
        Assert.Equal(0, transport.Calls);
        Assert.NotNull(await gateway.CompleteAsync(Request()));
    }

    [Fact]
    public async Task DisposingAtStartedReleasesPreparationWithoutCallingTransport()
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Account()]), [adapter], new());
        await using (var abandoned = gateway.StreamAsync(Request()).GetAsyncEnumerator())
        {
            Assert.True(await abandoned.MoveNextAsync());
            Assert.Equal(ChatUpdateKind.Started, abandoned.Current.Kind); Assert.Equal(0, transport.Calls);
        }
        Assert.NotNull(await gateway.CompleteAsync(Request())); Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task FileRoundtripDoesNotOverwriteTaskOrExistingAnswerAndRejectsInvalidUtf8()
    {
        var root = Directory.CreateTempSubdirectory("grokbot-file-test-");
        try
        {
            var input = Path.Combine(root.FullName, "task.md"); var output = Path.Combine(root.FullName, "answer.md");
            await File.WriteAllTextAsync(input, "Проверь код в этом документе.");
            var preview = await ReviewTaskFiles.ReadAsync(input); Assert.Equal(64, preview.Sha256.Length);
            // The source hash identifies the original file, including a BOM; the UI separately
            // records the hash of the actual composer text sent in the request.
            await File.WriteAllTextAsync(input, preview.Text, new System.Text.UTF8Encoding(true));
            var withBom = await ReviewTaskFiles.ReadAsync(input);
            Assert.Equal(preview.Text, withBom.Text); Assert.NotEqual(preview.Sha256, withBom.Sha256);
            await ReviewTaskFiles.SaveAsync(output, "Ответ");
            await Assert.ThrowsAsync<IOException>(() => ReviewTaskFiles.SaveAsync(output, "overwrite"));
            await Assert.ThrowsAsync<IOException>(() => ReviewTaskFiles.SaveAsync(input, "overwrite"));
            Assert.Equal("Ответ", await File.ReadAllTextAsync(output));
            Assert.Equal(preview.Text, await File.ReadAllTextAsync(input));
            await File.WriteAllBytesAsync(input, [0xff, 0xfe, 0xc0]);
            await Assert.ThrowsAsync<System.Text.DecoderFallbackException>(() => ReviewTaskFiles.ReadAsync(input));
            await Assert.ThrowsAsync<IOException>(() => ReviewTaskFiles.ReadAsync(input + ":stream"));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullWireLimitAndInvalidUnicodeRefuseBeforeStarted(bool invalidUnicode)
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Account()]), [adapter], new());
        var request = Request();
        request.Messages = [ChatMessage.User(invalidUnicode ? "Review \uD800" : new string('x', 64_000))];
        await using var stream = gateway.StreamAsync(request).GetAsyncEnumerator();
        await Assert.ThrowsAsync<GatewayException>(async () => await stream.MoveNextAsync());
        Assert.Equal(0, transport.Calls);
        Assert.NotNull(await gateway.CompleteAsync(Request())); // Refusal did not retain the provider slot.
    }

    [Fact]
    public async Task PreparationCannotBeReusedWithChangedAccountOverrides()
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        var account = Account();
        var request = new NativeChatRequest { Prompt = "Review this text.", Model = "grok-bot",
            WorkingDirectory = Path.GetTempPath(), Timeout = TimeSpan.FromSeconds(10) };
        await using var preparation = await adapter.PrepareRequestAsync(account, request, CancellationToken.None);
        account.ConfigDirectory = "D:/synthetic-disallowed-config";
        await using var stream = adapter.RunChatAsync(account, request, CancellationToken.None).GetAsyncEnumerator();
        await Assert.ThrowsAsync<GatewayException>(async () => await stream.MoveNextAsync());
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactFullWireBoundaryIsAcceptedWithoutTruncation(bool unicode)
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Account()]), [adapter], new());
        var task = new string(unicode ? '\u4e2d' : 'x', 64_000 - GrokBotRestrictions.ReviewInstructions.Length);
        var request = Request(); request.Messages = [ChatMessage.User(task)];
        Assert.NotNull(await gateway.CompleteAsync(request));
        Assert.Equal(GrokBotRestrictions.ReviewInstructions + task, transport.Prompt);
        Assert.Equal(64_000, transport.Prompt!.Length);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(transport.Prompt) <= 200_000);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task TranscriptFormattingIsCountedBeforeAddingProviderInstructions()
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Account()]), [adapter], new());
        var task = "-" + new string('x', 64_000 - GrokBotRestrictions.ReviewInstructions.Length - 1);
        var request = Request(); request.Messages = [ChatMessage.User(task)];
        await using var stream = gateway.StreamAsync(request).GetAsyncEnumerator();
        await Assert.ThrowsAsync<GatewayException>(async () => await stream.MoveNextAsync());
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task PreparedRequestCannotStartAnotherTransportAttempt()
    {
        var transport = new Transport(); var adapter = new GrokBotProviderAdapter(transport, new());
        var account = Account();
        var request = new NativeChatRequest { Prompt = "Review this text.", Model = "grok-bot",
            WorkingDirectory = Path.GetTempPath(), Timeout = TimeSpan.FromSeconds(10) };
        await using var preparation = await adapter.PrepareRequestAsync(account, request, CancellationToken.None);
        await using (var first = adapter.RunChatAsync(account, request, CancellationToken.None).GetAsyncEnumerator())
            Assert.True(await first.MoveNextAsync());
        await using var second = adapter.RunChatAsync(account, request, CancellationToken.None).GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
        Assert.Equal(1, transport.Calls);
    }

    private sealed class Transport : IGrokBotReviewTransport
    {
        public int Calls, Probes; public string? Prompt;
        public Func<CancellationToken, Task<(string, bool)>> Action = _ => Task.FromResult(("Одно замечание", false));
        public Task<bool> CheckSessionAsync(string executable, CancellationToken token) { Probes++; Assert.True(Path.IsPathFullyQualified(executable)); return Task.FromResult(true); }
        public Task<(string Text, bool CleanupPending)> ReviewAsync(string executable, string prompt, CancellationToken token)
        { Assert.True(Path.IsPathFullyQualified(executable)); Calls++; Prompt = prompt; return Action(token); }
    }
}
