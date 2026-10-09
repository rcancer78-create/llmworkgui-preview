using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

/// <summary>
/// Health reporting of the Cursor ACP lifecycle (ТЗ §6.10): every start and turn outcome is reported to
/// the Health Center under the <c>cursor-agent-acp</c> backend scope through
/// <see cref="IHealthCenterService"/>, never by writing breaker state directly.
/// </summary>
public sealed class CursorAcpHealthReportingTests
{
    private static readonly HealthScope ExpectedScope = HealthScope.ForBackend("cursor-agent-acp");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedStartReportsOnlyAmbiguousHealthWhenCleanupFails(bool withTransport)
    {
        var health = new RecordingHealthCenterService();
        var manager = FakeCursorAcpProcessManager.Started(
            new FakeCursorAcpProcessSession(withTransport ? FakeJsonRpcTransport.Unused() : null));
        var client = new FakeCursorAcpClient
        {
            InitializeHandler = _ => Task.FromResult(CursorAcpHandshakeResult.Degraded(
                CursorAcpHandshakeFailureKind.UnsupportedVersion, "Unsupported version"))
        };
        manager.StopHandler = (_, _) =>
        {
            Assert.Empty(health.Failures);
            throw new IOException("Cleanup unconfirmed");
        };
        await using var service = CreateService(manager, client, health);
        try
        {
            var result = await service.StartBackendAsync("refused-start-pending");
            Assert.True(result.RequiresReconciliation);
            Assert.Equal(HealthErrorClass.UnknownOrAmbiguousCompletion, Assert.Single(health.Failures).ErrorClass);
            Assert.Empty(health.Successes);
        }
        finally { manager.StopHandler = null; await service.StopBackendAsync(); }
    }

    [Fact]
    public async Task PendingNativeOwnershipPreservesAmbiguousHealthAndBackendState()
    {
        var health = new RecordingHealthCenterService();
        var manager = FakeCursorAcpProcessManager.Degraded(CursorAcpProcessStartFailureKind.ProcessCleanupPending,
            "Native process ownership is retained.");
        await using var service = CreateService(manager, CreateReadyClient(), health);
        var result = await service.StartBackendAsync("pending-native-health");
        Assert.False(result.IsReady);
        Assert.True(result.RequiresReconciliation);
        Assert.Equal(CursorAcpBackendFailureKind.ProcessCleanupPending, result.FailureKind);
        Assert.Equal(HealthErrorClass.UnknownOrAmbiguousCompletion, Assert.Single(health.Failures).ErrorClass);
        Assert.Empty(health.Successes);
    }

    [Fact]
    public async Task StartBackendAsync_MissingExecutable_ReportsExecutableMissing()
    {
        var health = new RecordingHealthCenterService();
        var processManager = FakeCursorAcpProcessManager.Degraded(
            CursorAcpProcessStartFailureKind.ExecutableMissing,
            "cursor-agent is not installed.");

        await using var service = CreateService(processManager, CreateReadyClient(), health);

        var result = await service.StartBackendAsync("exec-health-missing");

        Assert.False(result.IsReady);

        var failure = Assert.Single(health.Failures);
        Assert.Equal(ExpectedScope, failure.Scope);
        Assert.Equal(HealthErrorClass.ExecutableMissingOrVersion, failure.ErrorClass);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
        Assert.Empty(health.Successes);
    }

    [Fact]
    public async Task StartBackendAsync_LaunchFailure_ReportsStartupOrSessionCreation()
    {
        var health = new RecordingHealthCenterService();
        var processManager = FakeCursorAcpProcessManager.Degraded(
            CursorAcpProcessStartFailureKind.LaunchFailed,
            "The process supervisor rejected the launch.");

        await using var service = CreateService(processManager, CreateReadyClient(), health);

        var result = await service.StartBackendAsync("exec-health-launch");

        Assert.False(result.IsReady);

        var failure = Assert.Single(health.Failures);
        Assert.Equal(ExpectedScope, failure.Scope);
        Assert.Equal(HealthErrorClass.StartupOrSessionCreation, failure.ErrorClass);
    }

    [Fact]
    public async Task StartBackendAsync_WithoutBoundTransport_ReportsStartupOrSessionCreation()
    {
        var health = new RecordingHealthCenterService();
        var processManager = FakeCursorAcpProcessManager.Started(
            new FakeCursorAcpProcessSession(transport: null));

        await using var service = CreateService(processManager, CreateReadyClient(), health);

        var result = await service.StartBackendAsync("exec-health-no-transport");

        Assert.Equal(CursorAcpBackendFailureKind.TransportUnavailable, result.FailureKind);

        var failure = Assert.Single(health.Failures);
        Assert.Equal(ExpectedScope, failure.Scope);
        Assert.Equal(HealthErrorClass.StartupOrSessionCreation, failure.ErrorClass);
    }

    [Fact]
    public async Task StartBackendAsync_FailedHandshake_ReportsStartupOrSessionCreation()
    {
        var health = new RecordingHealthCenterService();
        var client = new FakeCursorAcpClient
        {
            InitializeHandler = _ => Task.FromResult(CursorAcpHandshakeResult.Degraded(
                CursorAcpHandshakeFailureKind.UnsupportedVersion,
                "The agent reported protocolVersion 2 instead of the required JSON number 1."))
        };

        var processManager = FakeCursorAcpProcessManager.Started(
            new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused()));

        await using var service = CreateService(processManager, client, health);

        var result = await service.StartBackendAsync("exec-health-handshake");

        Assert.Equal(CursorAcpBackendFailureKind.HandshakeFailed, result.FailureKind);

        var failure = Assert.Single(health.Failures);
        Assert.Equal(HealthErrorClass.StartupOrSessionCreation, failure.ErrorClass);
        Assert.Contains("protocolVersion 2", failure.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartBackendAsync_ReadyHandshake_ReportsSuccess()
    {
        var health = new RecordingHealthCenterService();
        var processManager = FakeCursorAcpProcessManager.Started(
            new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused()));

        await using var service = CreateService(processManager, CreateReadyClient(), health);

        var result = await service.StartBackendAsync("exec-health-ready");

        Assert.True(result.IsReady, result.Blocker);

        var success = Assert.Single(health.Successes);
        Assert.Equal(ExpectedScope, success.Scope);
        Assert.Empty(health.Failures);
    }

    [Fact]
    public async Task ExecuteTurnAsync_CompletedTurn_ReportsSuccess()
    {
        var health = new RecordingHealthCenterService();
        var client = CreateReadyClient();

        await using var service = await StartedServiceAsync(client, health);

        var result = await service.ExecuteTurnAsync(CreateTurnRequest());

        Assert.Equal(CursorAcpTurnOutcome.Succeeded, result.Outcome);

        // The start also reported a success, so the last report is the one that belongs to the turn.
        var success = health.Successes[^1];
        Assert.Equal(ExpectedScope, success.Scope);
        Assert.Contains("turn completed", success.Reason!, StringComparison.Ordinal);
        Assert.Empty(health.Failures);
    }

    [Fact]
    public async Task ExecuteTurnAsync_FailedTurn_ReportsProviderFailure()
    {
        var health = new RecordingHealthCenterService();
        var client = CreateReadyClient();
        client.PromptHandler = (_, _) => Task.FromResult(CursorAcpPromptResult.Degraded(
            CursorAcpPromptFailureKind.NotReady,
            "The handshake is not ready."));

        await using var service = await StartedServiceAsync(client, health);

        var result = await service.ExecuteTurnAsync(CreateTurnRequest());

        Assert.Equal(CursorAcpTurnOutcome.Failed, result.Outcome);

        var failure = Assert.Single(health.Failures);
        Assert.Equal(ExpectedScope, failure.Scope);
        Assert.Equal(HealthErrorClass.Provider4xx5xx, failure.ErrorClass);
    }

    [Fact]
    public async Task ExecuteTurnAsync_HardTimeout_ReportsNetworkTimeout()
    {
        var health = new RecordingHealthCenterService();
        var client = CreateReadyClient();
        var neverCompletes = new TaskCompletionSource<CursorAcpPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.PromptHandler = (_, _) => neverCompletes.Task;

        await using var service = CreateService(
            FakeCursorAcpProcessManager.Started(new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused())),
            client,
            health,
            options => options.TurnHardTimeout = TimeSpan.FromMilliseconds(300));

        var started = await service.StartBackendAsync("exec-health-timeout");
        Assert.True(started.IsReady, started.Blocker);

        var result = await service.ExecuteTurnAsync(CreateTurnRequest());

        // No delivery evidence was observed, so the hard timeout is classified as Ambiguous with a
        // timeout reason; either way the observed cause is a timeout and must be reported as one.
        Assert.Equal(CursorAcpTurnFailureKind.TurnTimeout, result.FailureKind);

        var failure = Assert.Single(health.Failures);
        Assert.Equal(ExpectedScope, failure.Scope);
        Assert.Equal(HealthErrorClass.NetworkOrTimeout, failure.ErrorClass);
    }

    [Fact]
    public async Task WithoutAHealthCenter_TheLifecycleWorksWithoutReporting()
    {
        var client = CreateReadyClient();
        var processManager = FakeCursorAcpProcessManager.Started(
            new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused()));

        await using var service = CreateService(processManager, client, healthCenter: null);

        var started = await service.StartBackendAsync("exec-health-none");
        Assert.True(started.IsReady, started.Blocker);

        var result = await service.ExecuteTurnAsync(CreateTurnRequest());
        Assert.Equal(CursorAcpTurnOutcome.Succeeded, result.Outcome);

        // No health service was configured, so the turn must simply complete without errors.
        Assert.Single(client.PromptRequests);
    }

    private static async Task<CursorAcpSessionLifecycleService> StartedServiceAsync(
        FakeCursorAcpClient client,
        IHealthCenterService healthCenter)
    {
        var service = CreateService(
            FakeCursorAcpProcessManager.Started(new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused())),
            client,
            healthCenter);

        var started = await service.StartBackendAsync("exec-health-turns");
        Assert.True(started.IsReady, started.Blocker);

        return service;
    }

    private static CursorAcpSessionLifecycleService CreateService(
        ICursorAcpProcessManager processManager,
        ICursorAcpClient client,
        IHealthCenterService? healthCenter,
        Action<CursorAcpOptions>? configure = null)
    {
        var options = new CursorAcpOptions
        {
            TurnHardTimeout = TimeSpan.FromSeconds(30),
            CancellationTimeout = TimeSpan.FromSeconds(30)
        };

        configure?.Invoke(options);

        return new CursorAcpSessionLifecycleService(
            processManager,
            new StubCursorAcpClientFactory(client),
            new CursorAcpModePolicy(),
            Options.Create(options),
            loggerFactory: null,
            healthCenter);
    }

    private static FakeCursorAcpClient CreateReadyClient() =>
        CursorAcpSessionLifecycleStartTests.CreateReadyClient();

    private static CursorAcpTurnRequest CreateTurnRequest() =>
        new()
        {
            Prompt = CursorAcpPromptRequest.Create("cursor-session-health", "Explain the failing test."),
            Mode = new CursorAcpModeDecision
            {
                Mode = CursorAcpMode.Ask,
                ModeId = "ask",
                Access = CursorAcpModeAccess.ReadOnly,
                State = CapabilityState.Supported,
                CanSend = true,
                RequiresWriterLock = false
            }
        };
}

/// <summary>
/// Records every health report the lifecycle service makes. It proves the reporting goes through the
/// normalized service contract under the expected scope instead of touching a repository directly.
/// </summary>
internal sealed class RecordingHealthCenterService : IHealthCenterService
{
    private readonly List<HealthFailureReport> _failures = new();
    private readonly List<HealthSuccessReport> _successes = new();

    public event EventHandler<HealthTransitionEventArgs>? TransitionRecorded
    {
        add { }
        remove { }
    }

    public IReadOnlyList<HealthFailureReport> Failures
    {
        get
        {
            lock (_failures)
            {
                return _failures.ToArray();
            }
        }
    }

    public IReadOnlyList<HealthSuccessReport> Successes
    {
        get
        {
            lock (_successes)
            {
                return _successes.ToArray();
            }
        }
    }

    public Task<HealthFailureOutcome> ReportFailureAsync(
        HealthScope scope,
        HealthErrorClass errorClass,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        lock (_failures)
        {
            _failures.Add(new HealthFailureReport(scope, errorClass, reason));
        }

        return Task.FromResult(new HealthFailureOutcome
        {
            Snapshot = CreateSnapshot(scope),
            CountedByBreaker = true,
            StateChanged = true,
            AutomaticRetryAllowed = false
        });
    }

    public Task<HealthSnapshot> ReportSuccessAsync(
        HealthScope scope,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        lock (_successes)
        {
            _successes.Add(new HealthSuccessReport(scope, reason));
        }

        return Task.FromResult(CreateSnapshot(scope));
    }

    public Task<HealthSnapshot> GetSnapshotAsync(
        HealthScope scope,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateSnapshot(scope));

    public Task<IReadOnlyList<HealthSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HealthSnapshot>>(Array.Empty<HealthSnapshot>());

    public Task<HealthSnapshot> ExpireCooldownAsync(
        HealthScope scope,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateSnapshot(scope));

    public Task<HealthSnapshot> StartProbeAsync(
        HealthScope scope,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateSnapshot(scope));

    public Task<HealthSnapshot> CompleteProbeAsync(
        HealthScope scope,
        bool succeeded,
        string? evidenceRedactedJson = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateSnapshot(scope));

    public Task<HealthSnapshot> RecordProbeObservationAsync(
        HealthScope scope,
        bool observedSuccess,
        string reason,
        string? evidenceRedactedJson = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateSnapshot(scope));

    public Task<HealthSnapshot> DisableManuallyAsync(
        HealthScope scope,
        string reason,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateSnapshot(scope));

    public Task<HealthSnapshot> EnableAsync(
        HealthScope scope,
        string reason,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateSnapshot(scope));

    public Task<HealthSnapshot> ForceEnableAsync(
        HealthScope scope,
        string reason,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateSnapshot(scope));

    public Task<HealthSnapshot> RequireProbeAsync(
        HealthScope scope,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateSnapshot(scope));

    public Task<IReadOnlyList<HealthEventView>> GetAuditAsync(
        HealthScope scope,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HealthEventView>>(Array.Empty<HealthEventView>());

    private static HealthSnapshot CreateSnapshot(HealthScope scope) => new()
    {
        Scope = scope,
        State = HealthState.Healthy
    };
}

internal sealed record HealthFailureReport(HealthScope Scope, HealthErrorClass ErrorClass, string? Reason);

internal sealed record HealthSuccessReport(HealthScope Scope, string? Reason);
