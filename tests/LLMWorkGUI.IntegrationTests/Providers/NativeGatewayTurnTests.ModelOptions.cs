using LLMGateway.Core;
using LLMGateway.Native.Adapters;
using LLMGateway.Native;
using Microsoft.Extensions.Logging.Abstractions;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using System.Text.Json;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class NativeGatewayTurnTests
{
    [Theory]
    [InlineData(true,false,false)]
    [InlineData(true,true,false)]
    [InlineData(true,false,true)]
    [InlineData(false,false,false)]
    public async Task CodexNativeRpcOptionsRequireAuthenticationBeforeAndAfterModelList(bool initiallyReady,bool changedIdentity,bool endedAuth)
    {
        await Ready();
        var paths=CreateDispatchProcess();
        var script=Path.Combine(_db.GetWorkspacePath(),"node_modules","dispatch-fixture","cli.mjs");
        var before=initiallyReady?"{type:'chatgpt',email:'before@example.invalid',planType:'fixture'}":"null";
        var after=endedAuth?"null":changedIdentity?"{type:'chatgpt',email:'after@example.invalid',planType:'fixture'}":before;
        var body="""
            import fs from 'node:fs';
            import readline from 'node:readline';
            let reads=0;
            for await (const line of readline.createInterface({input:process.stdin})) {
              const q=JSON.parse(line); if(q.id===undefined) continue;
              fs.appendFileSync('rpc-methods.txt',q.method+'\n');
              if(q.jsonrpc!==undefined) process.exit(9);
              let result={userAgent:'fixture/1.0.0'};
              if(q.method==='account/read') result={account: ++reads===1 ? BEFORE : AFTER,requiresOpenaiAuth:true};
              if(q.method==='model/list') result={data:[{model:'model',supportedReasoningEfforts:[{reasoningEffort:'high',description:'fixture'}]}],nextCursor:null};
              process.stdout.write(JSON.stringify({id:q.id,result})+'\n');
            }
            """;
        await File.WriteAllTextAsync(script,body.Replace("BEFORE",before).Replace("AFTER",after));
        UseDispatchGateway(new CodexAdapter(new ExecutableResolver(),new GatewayOptions(),NullLogger<CodexAdapter>.Instance),paths.Shim);
        if(initiallyReady && !changedIdentity && !endedAuth)
            Assert.Equal(new[]{"high"},(await _gateway!.DiscoverModelOptionsAsync("work","model")).ReasoningEfforts);
        else
            Assert.Equal(GatewayErrorKind.AuthenticationRequired,(await Assert.ThrowsAsync<GatewayException>(
                ()=>_gateway!.DiscoverModelOptionsAsync("work","model"))).Kind);
        var methods=await File.ReadAllLinesAsync(Path.Combine(_db.GetWorkspacePath(),"rpc-methods.txt"));
        Assert.Equal(initiallyReady,methods.Contains("model/list"));
        Assert.DoesNotContain(methods,m=>m.StartsWith("thread/",StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthenticatedOptionsPublisherSavesOnlyReportedOptionsWithoutInventingChatOrTools()
    {
        await Ready();
        var configuration = new SqliteModelRouteConfigurationService(_db.Factory,_guard!,new SensitiveDataFilter(),TimeProvider.System);
        var before = await configuration.ReadAsync();
        var model=Assert.Single(before.Models.Where(m=>m.Backend==BackendType.NativeGateway));
        var account=Assert.Single(before.Accounts.Where(a=>a.ProfileId==model.ProfileId));
        var store=new SqliteModelCapabilityEvidenceStore(_db.Factory,_guard!,new SensitiveDataFilter(),TimeProvider.System);
        var discovery=new NativeGatewayModelCapabilityDiscoveryService(()=>_gateway!,_db.Factory,store,_guard!,TimeProvider.System);
        await discovery.DiscoverAsync(model.Id,account.Id);
        var evidence=Assert.Single((await configuration.ReadAsync()).Capabilities);
        Assert.Equal(new[]{"high","max"},evidence.ReasoningEfforts);
        Assert.Equal(ModelCapabilityFlags.ReasoningVariants,evidence.Flags);
        Assert.Equal(ModelProvenance.PluginReported,evidence.Provenance);
        Assert.Equal("Explicit authenticated discovery fixture",evidence.DiscoverySource);
        Assert.Empty(evidence.SpeedModes); Assert.Empty(evidence.ExecutionModes); Assert.Null(evidence.ContextLimit);
        Assert.Equal(TimeSpan.FromMinutes(10),evidence.ExpiresAtUtc-evidence.ObservedAtUtc);
        Assert.Equal(0,_adapter.Calls);
    }

    [Theory]
    [InlineData("UPDATE Models SET ProviderModelId='changed'")]
    [InlineData("UPDATE Accounts SET ProviderNativeId='changed'")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'")]
    public async Task DiscoveryPublisherRejectsScopeMutationDuringNativeAwait(string mutation)
    {
        await Ready();
        var configuration=new SqliteModelRouteConfigurationService(_db.Factory,_guard!,new SensitiveDataFilter(),TimeProvider.System);
        var before=await configuration.ReadAsync();
        _adapter.DuringOptionsDiscovery=async()=> { await Sql(mutation); };
        var store=new SqliteModelCapabilityEvidenceStore(_db.Factory,_guard!,new SensitiveDataFilter(),TimeProvider.System);
        var discovery=new NativeGatewayModelCapabilityDiscoveryService(()=>_gateway!,_db.Factory,store,_guard!,TimeProvider.System);
        var model=Assert.Single(before.Models.Where(m=>m.Backend==BackendType.NativeGateway));
        var account=Assert.Single(before.Accounts.Where(a=>a.ProfileId==model.ProfileId));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>discovery.DiscoverAsync(model.Id,account.Id));
        Assert.Empty((await configuration.ReadAsync()).Capabilities);
    }

    [Fact]
    public async Task GatewayOptionsDiscoveryRejectsNativeProfileMutationBeforePublishing()
    {
        await Ready();
        _adapter.DuringOptionsDiscovery=()=> { _store.Account.ConfigDirectory="changed"; return Task.CompletedTask; };
        await Assert.ThrowsAsync<GatewayException>(()=>_gateway!.DiscoverModelOptionsAsync("work","model"));
    }

    [Theory]
    [InlineData("[{\"reasoningEffort\":\"ultra\"},{\"reasoningEffort\":\"high\"}]",true)]
    [InlineData("[]",true)]
    [InlineData("null",false)]
    [InlineData("[{\"reasoningEffort\":\"high\"},{\"reasoningEffort\":\"high\"}]",false)]
    [InlineData("[{\"reasoningEffort\":\" high\"}]",false)]
    [InlineData("[{\"reasoningEffort\":false}]",false)]
    [InlineData("[{\"reasoningEffort\":\"high\",\"reasoningEffort\":\"max\"}]",false)]
    public void CodexOptionsParserUsesActualPerModelMetadataAndRejectsMalformedEvidence(string options,bool valid)
    {
        using var json=JsonDocument.Parse("{\"data\":[{\"model\":\"model\",\"supportedReasoningEfforts\":"+options+"}]}");
        if(valid) Assert.Single(CodexAdapter.ParseReasoningOptions(json.RootElement,"model"));
        else Assert.Throws<GatewayException>(()=>CodexAdapter.ParseReasoningOptions(json.RootElement,"model").ToArray());
        Assert.Empty(CodexAdapter.ParseReasoningOptions(json.RootElement,"other"));
    }

    [Fact]
    public async Task GatewayOptionsDiscoveryRejectsChangedApiCredentialWithSameVariableName()
    {
        await Ready();
        var variable="LLMWGUI_DISCOVERY_TEST_"+Guid.NewGuid().ToString("N");
        _store.Account.AuthMode=AccountAuthMode.ApiKeyFromEnvironment;
        _store.Account.ApiKeyVariable=variable;
        Environment.SetEnvironmentVariable(variable,"fixture-before");
        try
        {
            _adapter.DuringOptionsDiscovery=()=> { Environment.SetEnvironmentVariable(variable,"fixture-after"); return Task.CompletedTask; };
            await Assert.ThrowsAsync<GatewayException>(()=>_gateway!.DiscoverModelOptionsAsync("work","model"));
        }
        finally { Environment.SetEnvironmentVariable(variable,null); }
    }
}
