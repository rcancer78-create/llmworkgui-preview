using System.Diagnostics;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessStdinPolicyTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly ProcessTimingLog _timings = new();
    public void Dispose() => output.WriteLine(_timings.ToString());
    [Fact]
    public void ProcessStartSpecification_DefaultStdinPolicy_IsClosed()
    {
        var specification = new ProcessStartSpecification
        {
            ExecutionId = "exec-default-stdin",
            FileName = "unused.exe"
        };

        Assert.Equal(ProcessStdinPolicy.Closed, specification.StdinPolicy);
    }

    [Fact]
    public async Task ExecuteAsync_ClosedStdin_ChildReadGetsImmediateEof()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = harness.CreateStdinReadSpecification("exec-stdin-closed");
        Assert.Equal(ProcessStdinPolicy.Closed, specification.StdinPolicy);

        var stopwatch = Stopwatch.StartNew();
        var operation = supervisor.ExecuteAsync(specification);
        var result = await _timings.MeasureCompletionAsync(operation, stopwatch);
        _timings.RecordElapsed("test-resumed", stopwatch);
        stopwatch.Stop();

        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stdin-eof", result.StandardOutputHead, StringComparison.Ordinal);
        Assert.DoesNotContain("stdin-value=", result.StandardOutputHead, StringComparison.Ordinal);
        Assert.True(
            _timings.CompletionElapsed < TimeSpan.FromSeconds(30),
            "A child process reading a closed stdin must receive EOF instead of hanging.");
    }

    [Fact]
    public async Task ExecuteAsync_NullStdin_ChildReadGetsImmediateEof()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = harness.CreateStdinReadSpecification(
            "exec-stdin-null",
            ProcessStdinPolicy.Null);

        var stopwatch = Stopwatch.StartNew();
        var operation = supervisor.ExecuteAsync(specification);
        var result = await _timings.MeasureCompletionAsync(operation, stopwatch);
        _timings.RecordElapsed("test-resumed", stopwatch);
        stopwatch.Stop();

        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stdin-eof", result.StandardOutputHead, StringComparison.Ordinal);
        Assert.True(
            _timings.CompletionElapsed < TimeSpan.FromSeconds(30),
            "A child process reading a null stdin must receive EOF instead of hanging.");
    }

    [Fact]
    public async Task ExecuteAsync_DirectProtocolTransport_RejectsBeforeLaunchAndSpooling()
    {
        using var dataDirectory = new TestDirectory();
        using var checkout = new TestDirectory();
        var launches = 0;
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = dataDirectory.Root }, null, null, _ =>
            {
                Interlocked.Increment(ref launches);
                throw new InvalidOperationException("A protocol-only specification reached native launch.");
            });
        var specification = new ProcessStartSpecification
        {
            ExecutionId = "exec-stdin-direct", FileName = "unused.exe",
            WorkingDirectory = checkout.Root, StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        };

        // The old expectation kept inaccessible stdin open until cancellation. ExecuteAsync cannot
        // hand that stream to a transport; only StartProtocolProcessAsync supports duplex ownership.
        var error = await Assert.ThrowsAsync<ArgumentException>(() => supervisor.ExecuteAsync(specification));
        Assert.Contains("StartProtocolProcessAsync", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, launches);
        Assert.False(Directory.Exists(Path.Combine(dataDirectory.Root, "runs", specification.ExecutionId)));
    }

    private ProcessSupervisor CreateSupervisor(
        string appDataDirectory,
        ProcessSupervisorOptions? options = null)
    {
        return new ProcessSupervisor(
            Options.Create(options ?? new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = appDataDirectory }, logger: _timings);
    }
}
