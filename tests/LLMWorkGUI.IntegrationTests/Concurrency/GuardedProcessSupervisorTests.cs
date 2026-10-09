using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Concurrency;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class GuardedProcessSupervisorTests
{
    [Fact]
    public async Task ExecuteAsync_WhenInstanceIsPrimary_DelegatesToInnerSupervisor()
    {
        var inner = new RecordingProcessSupervisor();
        var supervisor = new GuardedProcessSupervisor(inner, new StubInstanceGuard(isPrimarySupervisor: true));

        var result = await supervisor.ExecuteAsync(CreateSpecification());

        Assert.Equal(1, inner.InvocationCount);
        Assert.Equal("execution-1", result.ExecutionId);
        Assert.Same(inner, supervisor.Inner);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceIsViewOnly_IsBlockedBeforeStartingProcess()
    {
        var inner = new RecordingProcessSupervisor();
        var supervisor = new GuardedProcessSupervisor(inner, new StubInstanceGuard(isPrimarySupervisor: false));

        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(
            () => supervisor.ExecuteAsync(CreateSpecification()));

        Assert.Equal(0, inner.InvocationCount);
    }

    [Fact]
    public async Task StartProtocolProcessAsync_WhenInstanceIsViewOnly_IsBlockedBeforeStartingProcess()
    {
        var inner = new RecordingProcessSupervisor();
        var supervisor = new GuardedProcessSupervisor(inner, new StubInstanceGuard(isPrimarySupervisor: false));

        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(
            () => supervisor.StartProtocolProcessAsync(CreateProtocolSpecification()));

        Assert.Equal(0, inner.ProtocolInvocationCount);
    }

    [Fact]
    public async Task StartProtocolProcessAsync_WhenInstanceIsPrimary_DelegatesToInnerSupervisor()
    {
        var inner = new RecordingProcessSupervisor();
        var supervisor = new GuardedProcessSupervisor(inner, new StubInstanceGuard(isPrimarySupervisor: true));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => supervisor.StartProtocolProcessAsync(CreateProtocolSpecification()));

        // The guard permitted the launch and the call reached the inner supervisor, which does not
        // own real OS processes and therefore refuses the duplex capability explicitly.
        Assert.Equal(1, inner.ProtocolInvocationCount);
    }

    private static ProcessStartSpecification CreateProtocolSpecification()
    {
        return new ProcessStartSpecification
        {
            ExecutionId = "execution-protocol",
            FileName = "cmd.exe",
            StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        };
    }

    private static ProcessStartSpecification CreateSpecification()
    {
        return new ProcessStartSpecification
        {
            ExecutionId = "execution-1",
            FileName = "cmd.exe"
        };
    }

    private sealed class StubInstanceGuard : IApplicationInstanceGuard
    {
        public StubInstanceGuard(bool isPrimarySupervisor)
        {
            IsPrimarySupervisor = isPrimarySupervisor;
        }

        public string InstanceId { get; } = Guid.NewGuid().ToString("D");

        public bool IsPrimarySupervisor { get; }

        public bool IsViewOnly => !IsPrimarySupervisor;

        public void EnsureSupervisorPermitted()
        {
            if (IsViewOnly)
            {
                throw new SecondaryInstanceReadOnlyException("This instance runs in View-Only mode.");
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingProcessSupervisor : IProcessSupervisor
    {
        public int InvocationCount { get; private set; }

        public int ProtocolInvocationCount { get; private set; }

        public Task<IProtocolProcessSession> StartProtocolProcessAsync(
            ProcessStartSpecification specification,
            CancellationToken cancellationToken = default)
        {
            ProtocolInvocationCount++;

            throw new NotSupportedException(
                "The recording supervisor does not own real OS processes and cannot provide a duplex channel.");
        }

        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;

            var timestamp = DateTimeOffset.UtcNow;

            return Task.FromResult(new ProcessExecutionResult
            {
                ExecutionId = specification.ExecutionId,
                TerminationReason = ProcessTerminationReason.None,
                StartedAtUtc = timestamp,
                ExitedAtUtc = timestamp,
                RunDirectory = "run",
                StandardOutputLogPath = "stdout.log",
                StandardErrorLogPath = "stderr.log",
                StandardOutputBytes = 0,
                StandardErrorBytes = 0,
                StandardOutputHead = string.Empty,
                StandardOutputTail = string.Empty,
                StandardErrorHead = string.Empty,
                StandardErrorTail = string.Empty,
                OutputOverflowed = false
            });
        }
    }
}
