using System;
using System.IO;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

/// <summary>
/// Minimal read-only live smoke against an installed <c>cursor-agent</c> (ROADMAP Phase 6 exit
/// criteria). It only discovers the executable and performs the ACP <c>initialize</c> handshake: no
/// prompt is ever sent, so no model quota is spent (ROADMAP general rule 4).
/// </summary>
/// <remarks>
/// The whole class is skipped when Cursor Agent is not installed, so the suite stays green on a clean
/// machine while still producing real evidence where the CLI exists.
/// </remarks>
[Collection(CursorAcpProcessCollection.Name)]
public sealed class CursorAcpLiveSmokeTests
{
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task LiveCursorAgent_IsDiscovered_AndReportsVersion()
    {
        var resolver = CreateResolver();
        var resolution = await resolver.ResolveAsync();

        if (!resolution.IsAvailable)
        {
            // A clean machine without Cursor Agent is a supported configuration: the degraded result
            // must still be typed and carry a blocker, and that is what is asserted instead.
            Assert.False(string.IsNullOrWhiteSpace(resolution.Blocker));
            Assert.NotNull(resolution.Guidance);
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(resolution.ExecutablePath));
        Assert.True(File.Exists(resolution.ExecutablePath), resolution.ExecutablePath);

        // The version is observed evidence, never a guess.
        Assert.False(string.IsNullOrWhiteSpace(resolution.Version));

        // Discovery output must not leak the user profile path.
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            resolution.Version!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveCursorAgent_CompletesRealInitializeHandshake_WithoutSendingAnyPrompt()
    {
        var resolver = CreateResolver();
        var resolution = await resolver.ResolveAsync();

        if (!resolution.IsAvailable)
        {
            // Without the CLI there is nothing live to prove; the degraded discovery path is covered
            // by the test above and by the process-manager contract tests.
            return;
        }

        using var dataDirectory = new TestDirectory();

        var options = Options.Create(new CursorAcpOptions
        {
            HandshakeTimeout = LiveTimeout,
            RequestTimeout = LiveTimeout,
            ShutdownTimeout = TimeSpan.FromSeconds(15)
        });

        var supervisor = new ProcessSupervisor(
            Options.Create(new ProcessSupervisorOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(30),
                GracefulShutdownTimeout = TimeSpan.FromSeconds(5)
            }),
            new StorageOptions { AppDataDirectory = dataDirectory.Root });

        var manager = new CursorAcpProcessManager(
            resolver,
            supervisor,
            new StorageOptions { AppDataDirectory = dataDirectory.Root },
            options,
            new JsonRpcStdioTransportFactory(options));

        await using var lifecycle = new CursorAcpSessionLifecycleService(
            manager,
            new CursorAcpClientFactory(options),
            new CursorAcpModePolicy(),
            options);

        var started = await lifecycle.StartBackendAsync("live-smoke-handshake");

        // The handshake must reach a ready state against the installed agent. A drifting ACP surface
        // is a real finding, so it fails the smoke with the exact typed reason instead of passing
        // quietly.
        Assert.True(
            started.IsReady,
            $"The live ACP handshake did not reach a ready state: {started.FailureKind} - {started.Blocker}");

        Assert.NotNull(started.Session);
        Assert.NotNull(started.Session!.Transport);

        // ADR-0003 §2.2: the protocol version must be the JSON number 1.
        Assert.Equal(1, started.Handshake!.ProtocolVersion);

        // ADR-0003 §2.3: the required capabilities must be present, not assumed.
        Assert.True(started.Handshake.AgentCapabilities.LoadSession);
        Assert.True(started.Handshake.AgentCapabilities.SessionList);

        // No prompt was sent, so no session was created and no quota was consumed.
        Assert.Null(lifecycle.NativeSessionId);
        Assert.Empty(lifecycle.Ancestry);

        await lifecycle.StopBackendAsync();
        Assert.Null(lifecycle.Current);
    }

    private static WindowsCursorAgentExecutableResolver CreateResolver() =>
        new(
            new ProcessSupervisor(
                Options.Create(new ProcessSupervisorOptions
                {
                    StartupTimeout = TimeSpan.FromSeconds(30)
                }),
                new StorageOptions { AppDataDirectory = Path.GetTempPath() }),
            Options.Create(new CursorAcpOptions
            {
                VersionProbeTimeout = TimeSpan.FromSeconds(60)
            }));
}
