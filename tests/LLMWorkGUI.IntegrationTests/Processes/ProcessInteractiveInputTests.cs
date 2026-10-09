using System.Diagnostics;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessInteractiveInputTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly ProcessTimingLog _timings = new();
    public void Dispose() => output.WriteLine(_timings.ToString());
    [Fact]
    public async Task ExecuteAsync_CommandPromptsForStdin_ReceivesEofAndExitsWithoutHanging()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = harness.CreateStdinReadSpecification("interactive-command");

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
            "The child process must observe EOF immediately instead of waiting for interactive input.");
    }

    [Fact]
    public async Task ExecuteAsync_PowerShellReadLine_ReturnsNullAndExitsWithoutHanging()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = harness.CreatePowerShellStdinReadSpecification("interactive-powershell");

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
            "Console.In.ReadLine must return null on closed stdin instead of hanging.");
    }

    [Fact]
    public async Task ExecuteAsync_InteractiveInputFailure_ExitsWithEvidenceInsteadOfHanging()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var specification = harness.CreateStdinReadFailureSpecification("interactive-failure");

        var stopwatch = Stopwatch.StartNew();
        var operation = supervisor.ExecuteAsync(specification);
        var result = await _timings.MeasureCompletionAsync(operation, stopwatch);
        _timings.RecordElapsed("test-resumed", stopwatch);
        stopwatch.Stop();

        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("stdin", result.StandardErrorHead, StringComparison.OrdinalIgnoreCase);
        Assert.True(_timings.CompletionElapsed < TimeSpan.FromSeconds(30));
    }

    private ProcessSupervisor CreateSupervisor(string appDataDirectory)
    {
        return new ProcessSupervisor(
            Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = appDataDirectory }, logger: _timings);
    }
}
