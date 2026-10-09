using System.Diagnostics;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Reconciliation;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Reconciliation;

public sealed partial class ReconciliationProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProbeAsync_WhenBackendExecutableIsMissing_ReportsBackendMissing()
    {
        var probe = new ReconciliationProbe(new FakeCliExecutableLocator(null));

        var result = await probe.ProbeAsync(CreateRequest($"pid:{Environment.ProcessId}"));

        Assert.False(result.BackendAvailable);
        Assert.False(result.ProcessAlive);
        Assert.Null(result.ObservedNativeSessionId);
        Assert.Null(result.ObservedBinding);
    }

    [Fact]
    public async Task ProbeAsync_WhenPersistedProcessIsAlive_ReportsLivenessWithoutNativeIdentity()
    {
        var process = Process.GetCurrentProcess();
        var probe = new ReconciliationProbe(new FakeCliExecutableLocator(@"C:\tools\opencode.exe"));

        var result = await probe.ProbeAsync(CreateRequest(
            $"pid:{process.Id};name:{process.ProcessName}"));

        Assert.True(result.BackendAvailable);
        Assert.True(result.ProcessAlive);
        Assert.Null(result.ObservedNativeSessionId);
        Assert.Null(result.ObservedBinding);
    }

    [Fact]
    public async Task ProbeAsync_WhenProcessStateHasNoProcessId_ReportsProcessNotAlive()
    {
        var probe = new ReconciliationProbe(new FakeCliExecutableLocator(@"C:\tools\opencode.exe"));

        var result = await probe.ProbeAsync(CreateRequest("running"));

        Assert.True(result.BackendAvailable);
        Assert.False(result.ProcessAlive);
    }

    [Fact]
    public async Task ProbeAsync_WhenPersistedProcessIsNotRunning_ReportsProcessNotAlive()
    {
        var probe = new ReconciliationProbe(new FakeCliExecutableLocator(@"C:\tools\opencode.exe"));

        var result = await probe.ProbeAsync(CreateRequest($"pid:{int.MaxValue}"));

        Assert.True(result.BackendAvailable);
        Assert.False(result.ProcessAlive);
    }

    [Fact]
    public async Task ProbeAsync_WhenPersistedProcessNameDiffers_ReportsProcessNotAlive()
    {
        var probe = new ReconciliationProbe(new FakeCliExecutableLocator(@"C:\tools\opencode.exe"));

        var result = await probe.ProbeAsync(CreateRequest(
            $"pid:{Environment.ProcessId};name:opencode"));

        Assert.True(result.BackendAvailable);
        Assert.False(result.ProcessAlive);
    }

    private static ReconciliationProbeRequest CreateRequest(string? processState)
    {
        var binding = CreateBinding();

        var session = new Session(
            "session-1",
            binding,
            "project-1",
            @"C:\workspace",
            "native-1",
            SessionState.Active,
            LLMWorkGUI.Domain.Enums.ReconciliationOutcome.None,
            CloseReason.None,
            continuationOfSessionId: null,
            forkedFromSessionId: null,
            workflowRunId: null,
            role: null,
            activeExecutionId: "execution-1",
            createdAt: Now,
            lastEventAt: Now);

        var execution = new Execution(
            "execution-1",
            "session-1",
            "client-request-1",
            ExecutionState.Running,
            ExecutionFailureReason.None,
            "route-1",
            observedRouteId: null,
            retryOfExecutionId: null,
            processState,
            exitCode: null,
            terminationReason: null,
            artifacts: Array.Empty<string>(),
            sourceHashBefore: null,
            sourceHashAfter: null,
            createdAt: Now,
            startedAt: Now,
            endedAt: null);

        return new ReconciliationProbeRequest
        {
            Session = session,
            ActiveExecution = execution
        };
    }

    private static SessionBinding CreateBinding()
    {
        return new SessionBinding(
            BackendType.OpenCode,
            "provider-1",
            "account-1",
            "model-1",
            reasoningEffort: null,
            speedMode: null,
            executionMode: null);
    }

    private sealed class FakeCliExecutableLocator : ICliExecutableLocator
    {
        private readonly string? _executablePath;

        public FakeCliExecutableLocator(string? executablePath)
        {
            _executablePath = executablePath;
        }

        public Task<string?> LocateAsync(
            string executableName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_executablePath);
        }
    }
}
