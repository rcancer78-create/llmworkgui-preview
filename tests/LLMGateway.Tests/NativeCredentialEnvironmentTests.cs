using System.Reflection;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMGateway.Tests;

public sealed class NativeCredentialEnvironmentTests
{
    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("CODEX_API_KEY")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("GEMINI_API_KEY")]
    [InlineData("CURSOR_API_KEY")]
    [InlineData("GROK_API_KEY")]
    [InlineData("XAI_API_KEY")]
    public void NativeLoginExplicitlyRemovesInheritedProviderKey(string variable)
    {
        var environment=Build(new AccountProfile { Id="native-login",Provider=ProviderKind.Codex });
        Assert.True(environment.ContainsKey(variable));
        Assert.Null(environment[variable]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeLoginAndInteractiveLoginRemoveConfiguredSourceAlias(bool forLogin)
    {
        var environment=Build(new AccountProfile { Id="native-login",Provider=ProviderKind.Codex,
            ApiKeyVariable="CUSTOM_PROFILE_SOURCE" },forLogin);
        Assert.True(environment.ContainsKey("CUSTOM_PROFILE_SOURCE"));
        Assert.Null(environment["CUSTOM_PROFILE_SOURCE"]);
    }

    [Fact]
    public void InteractiveLoginNeverNeedsOrForwardsApiKeyEvenForApiModeProfile()
    {
        var profile=new AccountProfile { Id="login",Provider=ProviderKind.Codex,
            AuthMode=AccountAuthMode.ApiKeyFromEnvironment,
            ApiKeyVariable="LLMWORKGUI_UNSET_LOGIN_SOURCE_"+Guid.NewGuid().ToString("N") };
        var environment=Build(profile,true);
        Assert.Null(environment["CODEX_API_KEY"]);
        Assert.Null(environment[profile.ApiKeyVariable]);
    }

    [Fact]
    public void SelectedApiKeyIsMappedToTargetWhileItsSourceAndForeignKeysAreRemoved()
    {
        var variable="LLMWORKGUI_SYNTHETIC_SOURCE_"+Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable,"synthetic-only-selected-key");
        try
        {
            var profile=new AccountProfile { Id="api",Provider=ProviderKind.Codex,
                AuthMode=AccountAuthMode.ApiKeyFromEnvironment,ApiKeyVariable=variable };
            profile.Environment["AUTH_MODE"]="ordinary-setting";
            var environment=Build(profile);
            Assert.Equal("synthetic-only-selected-key",environment["CODEX_API_KEY"]);
            Assert.Null(environment[variable]);
            Assert.Null(environment["OPENAI_API_KEY"]);
            Assert.Null(environment["ANTHROPIC_API_KEY"]);
            Assert.Equal("ordinary-setting",environment["AUTH_MODE"]);
            Assert.Single(profile.Environment);
        }
        finally { Environment.SetEnvironmentVariable(variable,null); }
    }

    [Fact]
    public void SourceAliasMatchingSelectedTargetCaseInsensitivelyDoesNotEraseSelectedKey()
    {
        var environment=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase)
        { ["CODEX_API_KEY"]="synthetic-selected",["OPENAI_API_KEY"]="synthetic-foreign" };
        AccountEnvironment.RemoveForeignCredentials(environment,"CODEX_API_KEY",["codex_api_key"]);
        Assert.Equal("synthetic-selected",environment["CODEX_API_KEY"]);
        Assert.Null(environment["OPENAI_API_KEY"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceAliasMatchingConfigVariableCannotEraseExplicitConfigDirectory(bool forLogin)
    {
        var directory=Path.Combine(Path.GetTempPath(),"credential-config-"+Guid.NewGuid().ToString("N"));
        try
        {
            var profile=new AccountProfile { Id="config-collision",Provider=ProviderKind.Codex,
                ApiKeyVariable="CODEX_HOME",ConfigDirectory=directory };
            var environment=Build(profile,forLogin);
            Assert.Equal(Path.GetFullPath(directory),environment["CODEX_HOME"]);
            Assert.Null(environment["CODEX_API_KEY"]);
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllCurrentStoreProfileAliasesAreRemovedBeforeExplicitConfigMapping(bool forLogin)
    {
        var directory=Path.Combine(Path.GetTempPath(),"credential-store-"+Guid.NewGuid().ToString("N"));
        try
        {
            var options=new GatewayOptions { AccountsFile=Path.Combine(directory,"accounts.json"),DiscoverProfiles=false };
            var adapters=NativeGateway.CreateAdapters(options,NullLoggerFactory.Instance);
            var store=JsonAccountStore.Load(options,adapters);
            using var gateway=new LlmGateway(store,adapters,options);
            var adapter=adapters.OfType<CodexAdapter>().Single();
            var alias="OTHER_PROFILE_OPAQUE_ALIAS";
            await store.AddAsync(new AccountProfile { Id="other",Provider=ProviderKind.Claude,ApiKeyVariable=alias });
            var selected=new AccountProfile { Id="selected",Provider=ProviderKind.Codex,ConfigDirectory=directory };
            var first=Build(selected,forLogin,adapter);
            Assert.True(first.ContainsKey(alias));
            Assert.Null(first[alias]);
            await store.UpdateAsync(new AccountProfile { Id="other",Provider=ProviderKind.Claude,ApiKeyVariable="CODEX_HOME" });
            var after=Build(selected,forLogin,adapter);
            Assert.Equal(Path.GetFullPath(directory),after["CODEX_HOME"]);
            Assert.Null(after["CODEX_API_KEY"]);
            Assert.False(after.ContainsKey(alias));
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedNativeChildReceivesSelectedTargetButNoOtherProfileAlias(bool apiMode)
    {
        var directory=Path.Combine(Path.GetTempPath(),"credential-child-"+Guid.NewGuid().ToString("N"));
        var alias="LLMWORKGUI_OTHER_ALIAS_"+Guid.NewGuid().ToString("N");
        var selectedAlias="LLMWORKGUI_SELECTED_ALIAS_"+Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(alias,"synthetic-foreign");
        Environment.SetEnvironmentVariable(selectedAlias,"synthetic-selected");
        try
        {
            var options=new GatewayOptions { AccountsFile=Path.Combine(directory,"accounts.json"),DiscoverProfiles=false };
            var adapters=NativeGateway.CreateAdapters(options,NullLoggerFactory.Instance);
            var store=JsonAccountStore.Load(options,adapters);
            await store.AddAsync(new AccountProfile { Id="other",Provider=ProviderKind.Claude,ApiKeyVariable=alias });
            using var gateway=new LlmGateway(store,adapters,options);
            var selected=new AccountProfile { Id="selected",Provider=ProviderKind.Codex,ApiKeyVariable=selectedAlias,
                AuthMode=apiMode ? AccountAuthMode.ApiKeyFromEnvironment : AccountAuthMode.NativeLogin };
            var delta=Build(selected,false,adapters.OfType<CodexAdapter>().Single());
            var executable=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
            var command="@{ foreignPresent=($null -ne [Environment]::GetEnvironmentVariable('"+alias+"')); "
                + "sourcePresent=($null -ne [Environment]::GetEnvironmentVariable('"+selectedAlias+"')); "
                + "selectedTarget=($env:CODEX_API_KEY -eq 'synthetic-selected') } | ConvertTo-Json -Compress";
            var result=await NativeProcess.RunAsync(new NativeLaunch(new LaunchTarget(executable,[],LaunchKind.Direct,executable),
                ["-NoProfile","-NonInteractive","-Command",command],delta,directory),TimeSpan.FromSeconds(15),CancellationToken.None);
            Assert.True(result.Success);
            using var output=JsonDocument.Parse(result.StandardOutput);
            Assert.False(output.RootElement.GetProperty("foreignPresent").GetBoolean());
            Assert.False(output.RootElement.GetProperty("sourcePresent").GetBoolean());
            Assert.Equal(apiMode,output.RootElement.GetProperty("selectedTarget").GetBoolean());
            Assert.Equal("synthetic-foreign",Environment.GetEnvironmentVariable(alias));
            Assert.Equal("synthetic-selected",Environment.GetEnvironmentVariable(selectedAlias));
        }
        finally
        {
            Environment.SetEnvironmentVariable(alias,null);
            Environment.SetEnvironmentVariable(selectedAlias,null);
            if(Directory.Exists(directory)) Directory.Delete(directory,true);
        }
    }

    [Fact]
    public async Task SharedOptionsDoNotShareStoreBindingAndAdapterRebindingFailsClosed()
    {
        var directory=Path.Combine(Path.GetTempPath(),"credential-binding-"+Guid.NewGuid().ToString("N"));
        try
        {
            var options=new GatewayOptions { AccountsFile=Path.Combine(directory,"first.json"),DiscoverProfiles=false };
            var firstAdapters=NativeGateway.CreateAdapters(options,NullLoggerFactory.Instance);
            var firstStore=JsonAccountStore.Load(options,firstAdapters);
            await firstStore.AddAsync(new AccountProfile { Id="first",Provider=ProviderKind.Claude,ApiKeyVariable="FIRST_OPAQUE_ALIAS" });
            using var first=new LlmGateway(firstStore,firstAdapters,options);
            options.AccountsFile=Path.Combine(directory,"second.json");
            var secondAdapters=NativeGateway.CreateAdapters(options,NullLoggerFactory.Instance);
            var secondStore=JsonAccountStore.Load(options,secondAdapters);
            await secondStore.AddAsync(new AccountProfile { Id="second",Provider=ProviderKind.Claude,ApiKeyVariable="SECOND_OPAQUE_ALIAS" });
            using var second=new LlmGateway(secondStore,secondAdapters,options);
            var selected=new AccountProfile { Id="selected",Provider=ProviderKind.Codex };
            var firstEnvironment=Build(selected,false,firstAdapters.OfType<CodexAdapter>().Single());
            var secondEnvironment=Build(selected,false,secondAdapters.OfType<CodexAdapter>().Single());
            Assert.Null(firstEnvironment["FIRST_OPAQUE_ALIAS"]);
            Assert.False(firstEnvironment.ContainsKey("SECOND_OPAQUE_ALIAS"));
            Assert.Null(secondEnvironment["SECOND_OPAQUE_ALIAS"]);
            Assert.False(secondEnvironment.ContainsKey("FIRST_OPAQUE_ALIAS"));
            Assert.Throws<InvalidOperationException>(()=>new LlmGateway(secondStore,firstAdapters,options));
            Assert.Null(Build(selected,false,firstAdapters.OfType<CodexAdapter>().Single())["FIRST_OPAQUE_ALIAS"]);
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    }

    [Fact]
    public void ConfiguredCredentialNamesAreScrubbedBeforeConfigMappingAndSelectedInjection()
    {
        var extra="LLMWORKGUI_CONFIGURED_ALIAS_"+Guid.NewGuid().ToString("N");
        var source="LLMWORKGUI_CONFIGURED_SOURCE_"+Guid.NewGuid().ToString("N");
        var directory=Path.Combine(Path.GetTempPath(),"credential-names-"+Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(extra,"synthetic-extra");
        Environment.SetEnvironmentVariable(source,"synthetic-selected");
        try
        {
            var options=new GatewayOptions { CredentialVariableNames=[extra,"CODEX_HOME","CODEX_API_KEY"] };
            var adapter=new CodexAdapter(new ExecutableResolver(),options,NullLogger<CodexAdapter>.Instance);
            var profile=new AccountProfile { Id="selected",Provider=ProviderKind.Codex,ApiKeyVariable=source,
                AuthMode=AccountAuthMode.ApiKeyFromEnvironment,ConfigDirectory=directory };
            var environment=Build(profile,false,adapter);
            Assert.True(environment.ContainsKey(extra));
            Assert.Null(environment[extra]);
            Assert.Equal(Path.GetFullPath(directory),environment["CODEX_HOME"]);
            Assert.Equal("synthetic-selected",environment["CODEX_API_KEY"]);
            Assert.Null(environment[source]);
            Assert.Equal("synthetic-extra",Environment.GetEnvironmentVariable(extra));
            Assert.Equal("synthetic-selected",Environment.GetEnvironmentVariable(source));
        }
        finally
        {
            Environment.SetEnvironmentVariable(extra,null);
            Environment.SetEnvironmentVariable(source,null);
            if(Directory.Exists(directory)) Directory.Delete(directory,true);
        }
    }

    private static IReadOnlyDictionary<string,string?> Build(AccountProfile profile,bool forLogin=false,CodexAdapter? suppliedAdapter=null)
    {
        var adapter=suppliedAdapter ?? new CodexAdapter(new ExecutableResolver(),new GatewayOptions(),NullLogger<CodexAdapter>.Instance);
        var method=typeof(NativeAdapterBase).GetMethod("BuildEnvironment",BindingFlags.Instance|BindingFlags.NonPublic)!;
        return (IReadOnlyDictionary<string,string?>)method.Invoke(adapter,[profile,forLogin])!;
    }
}
