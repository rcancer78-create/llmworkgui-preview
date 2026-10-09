using LLMWorkGUI.Backends.Abstractions.Mirasim;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

public sealed partial class MirasimEgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuingAnOwnedSessionCannotSilentlyReplaceItsNativeIdentityOrForgetPostDispatchUncertainty(bool changedKey)
    {
        await using var f = await OwnedHostFixture.Create(_output, maxSessions: 2);
        var original = await f.CreateSession();
        f.Server.ContinueSessionKey = changedKey ? "unconfirmed-replacement-session" : original.SessionKey;
        if (changedKey)
        {
            var failure = await Record.ExceptionAsync(() => f.Service.ContinueSessionAsync(original));
            Assert.NotNull(failure); // A response for another identity cannot confirm continuation of this one.
            Assert.Equal(2, f.Server.Paths.Count);
            Assert.Contains(f.Server.Paths, path => path == $"/api/sessions/{original.SessionKey}/continue");
            var before = f.Server.Paths.Count;
            var turn = await f.Service.ExecuteTurnAsync(f.Request(original));
            Assert.NotEqual(MirasimTurnStatus.Completed, turn.Status);
            Assert.NotEqual(MirasimTurnStatus.Running, turn.Status);
            Assert.Equal(before, f.Server.Paths.Count);
            Assert.Equal(0L, await f.Db.Sql("SELECT COUNT(*) FROM Executions"));
            Assert.Equal(0L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
            await Assert.ThrowsAnyAsync<Exception>(() => f.Host.StopAsync());
            // No statement that either remote session has terminated or that capacity is free.
        }
        else
        {
            var binding = original;
            for (var index = 0; index < 4; index++)
            {
                binding = await f.Service.ContinueSessionAsync(binding);
                Assert.Equal(original.SessionKey, binding.SessionKey);
                Assert.Equal(original.Harness, binding.Harness);
                Assert.Equal(original.ModelId, binding.ModelId);
                Assert.Equal(original.WorkspacePath, binding.WorkspacePath);
            }
            Assert.Equal(5, f.Server.Paths.Count);
            Assert.Equal(MirasimTurnStatus.Completed, (await f.Service.ExecuteTurnAsync(f.Request(binding))).Status);
            Assert.Equal("Succeeded", await f.Db.Sql("SELECT State FROM Executions"));
            Assert.Equal(0L, await f.Db.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
            await f.Host.StopAsync();
        }
    }
}
