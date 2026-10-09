using LLMWorkGUI.Application.Agy;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Application.Tests.Quotas;
using LLMWorkGUI.Application.Tests.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.StarCliProxy;

/// <summary>
/// Proves the Routing Engine resolves the star-cliproxy bridge for BackendType.StarCliProxy
/// and keeps automatic modes fail-closed while no valid account context exists (ADR-0007).
/// </summary>
public sealed class StarCliProxyRoutingBridgeTests
{
    [Fact]
    public async Task StarCliProxyRoute_WithoutValidContext_FailsClosedInAutomaticPolicy()
    {
        var accountRepo = new InMemoryAccountRepository();
        var snapshotRepo = new InMemoryQuotaSnapshotRepository();

        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        var bridge = new StarCliProxyAccountBridge(manager);

        var engine = new RoutingEngine(
            accountRepo,
            snapshotRepo,
            weights: null,
            timeProvider: TimeProvider.System,
            logger: null,
            providerProfileRepository: CreateProfiles(),
            accountBridges: new[] { bridge });

        await accountRepo.SaveAsync(new Account(
            "codex:work", "prov-star", "Codex Work", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var decision = await engine.SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.StarCliProxy,
            ProviderProfileId = "prov-star",
            ModelId = "gpt-5.5",
            Policy = RoutingPolicy.Balanced
        });

        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedAccount);
        Assert.Contains("Fail-Closed", decision.ExplanationText);
        Assert.Contains("star-cliproxy", decision.ExplanationText);
    }

    [Fact]
    public async Task StarCliProxyRoute_WithValidCodexHome_PassesFailClosedGate()
    {
        using var codexHome = new TemporaryDirectory();

        var accountRepo = new InMemoryAccountRepository();
        var snapshotRepo = new InMemoryQuotaSnapshotRepository();

        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        manager.RegisterCodexContext(new CodexAccountContext("work", codexHome.Root));

        var bridge = new StarCliProxyAccountBridge(manager);

        Assert.True(bridge.SupportsPinning);

        var engine = new RoutingEngine(
            accountRepo,
            snapshotRepo,
            weights: null,
            timeProvider: TimeProvider.System,
            logger: null,
            providerProfileRepository: CreateProfiles(),
            accountBridges: new[] { bridge });

        await accountRepo.SaveAsync(new Account(
            "codex:work", "prov-star", "Codex Work", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var decision = await engine.SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.StarCliProxy,
            ProviderProfileId = "prov-star",
            ModelId = "gpt-5.5",
            Policy = RoutingPolicy.Pinned,
            PinnedAccountId = "codex:work"
        });

        Assert.True(decision.IsSuccess);
        Assert.Equal("codex:work", decision.SelectedAccount!.Id);
        Assert.DoesNotContain("Fail-Closed", decision.ExplanationText);
    }

    private static InMemoryProviderProfileRepository CreateProfiles()
    {
        var profiles = new InMemoryProviderProfileRepository();
        profiles.Save(new ProviderProfile("prov-star", "Star", BackendType.StarCliProxy, null, null,
            DataClassification.PrivateSource, true));
        return profiles;
    }

    private sealed class FakeAgyProfileService : IAgyProfileService
    {
        public bool IsAvailable { get; set; }

        public string? ExecutablePath => null;

        public string? AvailabilityBlocker => IsAvailable ? null : "agy-profile not installed";

        public Task<string?> GetActiveProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<AgyProfileSummary>> ListProfilesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgyProfileSummary>>(Array.Empty<AgyProfileSummary>());

        public Task<AgyProfileSwitchResult> SwitchProfileAsync(
            string profileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AgyProfileSwitchResult.Failure("not available"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "LLMWorkGUI.Tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
