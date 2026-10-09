using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed class HealthCorrectiveD000Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountMismatchCannotChangeNewerHealthOrRetireNewerAttempt(bool admitNewAttempt)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await ConfigureEndpoint(database);
        using var provider = CreateProvider(database);
        var health = provider.GetRequiredService<IHealthCenterService>();
        var account = HealthScope.ForAccount("account-1");
        var route = HealthScope.ForModelRoute("account-1", "model-1");
        await health.DisableManuallyAsync(account, "initial preparation");
        await health.EnableAsync(account, "prepare account probe");
        var executor = new BarrierMismatchExecutor();
        var probes = new HealthProbeService(health, provider.GetRequiredService<IProviderProfileRepository>(),
            provider.GetRequiredService<IAccountRepository>(), modelProbe: executor);
        var pending = probes.ProbeModelAsync(account,
            HealthProbeConfirmation.ForModel("model-1", costPreviewAcknowledged: true));
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        HealthProbeAttempt? newerAttempt = null;
        try
        {
            await health.DisableManuallyAsync(account, "newer account decision");
            await health.DisableManuallyAsync(route, "newer concrete route decision");
            await health.EnableAsync(route, "route requires its own new probe");
            if (admitNewAttempt)
            {
                await health.EnableAsync(account, "admit newer account probe");
                newerAttempt = await health.TryBeginProbeAttemptAsync(account, true, "new owned probe");
                Assert.NotNull(newerAttempt);
            }
            var expectedAccount = await health.GetSnapshotAsync(account);
            var expectedRoute = await health.GetSnapshotAsync(route);
            var routeAuditBefore = await health.GetAuditAsync(route);
            executor.Release.TrySetResult();
            var outcome = await pending;

            Assert.Equal(expectedAccount, await health.GetSnapshotAsync(account));
            Assert.Equal(expectedRoute, await health.GetSnapshotAsync(route));
            Assert.Equal(routeAuditBefore.Count + 1, (await health.GetAuditAsync(route)).Count);
            Assert.False(outcome.ConfirmsVerifiedRecovery);
            Assert.Equal(1, executor.Calls);
            Assert.Equal(0, await probes.RetryPendingCompletionsAsync());
            Assert.Equal(1, executor.Calls);
            if (newerAttempt is not null)
            {
                var completed = await health.CompleteProbeAttemptAsync(newerAttempt,
                    HealthProbeCompletionKind.ModelSucceeded, "new owned result");
                Assert.True(completed.WasCurrent);
                Assert.True(completed.VerifiedRecovery);
            }
        }
        finally
        {
            executor.Release.TrySetResult();
            await pending;
        }
    }

    [Fact]
    public async Task CurrentAccountMismatchStillBlocksOnlyItsConcreteRoute()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await ConfigureEndpoint(database);
        using var provider = CreateProvider(database);
        var health = provider.GetRequiredService<IHealthCenterService>();
        var account = HealthScope.ForAccount("account-1");
        await health.DisableManuallyAsync(account, "initial preparation");
        await health.EnableAsync(account, "prepare account probe");
        var executor = new BarrierMismatchExecutor();
        executor.Release.TrySetResult();
        var probes = new HealthProbeService(health, provider.GetRequiredService<IProviderProfileRepository>(),
            provider.GetRequiredService<IAccountRepository>(), modelProbe: executor);
        var outcome = await probes.ProbeModelAsync(account,
            HealthProbeConfirmation.ForModel("model-1", costPreviewAcknowledged: true));
        Assert.Equal(HealthState.Recovering, outcome.Snapshot.State);
        Assert.Equal(HealthState.CoolingDown,
            (await health.GetSnapshotAsync(HealthScope.ForModelRoute("account-1", "model-1"))).State);
        Assert.False(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(1, executor.Calls);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    public async Task AuthenticationCommittedProjectionRepairsAcknowledgementAndAnnouncesOnce(string fault)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        using var provider = CreateProvider(database);
        var wrapper = new AcknowledgementFaultStore(provider.GetRequiredService<IHealthTransitionStore>(), fault);
        var health = new HealthCenterService(provider.GetRequiredService<IHealthStateRepository>(),
            provider.GetRequiredService<IHealthEventRepository>(), transitionStore: wrapper);
        var scope = HealthScope.ForAccount("observed-auth-account");
        var notifications = 0;
        health.TransitionRecorded += (_, _) => notifications++;

        var outcomes = await health.ReportAuthenticationFailuresAsync([scope], "independent observed 401");

        Assert.Equal(scope, Assert.Single(outcomes).Snapshot.Scope);
        Assert.Empty(await wrapper.ListPendingAsync(default));
        Assert.Equal(1, (await health.GetSnapshotAsync(scope)).AccountedFailureCount);
        Assert.Equal(Assert.Single(wrapper.AdmittedIds), Assert.Single(await health.GetAuditAsync(scope)).Id);
        Assert.Equal(1, notifications);
        Assert.Equal(0, await health.RetryAuthenticationFanoutAsync());
        Assert.Equal(1, notifications);

        // Matching text is another actual observation, not a replay identity. It must not be deduplicated.
        await health.ReportAuthenticationFailuresAsync([scope], "independent observed 401");
        Assert.Equal(2, (await health.GetSnapshotAsync(scope)).AccountedFailureCount);
        Assert.Equal(2, (await health.GetAuditAsync(scope)).Count);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task AuthenticationPrecommitFailureRetainsBarrierUntilExactProjectionRetry()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        using var provider = CreateProvider(database);
        var wrapper = new AcknowledgementFaultStore(provider.GetRequiredService<IHealthTransitionStore>(), "before");
        var health = new HealthCenterService(provider.GetRequiredService<IHealthStateRepository>(),
            provider.GetRequiredService<IHealthEventRepository>(), transitionStore: wrapper);
        var scope = HealthScope.ForAccount("precommit-auth-account");
        var notifications = 0;
        health.TransitionRecorded += (_, _) => notifications++;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            health.ReportAuthenticationFailuresAsync([scope], "observed 401 before failed commit"));
        Assert.Single(await wrapper.ListPendingAsync(default));
        Assert.Empty(await health.GetAuditAsync(scope));
        Assert.False((await health.GetSnapshotAsync(scope)).IsRoutable);
        Assert.Equal(0, notifications);
        Assert.Equal(0, await health.RetryAuthenticationFanoutAsync());
        Assert.Equal(Assert.Single(wrapper.AdmittedIds), Assert.Single(await health.GetAuditAsync(scope)).Id);
        Assert.Equal(1, (await health.GetSnapshotAsync(scope)).AccountedFailureCount);
        Assert.Equal(1, notifications);
    }

    private static ServiceProvider CreateProvider(TestDatabase database)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        return services.BuildServiceProvider();
    }

    private static async Task ConfigureEndpoint(TestDatabase database)
    {
        await using var connection = await database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ProviderProfiles SET BaseUrl='https://synthetic.invalid';";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class BarrierMismatchExecutor : IModelProbeExecutor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool CanExecute(ModelProbeRequest request) => true;
        public async Task<ModelProbeResult> ExecuteAsync(ModelProbeRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return ModelProbeResult.Failed(HealthErrorClass.ModelUnavailableOrMismatch,
                "https://synthetic.invalid", 1, "synthetic mismatch observation");
        }
    }

    private sealed class AcknowledgementFaultStore(IHealthTransitionStore durable, string fault)
        : IHealthTransitionStore, IHealthAuthenticationFanoutStore
    {
        private readonly IHealthAuthenticationFanoutStore _fanout = (IHealthAuthenticationFanoutStore)durable;
        private bool _armed = true;
        public List<string> AdmittedIds { get; } = new();
        public Task SaveAsync(HealthStateRecord? state, HealthEventRecord? healthEvent, CancellationToken token = default)
            => durable.SaveAsync(state, healthEvent, token);
        public async Task EnqueueAsync(IReadOnlyList<HealthAuthenticationProjection> projections, CancellationToken token)
        {
            await _fanout.EnqueueAsync(projections, token);
            AdmittedIds.AddRange(projections.Select(projection => projection.Id));
        }
        public Task<IReadOnlyList<HealthAuthenticationProjection>> ListPendingAsync(CancellationToken token)
            => _fanout.ListPendingAsync(token);
        public Task<bool> HasPendingAsync(HealthScope scope, CancellationToken token) => _fanout.HasPendingAsync(scope, token);
        public Task<(HealthStateRecord? State, bool Pending)> ReadSnapshotAsync(HealthScope scope, CancellationToken token)
            => _fanout.ReadSnapshotAsync(scope, token);
        public async Task SaveAndCompleteAsync(HealthStateRecord state, HealthEventRecord healthEvent,
            string id, CancellationToken token)
        {
            var inject = _armed;
            _armed = false;
            if (inject && fault == "before") throw new IOException("synthetic precommit persistence refusal");
            await _fanout.SaveAndCompleteAsync(state, healthEvent, id, token);
            if (!inject) return;
            if (fault == "cancel") throw new OperationCanceledException("synthetic postcommit cancellation",
                new CancellationToken(canceled: true));
            throw new IOException("synthetic postcommit acknowledgement loss");
        }
    }
}
