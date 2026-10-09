using LLMWorkGUI.Backends.Abstractions.Mirasim;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

public sealed partial class MirasimEgressTests
{
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task ResponseAuthorityReviewNonSuccessHttpCannotCommitDoneOrReleaseWriter(int status)
    {
        await using var f = await OwnedHostFixture.Create(_output);
        var binding = await f.CreateSession();
        f.Server.TurnHttpStatus = status;
        var result = await f.Service.ExecuteTurnAsync(f.Request(binding));

        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.False(result.IsTerminal);
        Assert.Null(result.AssistantResponse);
        Assert.Equal("Ambiguous", await f.Db.Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(1L, await f.Db.Sql("SELECT COUNT(*) FROM Sessions WHERE ActiveExecutionId IS NOT NULL"));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Host.StopAsync());
        Assert.Single(f.Server.Paths, path => path.EndsWith("/turns", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("done", false)]
    [InlineData("done", true)]
    [InlineData("cancelled", false)]
    [InlineData("cancelled", true)]
    [InlineData("incomplete", false)]
    [InlineData("incomplete", true)]
    [InlineData("error", false)]
    [InlineData("error", true)]
    [InlineData("running", false)]
    [InlineData("running", true)]
    public async Task ResponseAuthorityReviewInitialResponseWithoutUsableTurnIdentityRetainsWriter(string phase, bool blank)
    {
        await using var f = await OwnedHostFixture.Create(_output);
        var binding = await f.CreateSession();
        f.Server.TurnPhase = phase;
        f.Server.OverrideTurnIdentity = true;
        f.Server.TurnIdentityOverride = blank ? "   " : null;
        var result = await f.Service.ExecuteTurnAsync(f.Request(binding));

        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.False(result.IsTerminal);
        Assert.Null(result.AssistantResponse);
        Assert.Equal("Ambiguous", await f.Db.Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(1L, await f.Db.Sql("SELECT COUNT(*) FROM Sessions WHERE ActiveExecutionId IS NOT NULL"));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Host.StopAsync());
        Assert.Single(f.Server.Paths, path => path.EndsWith("/turns", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(503, MirasimTurnErrorClass.UpstreamUnavailable503)]
    [InlineData(422, MirasimTurnErrorClass.PlatformBusy422)]
    public async Task ResponseAuthorityReviewEstablishedTypedRefusalStillReleasesWriter(int status, MirasimTurnErrorClass expected)
    {
        await using var f = await OwnedHostFixture.Create(_output);
        var binding = await f.CreateSession();
        f.Server.TurnHttpStatus = status;
        var result = await f.Service.ExecuteTurnAsync(f.Request(binding));

        Assert.Equal(MirasimTurnStatus.Failed, result.Status);
        Assert.Equal(expected, result.ErrorClass);
        Assert.Null(result.AssistantResponse);
        Assert.Equal("Failed", await f.Db.Sql("SELECT State FROM Executions"));
        Assert.Equal(0L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        await f.Host.StopAsync();
    }

    [Fact]
    public async Task ResponseAuthorityReviewValidSynchronousCompletionStillCommitsBeforeRelease()
    {
        await using var f = await OwnedHostFixture.Create(_output);
        var binding = await f.CreateSession();
        f.Db.Locks.BeforeRelease = async () => Assert.Equal("Succeeded", await f.Db.Sql("SELECT State FROM Executions"));
        var result = await f.Service.ExecuteTurnAsync(f.Request(binding));

        Assert.Equal(MirasimTurnStatus.Completed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.TurnId));
        Assert.Equal("synthetic response", result.AssistantResponse);
        Assert.Equal(0L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        await f.Host.StopAsync();
    }
}
