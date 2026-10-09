using System.Runtime.CompilerServices;
using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class ToolCallStopSequenceReviewTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StopFiltersVisibleTextWithoutCuttingToolArguments(bool finalOnly, bool visibleTextStop)
    {
        var directory = Directory.CreateTempSubdirectory("llmgw-owned-tool-stop-");
        try
        {
            var prefix = visibleTextStop ? "beforeENDafter" : "before";
            var answer = prefix + "<tool_calls>[{\"name\":\"known\",\"arguments\":{\"note\":\"END\"}}]</tool_calls>";
            using var gateway = new LlmGateway(JsonAccountStore.InMemory([
                new AccountProfile { Id="owned", Provider=ProviderKind.Codex, IsActive=true }]),
                [new Adapter(finalOnly,answer)],new GatewayOptions { WorkspaceDirectory=directory.FullName });
            var request = new ChatRequest { Model="codex/owned/model",Messages=[ChatMessage.User("Owned tool stop fixture")],
                Stop=[visibleTextStop ? "END" : "}"], Tools=[new ToolDefinition("known",null,"{}")] };
            var result = await gateway.CompleteAsync(request);
            var call = Assert.Single(result.ToolCalls);
            Assert.Equal("known",call.Name);
            Assert.Equal("{\"note\":\"END\"}",call.ArgumentsJson);
            Assert.Equal("before",result.Content);
            Assert.Equal("tool_calls",result.FinishReason);
        }
        finally { directory.Delete(true); }
    }
    private sealed class Adapter(bool finalOnly,string answer) : IProviderAdapter
    {
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned scripted";
        public string DefaultExecutable => "owned-scripted";
        public ProviderCapabilities Capabilities { get; } = new(true,"owned",MultiAccountSupport.Isolated,"owned",null,null,true,100_000);
        public string? ResolveExecutable(AccountProfile profile) => "owned-scripted";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public Task<AccountStatus> GetStatusAsync(AccountProfile profile,CancellationToken token) => Task.FromResult(new AccountStatus(AccountAvailability.Ready,null,null,null,DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile profile,CancellationToken token) => Task.FromResult<IReadOnlyList<NativeModel>>([new("model","Owned",true)]);
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile profile,CancellationToken token) => Task.FromResult(new QuotaSnapshot(profile.Id,profile.Provider,DateTimeOffset.UtcNow,AccountAvailability.Ready,true,null,[],"owned",null));
        public Task StartInteractiveLoginAsync(AccountProfile profile,CancellationToken token) => Task.CompletedTask;
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile profile,NativeChatRequest request,[EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield();
            yield return finalOnly ? NativeChatEvent.Final(answer) : NativeChatEvent.Delta(answer);
        }
    }
}
