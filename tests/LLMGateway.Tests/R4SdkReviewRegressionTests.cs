using System.Text.Json;
using System.Text.Json.Nodes;
using LLMGateway.Core;
using LLMGateway.Core.OpenAi;

namespace LLMGateway.Tests;

public sealed class R4SdkReviewRegressionTests
{
    [Theory]
    [InlineData("NOTE", "-----BEGIN EC PRIVATE KEY-----\nsynthetic-fixture\n-----END EC PRIVATE KEY-----")]
    [InlineData("NOTE", "-----BEGIN ENCRYPTED PRIVATE KEY-----\nsynthetic-fixture\n-----END ENCRYPTED PRIVATE KEY-----")]
    [InlineData("NOTE", "-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-fixture\n-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("AUTHORIZATION", "Token synthetic-fixture")]
    [InlineData("COOKIE", "owned-session=synthetic-fixture")]
    public async Task RecognizedPrivateKeyAndAuthorizationSettingsCannotBePersistedOrProjected(string name, string value)
    {
        var directory=Directory.CreateTempSubdirectory("llmgw-owned-r4-secret-");
        try
        {
            var path=Path.Combine(directory.FullName,"accounts.json");
            var store=JsonAccountStore.Load(new() {AccountsFile=path,DiscoverProfiles=false}, []);
            var before=await File.ReadAllBytesAsync(path);
            var profile=new AccountProfile {Id="owned",Provider=ProviderKind.Grok,Environment=new() {[name]=value}};
            Assert.True(AccountEnvironment.IsSecret(name,value));
            Assert.Empty(AccountEnvironment.WithoutSecrets(profile.Environment));
            Assert.Equal(GatewayErrorKind.InvalidRequest,(await Assert.ThrowsAsync<GatewayException>(() => store.AddAsync(profile))).Kind);
            Assert.Equal(before,await File.ReadAllBytesAsync(path));
            Assert.Equal(value,profile.Environment[name]);
        }
        finally {directory.Delete(true);}
    }

    [Fact]
    public void AuthenticationModeAndOrdinarySettingsRemainUsable()
    {
        var environment=new Dictionary<string,string> { ["AUTH_MODE"]="oauth",["LANG"]="ru-RU",["NOTE"]="owned synthetic note"};
        Assert.Equal(environment,AccountEnvironment.WithoutSecrets(environment));
        Assert.All(environment,pair => Assert.False(AccountEnvironment.IsSecret(pair.Key,pair.Value)));
    }

    [Theory]
    [InlineData("\"banana\"")]
    [InlineData("\"\"")]
    [InlineData("{\"type\":\"bogus\",\"function\":{\"name\":\"owned\"}}")]
    [InlineData("{\"function\":{\"name\":\"owned\"}}")]
    [InlineData("{\"type\":\"function\",\"function\":{\"name\":\" \"}}")]
    public void InvalidWireToolChoiceIsNotConvertedToNamedExecution(string wire)
    {
        var dto=Request(wire);
        Assert.Equal(GatewayErrorKind.InvalidRequest,Assert.Throws<GatewayException>(() => OpenAiMapper.ToChatRequest(dto)).Kind);
    }

    [Theory]
    [InlineData("\"auto\"","auto")]
    [InlineData("\"none\"","none")]
    [InlineData("\"required\"","required")]
    [InlineData("{\"type\":\"function\",\"function\":{\"name\":\"owned\"}}","owned")]
    public void ValidWireToolChoicePreservesExplicitMeaning(string wire,string expected) =>
        Assert.Equal(expected,OpenAiMapper.ToChatRequest(Request(wire)).ToolChoice);

    [Theory]
    [InlineData("multiple-active")]
    [InlineData("no-active")]
    [InlineData("disabled-active")]
    [InlineData("blank-display")]
    public async Task LoadRepairsAreDurableAndSecondReadDoesNotRewrite(string mode)
    {
        var directory=Directory.CreateTempSubdirectory("llmgw-owned-r4-normalize-");
        try
        {
            var path=Path.Combine(directory.FullName,"accounts.json");
            JsonAccountStore.Load(new() {AccountsFile=path,DiscoverProfiles=false}, []);
            var node=JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var accounts=node["accounts"]!.AsArray(); var first=accounts[0]!;
            var duplicate=first.DeepClone(); duplicate["id"]="owned-other"; duplicate["is_active"]=false; accounts.Add(duplicate);
            if (mode=="multiple-active") duplicate["is_active"]=true;
            if (mode=="no-active") first["is_active"]=false;
            if (mode=="disabled-active") first["enabled"]=false;
            if (mode=="blank-display") first["display_name"]=" ";
            await File.WriteAllTextAsync(path,node.ToJsonString()); var before=await File.ReadAllBytesAsync(path);
            JsonAccountStore.Load(new() {AccountsFile=path,DiscoverProfiles=false}, []);
            var after=await File.ReadAllBytesAsync(path); Assert.False(before.SequenceEqual(after));
            var persisted=JsonNode.Parse(after)!; var repaired=persisted["accounts"]!.AsArray();
            Assert.Equal(1,repaired.Count(account => account!["provider"]!.ToJsonString()==first["provider"]!.ToJsonString() && account["is_active"]!.GetValue<bool>()));
            if (mode=="blank-display") Assert.Equal(first["id"]!.GetValue<string>(),repaired[0]!["display_name"]!.GetValue<string>());
            if (mode=="disabled-active") Assert.True(repaired.Single(account => account!["id"]!.GetValue<string>()=="owned-other")!["is_active"]!.GetValue<bool>());
            JsonAccountStore.Load(new() {AccountsFile=path,DiscoverProfiles=false}, []);
            Assert.Equal(after,await File.ReadAllBytesAsync(path));
        }
        finally {directory.Delete(true);}
    }

    private static OpenAiChatRequest Request(string wire) => new()
    {
        Messages=[new() {Role="user",Content=JsonSerializer.SerializeToElement("Synthetic input")}],
        ToolChoice=JsonDocument.Parse(wire).RootElement.Clone()
    };
}
