using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Projects;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class ProjectDataPolicyTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly Guard _guard = new();
    private SqliteProjectDataPolicyService Service => new(_db.Factory, _guard, new FixedClock());
    public void Dispose() => _db.Dispose();

    private async Task Ready()
    {
        await _db.InitializeAsync();
        await _db.SeedRouteChainAsync();
    }

    private async Task Sql(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ExplicitChange_IsAtomicAuditedAndDoesNotChangeProjectMetadata()
    {
        await Ready();
        var before = Assert.Single(await Service.ListAsync());
        await Service.ChangeAsync(before, DataClassification.Restricted, true);
        var after = Assert.Single(await Service.ListAsync());
        Assert.Equal(DataClassification.Restricted, after.DataClassification);
        Assert.Equal(before.RootPath, after.RootPath);
        Assert.Equal(before.DisplayName, after.DisplayName);
        Assert.NotEqual(before.Revision, after.Revision);
        Assert.Equal(1, await _db.CountAsync("ProjectDataPolicyChanges", "PreviousClass='PrivateSource' AND NewClass='Restricted'"));
        Assert.Equal(1, await _db.CountAsync("Projects", "IsDirty=0 AND HasRequiredInstructions=1"));
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("secondary")]
    [InlineData("stale")]
    [InlineData("lock")]
    [InlineData("execution")]
    public async Task Refusal_PreservesClassAndAudit(string reason)
    {
        await Ready();
        var before = Assert.Single(await Service.ListAsync());
        if (reason == "secondary") _guard.Primary = false;
        if (reason == "stale") await Sql("UPDATE Projects SET DisplayName='Changed concurrently'");
        if (reason is "lock" or "execution")
        {
            await _db.SeedSessionAsync();
            await _db.SeedExecutionAsync("active", state: reason == "lock" ? "Succeeded" : "Running",
                endedAt: reason == "lock" ? DateTimeOffset.UtcNow : null);
            if (reason == "lock") await _db.InsertProjectLockAsync("lock", before.RootPath, "active");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ChangeAsync(before, DataClassification.PublicSource, reason != "consent"));
        Assert.Equal(before.DataClassification, Assert.Single(await Service.ListAsync()).DataClassification);
        Assert.Equal(0, await _db.CountAsync("ProjectDataPolicyChanges"));
    }

    [Fact]
    public async Task LockOnCanonicalPath_BlocksLegacyRootWithTrailingSeparator()
    {
        await Ready();
        var before = Assert.Single(await Service.ListAsync());
        await _db.SeedSessionAsync();
        await _db.SeedExecutionAsync("closed", state: "Succeeded", endedAt: DateTimeOffset.UtcNow);
        await _db.InsertProjectLockAsync("unreleased", before.RootPath, "closed");
        await Sql("UPDATE Projects SET RootPath=RootPath || '/'");
        var legacy = Assert.Single(await Service.ListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ChangeAsync(legacy, DataClassification.PublicSource, true));
        Assert.Equal(0, await _db.CountAsync("ProjectDataPolicyChanges"));
    }

    [Fact]
    public async Task AuditFailure_RollsBackChange()
    {
        await Ready();
        var before = Assert.Single(await Service.ListAsync());
        await Sql("CREATE TRIGGER reject_policy_audit BEFORE INSERT ON ProjectDataPolicyChanges BEGIN SELECT RAISE(ABORT,'audit unavailable'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => Service.ChangeAsync(before, DataClassification.PublicSource, true));
        Assert.Equal(before, Assert.Single(await Service.ListAsync()));
    }

    [Fact]
    public async Task FixedClockAndRestoredClass_DoNotReviveOldSnapshot()
    {
        await Ready();
        var original = Assert.Single(await Service.ListAsync());
        await Service.ChangeAsync(original, DataClassification.Restricted, true);
        var second = Assert.Single(await Service.ListAsync());
        await Service.ChangeAsync(second, original.DataClassification, true);
        var third = Assert.Single(await Service.ListAsync());
        Assert.True(DateTimeOffset.Parse(third.Revision) > DateTimeOffset.Parse(second.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ChangeAsync(original, DataClassification.PublicSource, true));
        Assert.Equal(2, await _db.CountAsync("ProjectDataPolicyChanges"));
    }

    [Fact]
    public async Task CancelledChange_IsReadOnly()
    {
        await Ready();
        var before = Assert.Single(await Service.ListAsync());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ChangeAsync(before, DataClassification.PublicSource, true, cancelled.Token));
        Assert.Equal(before, Assert.Single(await Service.ListAsync()));
        Assert.Equal(0, await _db.CountAsync("ProjectDataPolicyChanges"));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
    private sealed class Guard : IApplicationInstanceGuard
    {
        public bool Primary { get; set; } = true;
        public string InstanceId => "owned-test-instance";
        public bool IsPrimarySupervisor => Primary;
        public bool IsViewOnly => !Primary;
        public void EnsureSupervisorPermitted() { if (!Primary) throw new InvalidOperationException("view only"); }
        public void Dispose() { }
    }
}
