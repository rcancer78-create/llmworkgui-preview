using LLMWorkGUI.Backends.Abstractions.Mirasim;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

public sealed partial class MirasimEgressTests
{
    [Theory]
    [InlineData("probe")]
    [InlineData("create")]
    [InlineData("turn")]
    public async Task StockDependencyInjectionTransportNeverFollowsTheApprovedHostsRedirectToAnotherService(string operation)
    {
        await using var source = await OwnedHostFixture.Create(_output);
        await using var destination = new OwnedMirasimHttpServer
        { FixedResponseBody = "{\"sessionKey\":\"redirected-session\",\"phase\":\"done\",\"turnId\":\"redirected-turn\"}" };
        MirasimSessionBinding? binding = null;
        if (operation == "turn") binding = await source.CreateSession();
        source.Server.RedirectLocation = $"http://127.0.0.1:{destination.Port}/unapproved-target";
        if (operation == "probe")
            await source.Host.Services.GetRequiredService<IMirasimClient>().ProbeHealthAsync();
        else if (operation == "create")
            await Record.ExceptionAsync(() => source.CreateSession());
        else
        {
            var result = await source.Service.ExecuteTurnAsync(source.Request(binding!));
            Assert.NotEqual(MirasimTurnStatus.Completed, result.Status);
            Assert.Equal(1L, await source.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        }
        // Both endpoints are owned loopback listeners. The assertion proves no redirect escapes
        // the approved endpoint, without contacting an external service or exposing credentials.
        Assert.Empty(destination.Paths);
        Assert.Contains(source.Server.Paths, path => operation != "turn" || path.EndsWith("/turns", StringComparison.Ordinal));
    }
}
