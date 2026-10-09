using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// Composition-level negative controls for the Phase 10 native account-context switch.
///
/// The fake account-context manager below reports a resolved context for both kinds: the strongest
/// evidence a test can fabricate. It is registered into the real infrastructure graph on purpose, to
/// prove that adapter evidence is not an input to the switch and that a fake cannot authorize a native
/// switch. It is test-only by construction: no production composition, the hardening runner or the WPF
/// HostBootstrapper resolves it, which the descriptor assertions below check.
/// </summary>
public sealed class NativeAccountContextSwitchCompositionTests
{
    [Fact]
    public async Task AReportingFakeAdapter_CannotAuthorizeASwitch()
    {
        using var dataDirectory = new TestDirectory();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IAccountContextManager>(new ReportingFakeAccountContextManager());
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        var studio = provider.GetRequiredService<IWorkflowStudioService>();
        var mirasim = new WorkflowMirasimIsolationState("mirasim-account-1", "relay-active", true);

        var agy = await studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Agy,
                "profile-alpha",
                PreviousNativeSessionId: "native-agy-old",
                AgyProfileName: "profile-alpha",
                RequestedRouteId: "route-star-cliproxy-agy",
                MirasimState: mirasim));

        var codexHome = Path.Combine(Path.GetTempPath(), "codex-home-fake-adapter");

        var codex = await studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Codex,
                "codex-account-alpha",
                PreviousNativeSessionId: "native-codex-old",
                CodexHomePath: codexHome,
                RequestedRouteId: "route-star-cliproxy-codex",
                MirasimState: mirasim));

        foreach (var result in new[] { agy, codex })
        {
            Assert.False(result.IsSwitched);
            Assert.True(result.IsBlocked);
            Assert.Equal(
                WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
                result.Refusal);
            Assert.Null(result.NativeSessionId);
            Assert.Null(result.ObservedRouteId);
            Assert.Equal("none", result.RouteEvidenceSource);
            Assert.False(result.CarriesPreviousSession);
            Assert.False(result.CredentialsTransferred);
            Assert.Same(mirasim, result.MirasimState);
        }

        // The existing native session of each kind is reported back untouched, never forked.
        Assert.Equal("native-agy-old", agy.PreviousNativeSessionId);
        Assert.Equal("native-codex-old", codex.PreviousNativeSessionId);

        // The refusal names both missing halves and never repeats the CODEX_HOME path.
        Assert.Contains("agy-profile executable", agy.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("star-cliproxy.exe", codex.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("unique route key", codex.FailureReason!, StringComparison.Ordinal);
        Assert.DoesNotContain(codexHome, codex.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShippedInfrastructureGraph_ComposesTheRealStudioAndRefusesTheSwitch()
    {
        using var dataDirectory = new TestDirectory();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        // The production graph resolves the real studio and the real hardening runner, never a double.
        var studio = provider.GetRequiredService<IWorkflowStudioService>();
        var runner = provider.GetRequiredService<IEndToEndWorkflowScenarioRunner>();

        Assert.IsType<WorkflowStudioService>(studio);

        var result = await studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Agy,
                "profile-alpha",
                AgyProfileName: "profile-alpha",
                RequestedRouteId: "route-star-cliproxy-agy",
                MirasimState: new WorkflowMirasimIsolationState("mirasim-account-1", "relay-active", true)));

        Assert.False(result.IsSwitched);
        Assert.Equal(
            WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
            result.Refusal);
        Assert.Null(result.NativeSessionId);

        // No production descriptor is a test double, and the hardening runner takes the real studio.
        var testDoubleDescriptors = services
            .Where(descriptor => descriptor.ImplementationType is not null
                && descriptor.ImplementationType.Namespace?.Contains("Tests", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Empty(testDoubleDescriptors);

        var accountContextDescriptor = services
            .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IAccountContextManager));

        Assert.NotNull(accountContextDescriptor);
        Assert.DoesNotContain(
            "Tests",
            accountContextDescriptor!.ImplementationType?.Namespace ?? string.Empty,
            StringComparison.Ordinal);
        Assert.NotNull(runner);
    }

    private sealed class ReportingFakeAccountContextManager : IAccountContextManager
    {
        private readonly CodexAccountContext _codex = new(
            "codex-account-alpha",
            Path.Combine(Path.GetTempPath(), "codex-home-fake-adapter"));

        public IReadOnlyList<CodexAccountContext> CodexContexts => new[] { _codex };

        public bool HasPinnableContexts => true;

        public void RegisterCodexContext(CodexAccountContext context) =>
            throw new NotSupportedException("A test-only account-context manager registers nothing.");

        public Task<IReadOnlyList<AgyAccountContext>> ListAgyContextsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgyAccountContext>>(
                new[] { new AgyAccountContext("profile-alpha") });

        public Task<string?> GetActiveAgyProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("profile-alpha");

        public Task<AccountContextSelection> SelectAndVerifyAsync(
            AccountContextRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Resolve(request));

        public Task<AccountContextSelection> VerifyContextAsync(
            AccountContextRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Resolve(request));

        public Task<TResult> ExecuteSerializedAsync<TResult>(
            AccountContextRequest request,
            Func<AccountContextSelection, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default) =>
            operation(Resolve(request), cancellationToken);

        private AccountContextSelection Resolve(AccountContextRequest request) =>
            request.Kind == AccountContextKind.Codex
                ? AccountContextSelection.ResolvedCodex(_codex, requiresNewSession: true)
                : AccountContextSelection.ResolvedAgy(request.AccountId, requiresNewSession: true);
    }
}
