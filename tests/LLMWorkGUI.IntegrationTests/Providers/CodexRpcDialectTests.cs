using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class CodexRpcDialectTests
{
    [Theory]
    [InlineData(false,"{\"id\":1,\"result\":{\"ok\":true}}",false)]
    [InlineData(true,"{\"id\":1,\"result\":{\"ok\":true}}",true)]
    [InlineData(true,"{\"jsonrpc\":\"1.0\",\"id\":1,\"result\":{}}",false)]
    [InlineData(true,"{\"jsonrpc\":null,\"id\":1,\"result\":{}}",false)]
    [InlineData(true,"{\"id\":1,\"error\":42}",false)]
    [InlineData(true,"{\"id\":1,\"result\":{},\"error\":{\"code\":-1,\"message\":\"fixture\"}}",false)]
    public async Task CodexHeaderlessRepliesAreAcceptedOnlyOnExplicitDialect(bool codex,string reply,bool valid)
    {
        using var directory=new TestDirectory();
        var script=directory.GetPath("rpc.ps1");
        await File.WriteAllTextAsync(script,"$line=[Console]::ReadLine(); [IO.File]::WriteAllText('request.json',$line); [Console]::WriteLine('"+reply+"'); Start-Sleep -Seconds 30");
        var shell=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
        var launch=new NativeLaunch(new(shell,[],LaunchKind.Direct,shell),["-NoProfile","-NonInteractive","-File",script],
            new Dictionary<string,string?>(),directory.Root);
        await using var client=codex?JsonRpcStdioClient.StartCodexAppServer(launch):JsonRpcStdioClient.Start(launch);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if(valid) Assert.True((await client.RequestAsync("initialize",new{},TimeSpan.FromSeconds(10),deadline.Token)).GetProperty("ok").GetBoolean());
        else await Assert.ThrowsAsync<GatewayException>(()=>client.RequestAsync("initialize",new{},TimeSpan.FromSeconds(10),deadline.Token));
        using var request=JsonDocument.Parse(await File.ReadAllTextAsync(directory.GetPath("request.json"),deadline.Token));
        Assert.Equal(!codex,request.RootElement.TryGetProperty("jsonrpc",out _));
        Assert.Equal("initialize",request.RootElement.GetProperty("method").GetString());
    }
}
