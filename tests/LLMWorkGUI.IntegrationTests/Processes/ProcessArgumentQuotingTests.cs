using System.Text;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessArgumentQuotingTests
{
    private static readonly string LongWindowsPath =
        @"C:\" + string.Join('\\', Enumerable.Repeat("long-segment-abcdefghijklmnopqrstuvwxyz", 12)) + @"\file.txt";

    public static IEnumerable<object[]> ArgumentBoundaryCases
    {
        get
        {
            yield return new object[] { "empty", string.Empty };
            yield return new object[] { "spaces", "a b c" };
            yield return new object[] { "double-quotes", "\"hello\"" };
            yield return new object[] { "single-quotes", "'test'" };
            yield return new object[] { "dollar-variable", "$VAR" };
            yield return new object[] { "percent-variable", "%PATH%" };
            yield return new object[] { "unicode-cyrillic-cjk", "Привет 世界" };
            yield return new object[] { "unicode-emoji", "turn 🚀 done" };
            yield return new object[] { "long-windows-path", LongWindowsPath };
            yield return new object[] { "backslashes", @"back\\slash" };
            yield return new object[] { "trailing-backslash", @"trailing\" };
            yield return new object[] { "leading-spaces", "  leading" };
            yield return new object[] { "shell-metacharacters", "a & echo injected | more" };
        }
    }

    [Theory]
    [MemberData(nameof(ArgumentBoundaryCases))]
    public async Task ExecuteAsync_TransfersSingleBoundaryArgumentExactly(string caseName, string argument)
    {
        Assert.False(string.IsNullOrEmpty(caseName));

        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var capturePath = harness.GetPath("captured-single-argument.txt");
        var specification = harness.CreateArgumentCaptureSpecification(
            "exec-argument-" + caseName,
            capturePath,
            argument);

        var result = await supervisor.ExecuteAsync(specification);

        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardErrorHead);

        var captured = await ReadCapturedArgumentsAsync(capturePath);

        Assert.Single(captured);
        Assert.Equal(argument, captured[0]);
    }

    [Fact]
    public async Task ExecuteAsync_TransfersAllArgumentsWithoutMergingOrShellInterpolation()
    {
        using var harness = new FakeProcessHarness();
        using var dataDirectory = new TestDirectory();
        var supervisor = CreateSupervisor(dataDirectory.Root);

        var arguments = new[]
        {
            string.Empty,
            "a b c",
            "\"hello\"",
            "'test'",
            "$VAR",
            "%PATH%",
            "Привет 世界",
            "turn 🚀 done",
            LongWindowsPath,
            @"back\\slash",
            "trailing\\",
            "  leading",
            "a & echo injected | more"
        };

        var capturePath = harness.GetPath("captured-all-arguments.txt");
        var specification = harness.CreateArgumentCaptureSpecification(
            "exec-arguments-boundary",
            capturePath,
            arguments);

        var result = await supervisor.ExecuteAsync(specification);

        Assert.Equal(ProcessTerminationReason.None, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);

        var captured = await ReadCapturedArgumentsAsync(capturePath);

        Assert.Equal(arguments, captured);
    }

    private static async Task<IReadOnlyList<string>> ReadCapturedArgumentsAsync(string capturePath)
    {
        Assert.True(File.Exists(capturePath), $"The argument capture file '{capturePath}' was not created.");

        var lines = await File.ReadAllLinesAsync(capturePath);

        return lines
            .Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line)))
            .ToArray();
    }

    private static ProcessSupervisor CreateSupervisor(string appDataDirectory)
    {
        return new ProcessSupervisor(
            Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = appDataDirectory });
    }
}
