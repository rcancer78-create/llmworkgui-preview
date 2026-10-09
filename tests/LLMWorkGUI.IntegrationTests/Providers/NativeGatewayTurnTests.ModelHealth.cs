using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class NativeGatewayTurnTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PendingAuthenticationProjectionBlocksNativeDispatch(bool afterAdmission, bool sharedBackend)
    {
        await Ready();
        var account = (string)(await Sql("SELECT AccountId FROM Routes WHERE Id='" + _request.RouteId + "'"))!;
        Task Enqueue() => new SqliteHealthTransitionStore(_db.Factory).EnqueueAsync(
            [new HealthAuthenticationProjection(Guid.NewGuid().ToString("N"), sharedBackend ? new("OpenCodeBackend", "synthetic") : HealthScope.ForModelRoute(account, "other-model"),
                sharedBackend ? null : account, "Synthetic pending authorization block", DateTimeOffset.UtcNow)
                { ProtectedAccountIds = [account] }], default);
        if (afterAdmission)
        {
            _locks.AfterAcquire = Enqueue;
            var result = await _service!.ExecuteAsync(_request);
            Assert.Equal(ExecutionState.Failed, result.State);
            Assert.False(result.RequiresReconciliation);
            Assert.False(Assert.Single(_tokens).IsHeld);
        }
        else
        {
            await Enqueue();
            Assert.Empty(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _service!.ExecuteAsync(_request));
        }
        Assert.Equal(0, _factoryCalls);
        Assert.Equal(0, _adapter.Calls);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM ExecutionEvents WHERE EventKind='NativeGatewayRunning'"));
    }

    [Fact]
    public async Task TypedModelHealthBlocksCatalogAndAdmissionWithoutBlockingAccount()
    {
        await Ready();
        var account = (string)(await Sql("SELECT AccountId FROM Routes WHERE Id='" + _request.RouteId + "'"))!;
        var model = (string)(await Sql("SELECT ModelId FROM Routes WHERE Id='" + _request.RouteId + "'"))!;
        await _health.DisableManuallyAsync(HealthScope.ForModelRoute(account, model), "Synthetic model block");
        Assert.True((await _health.GetSnapshotAsync(HealthScope.ForAccount(account))).IsRoutable);
        Assert.Empty(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service!.ExecuteAsync(_request));
        Assert.Equal(0, _factoryCalls);
        Assert.Equal(0, _adapter.Calls);
    }

    [Fact]
    public async Task TypedModelHealthCommittedAfterAdmissionBlocksRunningTransition()
    {
        await Ready();
        var account = (string)(await Sql("SELECT AccountId FROM Routes WHERE Id='" + _request.RouteId + "'"))!;
        var model = (string)(await Sql("SELECT ModelId FROM Routes WHERE Id='" + _request.RouteId + "'"))!;
        _locks.AfterAcquire = () => _health.DisableManuallyAsync(HealthScope.ForModelRoute(account, model),
            "Synthetic late model block");
        var result = await _service!.ExecuteAsync(_request);
        Assert.Equal(ExecutionState.Failed, result.State);
        Assert.False(result.RequiresReconciliation);
        Assert.Equal(0, _factoryCalls);
        Assert.False(Assert.Single(_tokens).IsHeld);
        Assert.Equal(0, _adapter.Calls);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM ExecutionEvents WHERE EventKind='NativeGatewayRunning'"));
        Assert.False((await _health.GetSnapshotAsync(HealthScope.ForModelRoute(account, model))).IsRoutable);
    }
}
