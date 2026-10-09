using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.Abstractions.Processes;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class WindowsCursorAgentExecutableResolverContractTests
{
    private const string UserProfile = @"C:\Users\alice";

    [Fact]
    public async Task ResolveAsync_EnvironmentOverride_WinsAndProbesVersion()
    {
        var supervisor = StubProcessSupervisor.Returning(
            exitCode: 0,
            standardOutput: "cursor-agent 2026.09.15-d2fe57e");

        var executablePath = @"C:\tools\cursor-agent.cmd";
        var resolver = Create(
            supervisor,
            new Dictionary<string, string?>
            {
                ["CURSOR_AGENT_EXECUTABLE"] = executablePath,
                ["USERPROFILE"] = UserProfile
            },
            path => string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase));

        var resolution = await resolver.ResolveAsync();

        Assert.True(resolution.IsAvailable);
        Assert.Equal(CursorAcpExecutableStatus.Found, resolution.Status);
        Assert.Equal(executablePath, resolution.ExecutablePath);
        Assert.Equal("2026.09.15-d2fe57e", resolution.Version);
        Assert.Null(resolution.Blocker);

        var specification = Assert.Single(supervisor.Specifications);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), specification.FileName);
        Assert.Equal(
            new[] { "/d", "/c", executablePath, "--version" },
            specification.Arguments);
        Assert.Equal(ProcessStdinPolicy.Closed, specification.StdinPolicy);
    }

    [Fact]
    public async Task ResolveAsync_PathDirectory_ResolvesExecutableWithoutBatchWrapper()
    {
        var supervisor = StubProcessSupervisor.Returning(exitCode: 0, standardOutput: "1.2.3");
        var executablePath = @"C:\bin\cursor-agent.exe";

        var resolver = Create(
            supervisor,
            new Dictionary<string, string?> { ["PATH"] = @"C:\bin;D:\other" },
            path => string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase));

        var resolution = await resolver.ResolveAsync();

        Assert.True(resolution.IsAvailable);
        Assert.Equal(executablePath, resolution.ExecutablePath);
        Assert.Equal("1.2.3", resolution.Version);

        var specification = Assert.Single(supervisor.Specifications);
        Assert.Equal(executablePath, specification.FileName);
        Assert.Equal(new[] { "--version" }, specification.Arguments);
    }

    [Fact]
    public async Task ResolveAsync_TypicalCursorInstallDirectory_IsSearched()
    {
        var supervisor = StubProcessSupervisor.Returning(exitCode: 0, standardOutput: "2026.09.15-d2fe57e");
        var executablePath = @"C:\Users\alice\AppData\Local\Programs\cursor\resources\app\bin\cursor-agent.cmd";

        var resolver = Create(
            supervisor,
            new Dictionary<string, string?>
            {
                ["LOCALAPPDATA"] = @"C:\Users\alice\AppData\Local",
                ["USERPROFILE"] = UserProfile
            },
            path => string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase));

        var resolution = await resolver.ResolveAsync();

        Assert.True(resolution.IsAvailable);
        Assert.Equal(executablePath, resolution.ExecutablePath);
    }

    [Fact]
    public async Task ResolveAsync_NotInstalled_ReturnsMissingWithBlockerAndGuidance()
    {
        var supervisor = StubProcessSupervisor.Returning();
        var resolver = Create(supervisor, new Dictionary<string, string?>(), _ => false);

        var resolution = await resolver.ResolveAsync();

        Assert.False(resolution.IsAvailable);
        Assert.Equal(CursorAcpExecutableStatus.Missing, resolution.Status);
        Assert.Equal(CursorAcpPolicy.NotInstalledBlocker, resolution.Blocker);
        Assert.Equal(CursorAcpPolicy.NotInstalledGuidance, resolution.Guidance);
        Assert.Empty(supervisor.Specifications);
    }

    [Fact]
    public async Task ResolveAsync_ConfiguredPathMissing_ReturnsMissingWithMaskedPath()
    {
        var supervisor = StubProcessSupervisor.Returning();
        var resolver = Create(
            supervisor,
            new Dictionary<string, string?>
            {
                ["CURSOR_AGENT_PATH"] = @"C:\Users\alice\missing\cursor-agent.cmd",
                ["USERPROFILE"] = UserProfile
            },
            _ => false);

        var resolution = await resolver.ResolveAsync();

        Assert.Equal(CursorAcpExecutableStatus.Missing, resolution.Status);
        Assert.DoesNotContain("alice", resolution.Blocker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", resolution.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_VersionProbeTimeout_ReturnsUnresolved()
    {
        var executablePath = @"C:\tools\cursor-agent.exe";
        var resolver = Create(
            StubProcessSupervisor.Hanging(),
            new Dictionary<string, string?> { ["CURSOR_AGENT_EXECUTABLE"] = executablePath },
            path => string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase),
            options: new CursorAcpOptions { VersionProbeTimeout = TimeSpan.FromMilliseconds(200) });

        var resolution = await resolver.ResolveAsync();

        Assert.Equal(CursorAcpExecutableStatus.Unresolved, resolution.Status);
        Assert.Contains("did not complete", resolution.Blocker, StringComparison.Ordinal);
        Assert.Equal(CursorAcpPolicy.VersionProbeGuidance, resolution.Guidance);
    }

    [Fact]
    public async Task ResolveAsync_NonZeroExit_ReturnsUnresolvedWithMaskedStderr()
    {
        var executablePath = @"C:\tools\cursor-agent.exe";
        var resolver = Create(
            StubProcessSupervisor.Returning(
                exitCode: 1,
                standardError: @"fatal: cannot open C:\Users\alice\.cursor\log.txt"),
            new Dictionary<string, string?>
            {
                ["CURSOR_AGENT_EXECUTABLE"] = executablePath,
                ["USERPROFILE"] = UserProfile
            },
            path => string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase));

        var resolution = await resolver.ResolveAsync();

        Assert.Equal(CursorAcpExecutableStatus.Unresolved, resolution.Status);
        Assert.Contains("exited with code 1", resolution.Blocker, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", resolution.Blocker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", resolution.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_UnparsableVersionOutput_ReturnsUnresolved()
    {
        var executablePath = @"C:\tools\cursor-agent.exe";
        var resolver = Create(
            StubProcessSupervisor.Returning(exitCode: 0, standardOutput: "no version marker"),
            new Dictionary<string, string?> { ["CURSOR_AGENT_EXECUTABLE"] = executablePath },
            path => string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase));

        var resolution = await resolver.ResolveAsync();

        Assert.Equal(CursorAcpExecutableStatus.Unresolved, resolution.Status);
        Assert.Contains("did not report a parseable version", resolution.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_ConfiguredDirectory_AppendsExecutableName()
    {
        var supervisor = StubProcessSupervisor.Returning(exitCode: 0, standardOutput: "1.2.3");
        var executablePath = @"C:\tools\cursor-agent.exe";

        var resolver = Create(
            supervisor,
            new Dictionary<string, string?> { ["CURSOR_AGENT_EXECUTABLE"] = @"C:\tools" },
            path => string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase));

        var resolution = await resolver.ResolveAsync();

        Assert.True(resolution.IsAvailable);
        Assert.Equal(executablePath, resolution.ExecutablePath);
    }

    [Fact]
    public async Task ResolveAsync_VersionProbe_DoesNotInheritTheParentEnvironment()
    {
        const string sentinelName = "LLMWORKGUI_PARENT_SENTINEL";
        const string sentinelValue = "parent-only-cursor-probe";
        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            var supervisor = StubProcessSupervisor.Returning(exitCode: 0, standardOutput: "1.2.3");
            var executablePath = @"C:\bin\cursor-agent.exe";
            var resolver = Create(
                supervisor,
                new Dictionary<string, string?> { ["PATH"] = @"C:\bin;D:\parent-only-tools" },
                path => string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase));

            var resolution = await resolver.ResolveAsync();

            Assert.True(resolution.IsAvailable);
            var specification = Assert.Single(supervisor.Specifications);
            Assert.False(specification.InheritEnvironment);
            Assert.False(specification.EnvironmentVariables.ContainsKey(sentinelName));
            Assert.DoesNotContain(sentinelValue, specification.EnvironmentVariables.Values);
            Assert.DoesNotContain(@"D:\parent-only-tools", specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            var baseline = ProcessRuntimeEnvironment.CreateBaseline(executablePath);
            Assert.Equal(baseline["PATH"], specification.EnvironmentVariables["PATH"]);
            Assert.Contains(Environment.SystemDirectory, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WindowsPowerShell", specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"C:\bin", specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    [Fact]
    public async Task ResolveAsync_SupervisorFailure_ReturnsUnresolvedWithoutThrowing()
    {
        var executablePath = @"C:\tools\cursor-agent.exe";
        var resolver = Create(
            StubProcessSupervisor.Throwing(new InvalidOperationException("launch rejected")),
            new Dictionary<string, string?> { ["CURSOR_AGENT_EXECUTABLE"] = executablePath },
            path => string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase));

        var resolution = await resolver.ResolveAsync();

        Assert.Equal(CursorAcpExecutableStatus.Unresolved, resolution.Status);
        Assert.Contains("launch rejected", resolution.Blocker, StringComparison.Ordinal);
    }

    private static WindowsCursorAgentExecutableResolver Create(
        StubProcessSupervisor supervisor,
        IReadOnlyDictionary<string, string?> environment,
        Func<string, bool> fileExists,
        CursorAcpOptions? options = null) =>
        new(
            supervisor,
            key => environment.TryGetValue(key, out var value) ? value : null,
            fileExists,
            searchDirectories: null,
            options);
}
