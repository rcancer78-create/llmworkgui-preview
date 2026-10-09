using System.Net;
using System.Text;
using LLMGateway.Core;
using LLMGateway.Core.Client;
using LLMGateway.Core.OpenAi;

namespace LLMGateway.Tests;

public sealed class R3SdkReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactRenderedPromptBudgetPreservesEveryTurn(bool instructions)
    {
        var request = new ChatRequest { Messages = [ChatMessage.User(new string('a', 200)), ChatMessage.Assistant("previous"), ChatMessage.User("next")] };
        if (instructions) request.Messages.Insert(0, ChatMessage.System("Owned instructions"));
        var rendered = PromptBuilder.Build(request, 10000);
        Assert.Contains(new string('a', 200), rendered);
        Assert.Equal(rendered, PromptBuilder.Build(request, rendered.Length));
    }

    [Theory]
    [InlineData(null, "call-owned", "function")]
    [InlineData("", "call-owned", "function")]
    [InlineData("   ", "call-owned", "function")]
    [InlineData("owned", null, "function")]
    [InlineData("owned", "", "function")]
    [InlineData("owned", "call-owned", "unknown")]
    public void MalformedAssistantToolCallIsRefusedWithoutDroppingOrInventingHistory(string? name, string? id, string type)
    {
        var dto = new OpenAiChatRequest { Messages = [new() { Role = "assistant", ToolCalls = [new() { Id=id, Type=type, Function=new() {Name=name, Arguments="{}"} }] }] };
        var error = Assert.Throws<GatewayException>(() => OpenAiMapper.ToChatRequest(dto));
        Assert.Equal(GatewayErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(id, dto.Messages[0].ToolCalls![0].Id);
    }

    [Fact]
    public void ValidAssistantToolCallPreservesExactIdentityAndArguments()
    {
        var dto = new OpenAiChatRequest { Messages = [new() { Role = "assistant", ToolCalls = [new() { Id="call-owned", Type="function", Function=new() {Name="owned", Arguments="{\"owned\":1}"} }] }] };
        var call = Assert.Single(Assert.Single(OpenAiMapper.ToChatRequest(dto).Messages).ToolCalls!);
        Assert.Equal(new ToolCall("call-owned", "owned", "{\"owned\":1}"), call);
    }

    [Theory]
    [InlineData("Basic b3duZWQ6Zml4dHVyZQ==")]
    [InlineData("https://owned.invalid/path?api_key=owned-value")]
    [InlineData("https://owned.invalid/path?%61ccess_token=owned-value")]
    public async Task AuthenticationValuesCannotReachPersistedProfileOrPublicEnvironment(string value)
    {
        var root=Directory.CreateTempSubdirectory("llmgw-owned-r3-secret-");
        try
        {
            var file=Path.Combine(root.FullName,"accounts.json");
            var store=JsonAccountStore.Load(new() {AccountsFile=file,DiscoverProfiles=false}, []);
            var before=await File.ReadAllBytesAsync(file);
            var profile=new AccountProfile { Id="owned",Provider=ProviderKind.Grok,Environment=new() {["PROXY_AUTH"]=value} };
            var error=await Assert.ThrowsAsync<GatewayException>(() => store.AddAsync(profile));
            Assert.Equal(GatewayErrorKind.InvalidRequest,error.Kind);
            Assert.Equal(before,await File.ReadAllBytesAsync(file));
            Assert.Empty(AccountEnvironment.WithoutSecrets(profile.Environment));
            Assert.Equal(value,profile.Environment["PROXY_AUTH"]);
        }
        finally {root.Delete(true);}
    }

    [Theory]
    [InlineData("discovery-dangerous")]
    [InlineData("discovery-provider")]
    [InlineData("write-provider")]
    public async Task InvalidDiscoveredOrCallerProviderCannotBePersisted(string mode)
    {
        var root=Directory.CreateTempSubdirectory("llmgw-owned-r3-discovery-");
        try
        {
            var file=Path.Combine(root.FullName,"accounts.json");
            var options=new GatewayOptions {AccountsFile=file,DiscoverProfiles=false};
            var store=JsonAccountStore.Load(options,[]);
            var before=await File.ReadAllBytesAsync(file);
            var account=new AccountProfile {Id="discovered-owned",Provider=mode.EndsWith("provider") ? (ProviderKind)999 : ProviderKind.Grok};
            if (mode=="discovery-dangerous") account.ExtraArguments=["--dangerously-auto-approve"];
            Exception? error;
            if (mode=="write-provider") error=await Record.ExceptionAsync(() => store.AddAsync(account));
            else
            {
                options.DiscoverProfiles=true;
                error=Record.Exception(() => JsonAccountStore.Load(options,[new DiscoveryAdapter(account)]));
            }
            Assert.Equal(GatewayErrorKind.InvalidRequest,Assert.IsType<GatewayException>(error).Kind);
            Assert.Equal(before,await File.ReadAllBytesAsync(file));
        }
        finally {root.Delete(true);}
    }

    [Fact]
    public void DiscoveryStripsCredentialValuesWithoutMutatingAdapterOwnedProfile()
    {
        var root=Directory.CreateTempSubdirectory("llmgw-owned-r3-discovery-control-");
        try
        {
            var account=new AccountProfile {Id="discovered-owned",Provider=ProviderKind.Grok,Environment=new() {["XAI_API_KEY"]="xai-owned-fixture",["NOTE"]="ordinary-setting"}};
            var store=JsonAccountStore.Load(new() {AccountsFile=Path.Combine(root.FullName,"accounts.json"),DiscoverProfiles=true},[new DiscoveryAdapter(account)]);
            Assert.Equal(new Dictionary<string,string> {["NOTE"]="ordinary-setting"},store.Find(account.Id)!.Environment);
            Assert.Contains("XAI_API_KEY",account.Environment.Keys);
            Assert.DoesNotContain("xai-owned-fixture",File.ReadAllText(store.FilePath));
        }
        finally {root.Delete(true);}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulJsonBodyIsBoundedForGetAndPost(bool post)
    {
        var body="{\"data\":[],\"padding\":\""+new string('x',16*1024*1024)+"\"}";
        using var http=new HttpClient(new Handler(body)) {BaseAddress=new("https://owned.invalid/")};
        using var client=new OpenAiGatewayClient(http);
        var error=await Assert.ThrowsAsync<GatewayException>(async () =>
        {
            if (post) await client.CompleteAsync(new() {Model="owned",Messages=[ChatMessage.User("owned")]});
            else await client.GetAccountsAsync();
        });
        Assert.Equal(GatewayErrorKind.Upstream,error.Kind);
        Assert.Contains("16 MiB",error.Message);
    }

    [Fact]
    public async Task ManySmallValidSseEventsCannotCreateAnUnboundedToolDictionary()
    {
        var body=new StringBuilder();
        for(var i=0;i<129;i++) body.Append("data: {\"id\":\"owned\",\"model\":\"owned\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":").Append(i).Append(",\"id\":\"call-"+i+"\",\"type\":\"function\",\"function\":{\"name\":\"owned\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n");
        body.Append("data: [DONE]\n\n");
        using var http=new HttpClient(new Handler(body.ToString(),"text/event-stream")) {BaseAddress=new("https://owned.invalid/")};
        using var client=new OpenAiGatewayClient(http);
        var completed=false;
        var error=await Assert.ThrowsAsync<GatewayException>(async () =>
        {
            await foreach(var update in client.StreamAsync(new() {Model="owned",Messages=[ChatMessage.User("owned")]})) completed |= update.Kind==ChatUpdateKind.Completed;
        });
        Assert.False(completed);
        Assert.Equal(GatewayErrorKind.Upstream,error.Kind);
    }

    [Theory]
    [InlineData(false, 4200)]
    [InlineData(true, 257)]
    public async Task SmallSseFragmentsCannotGrowTextOrOneToolArgumentWithoutBound(bool arguments, int chunks)
    {
        var text=new string('x',4096);
        object delta=arguments
            ? new {tool_calls=new[] {new {index=0,id="call-owned",type="function",function=new {name="owned",arguments=text}}}}
            : new {content=text};
        var packet="data: "+System.Text.Json.JsonSerializer.Serialize(new {id="owned",model="owned",created=1,
            choices=new[] {new {index=0,delta,finish_reason=arguments ? "tool_calls" : "stop"}}})+"\n\n";
        var body=new StringBuilder();
        for(var i=0;i<chunks;i++) body.Append(packet);
        body.Append("data: [DONE]\n\n");
        using var http=new HttpClient(new Handler(body.ToString(),"text/event-stream")) {BaseAddress=new("https://owned.invalid/")};
        using var client=new OpenAiGatewayClient(http);
        var completed=false;
        var error=await Assert.ThrowsAsync<GatewayException>(async () =>
        {
            await foreach(var update in client.StreamAsync(new() {Model="owned",Messages=[ChatMessage.User("owned")]})) completed |= update.Kind==ChatUpdateKind.Completed;
        });
        Assert.False(completed);
        Assert.Equal(GatewayErrorKind.Upstream,error.Kind);
        Assert.Contains("лимит",error.Message);
    }

    private sealed class Handler(string body,string type="application/json") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(body,Encoding.UTF8,type)});
    }
    private sealed class DiscoveryAdapter(AccountProfile account) : IProviderAdapter
    {
        public ProviderKind Provider=>ProviderKind.Grok;
        public string DisplayName=>"Owned discovery fixture";
        public string DefaultExecutable=>"owned";
        public ProviderCapabilities Capabilities=>throw new NotSupportedException();
        public IEnumerable<AccountProfile> DiscoverProfiles()=>[account];
        public string? ResolveExecutable(AccountProfile a)=>throw new NotSupportedException();
        public Task<AccountStatus> GetStatusAsync(AccountProfile a,CancellationToken t)=>throw new NotSupportedException();
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile a,CancellationToken t)=>throw new NotSupportedException();
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile a,CancellationToken t)=>throw new NotSupportedException();
        public IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile a,NativeChatRequest r,CancellationToken t)=>throw new NotSupportedException();
        public Task StartInteractiveLoginAsync(AccountProfile a,CancellationToken t)=>throw new NotSupportedException();
    }
}
