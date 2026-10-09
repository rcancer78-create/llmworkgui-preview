using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

/// <summary>
/// Guards of the duplex protocol-process launch contract (ТЗ §4.2, §6.8). A protocol process must
/// declare <see cref="ProcessStdinPolicy.DirectProtocolTransport"/>, its standard output must never
/// be spooled, and its standard error must still be captured by the supervisor.
/// </summary>
public sealed class ProtocolProcessLaunchTests
{
    [Fact]
    public async Task RegularProcessGenerationIsIssuedAtRealStartupAndSharesProtocolSequence()
    {
        using var data = new TestDirectory();
        using var checkout = new TestDirectory();
        var supervisor = CreateSupervisor(data.Root);
        long regular = 0;
        var result = await supervisor.ExecuteAsync(new ProcessStartSpecification
        {
            ExecutionId = "regular-generation", FileName = "cmd.exe", Arguments = ["/d", "/c", "exit", "0"],
            WorkingDirectory = checkout.Root, ProcessStarted = generation => regular = generation
        });
        Assert.Equal(0, result.ExitCode);
        Assert.True(regular > 0);
        await using var protocol = await supervisor.StartProtocolProcessAsync(new ProcessStartSpecification
        {
            ExecutionId = "protocol-after-regular", FileName = "cmd.exe", Arguments = ["/d", "/c", "more"],
            WorkingDirectory = checkout.Root, StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        });
        Assert.True(protocol.ProcessGeneration > regular);
    }

    [Fact]
    public async Task FailedStartupDoesNotIssueProcessGeneration()
    {
        using var data = new TestDirectory();
        using var checkout = new TestDirectory();
        var supervisor = CreateSupervisor(data.Root);
        var calls = 0;
        var result = await supervisor.ExecuteAsync(new ProcessStartSpecification
        {
            ExecutionId = "generation-no-start", FileName = Path.Combine(data.Root, "does-not-exist.exe"),
            WorkingDirectory = checkout.Root, ProcessStarted = _ => calls++
        });
        Assert.Null(result.ProcessId);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ProcessGeneration_IsDistinctAcrossSupervisorInstances()
    {
        using var data = new TestDirectory();
        using var checkout = new TestDirectory();
        var firstSupervisor = CreateSupervisor(data.Root);
        var secondSupervisor = CreateSupervisor(data.Root);
        ProcessStartSpecification Specification(string id) => new()
        {
            ExecutionId = id, FileName = "cmd.exe", Arguments = ["/d", "/c", "more"],
            WorkingDirectory = checkout.Root, StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        };
        await using var first = await firstSupervisor.StartProtocolProcessAsync(Specification("generation-first"));
        await using var second = await secondSupervisor.StartProtocolProcessAsync(Specification("generation-second"));

        Assert.True(first.IsRunning);
        Assert.True(second.IsRunning);
        Assert.True(first.ProcessGeneration > 0);
        Assert.True(second.ProcessGeneration > first.ProcessGeneration);
    }

    [Fact]
    public async Task LiveProtocolOwnershipRejectsDuplicateUntilDisposalCompletes()
    {
        using var data = new TestDirectory();
        using var checkout = new TestDirectory();
        var supervisor = CreateSupervisor(data.Root);
        var specification = new ProcessStartSpecification
        {
            ExecutionId = "protocol-duplicate", FileName = "cmd.exe",
            Arguments = ["/d", "/c", "more"], WorkingDirectory = checkout.Root,
            StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        };
        var session = await supervisor.StartProtocolProcessAsync(specification);
        try
        {
            Assert.True(session.ProcessGeneration > 0);
            var duplicate = await Assert.ThrowsAsync<ProcessStartupPendingException>(() => supervisor.StartProtocolProcessAsync(specification));
            Assert.Equal(ProcessTerminationReason.CleanupPending, duplicate.Reason);
            Assert.False(duplicate.CleanupCompletion.IsCompleted);
            // A regular launch must use its own stdin policy; execution ownership still rejects it
            // while the protocol session owns this ID, before another native process can start.
            Assert.Equal(ProcessTerminationReason.CleanupPending, (await supervisor.ExecuteAsync(
                new ProcessStartSpecification
                {
                    ExecutionId = specification.ExecutionId, FileName = specification.FileName,
                    Arguments = specification.Arguments, WorkingDirectory = specification.WorkingDirectory,
                    StdinPolicy = ProcessStdinPolicy.Closed
                })).TerminationReason);
            await session.DisposeAsync();
            await duplicate.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(session.IsRunning);
            await using var replacement = await supervisor.StartProtocolProcessAsync(specification);
            // Windows may reuse a retired PID; the generation must distinguish the launch.
            Assert.True(replacement.ProcessId > 0);
            Assert.True(replacement.ProcessGeneration > session.ProcessGeneration);
        }
        finally { await session.DisposeAsync(); }
    }

    [Theory]
    [InlineData(ProcessStdinPolicy.Closed)]
    [InlineData(ProcessStdinPolicy.Null)]
    public async Task StartProtocolProcessAsync_NonProtocolStdinPolicy_IsRejected(ProcessStdinPolicy policy)
    {
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            supervisor.StartProtocolProcessAsync(new ProcessStartSpecification
            {
                ExecutionId = "exec-protocol-policy",
                FileName = "cmd.exe",
                Arguments = new[] { "/d", "/c", "exit", "0" },
                WorkingDirectory = dataDirectory.Root,
                StdinPolicy = policy
            }));

        Assert.Contains("DirectProtocolTransport", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartProtocolProcessAsync_LiveProcess_HandsOverDuplexStreams()
    {
        using var dataDirectory = new TestDirectory();
        using var checkoutDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        await using var session = await supervisor.StartProtocolProcessAsync(new ProcessStartSpecification
        {
            ExecutionId = "exec-protocol-duplex",
            FileName = "cmd.exe",
            // 'more' echoes standard input back to standard output, proving a real duplex channel.
            Arguments = new[] { "/d", "/c", "more" },
            WorkingDirectory = checkoutDirectory.Root,
            StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        });

        Assert.True(session.ProcessId > 0);
        Assert.True(session.StandardInput.CanWrite);
        Assert.True(session.StandardOutput.CanRead);

        var writer = new StreamWriter(session.StandardInput) { AutoFlush = true };
        await writer.WriteLineAsync("protocol-frame");
        session.StandardInput.Close();

        var reader = new StreamReader(session.StandardOutput);
        var echoed = await reader.ReadToEndAsync();

        Assert.Contains("protocol-frame", echoed, StringComparison.Ordinal);

        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        // Standard output is owned by the transport, so the supervisor reports no spooled bytes and
        // no standard-output log path for a protocol process.
        Assert.Equal(0, result.StandardOutputBytes);
        Assert.Equal(string.Empty, result.StandardOutputLogPath);
        Assert.False(File.Exists(Path.Combine(
            session.RunDirectory,
            ProcessSupervisorOptions.StandardOutputFileName)));

        // Standard error is still spooled with a bounded writer.
        Assert.Equal(session.StandardErrorLogPath, result.StandardErrorLogPath);
        Assert.True(File.Exists(session.StandardErrorLogPath));
    }

    [Fact]
    public async Task StartProtocolProcessAsync_StopAsync_IsIdempotentAndReportsTermination()
    {
        using var dataDirectory = new TestDirectory();
        using var checkoutDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        await using var session = await supervisor.StartProtocolProcessAsync(new ProcessStartSpecification
        {
            ExecutionId = "exec-protocol-stop",
            FileName = "cmd.exe",
            Arguments = new[] { "/d", "/c", "ping", "-n", "3600", "127.0.0.1" },
            WorkingDirectory = checkoutDirectory.Root,
            StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        });

        Assert.True(session.IsRunning);

        await session.StopAsync();
        await session.StopAsync();

        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(session.IsRunning);
        Assert.Equal(ProcessTerminationReason.UserCancelled, result.TerminationReason);
        Assert.Equal("exec-protocol-stop", result.ExecutionId);
    }

    private static ProcessSupervisor CreateSupervisor(string appDataDirectory) =>
        new(
            Options.Create(new ProcessSupervisorOptions
            {
                GracefulShutdownTimeout = TimeSpan.FromSeconds(2)
            }),
            new StorageOptions { AppDataDirectory = appDataDirectory });
}
