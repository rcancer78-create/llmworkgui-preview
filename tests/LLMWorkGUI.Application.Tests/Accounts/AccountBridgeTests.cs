using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Accounts;

public sealed class AccountBridgeTests
{
    [Fact]
    public async Task MockAccountBridge_ConcurrentRegistrationAndDiscoveryPreserveEveryAccountAndSnapshot()
    {
        var bridge = new MockAccountBridge();
        bridge.RegisterAccount("profile", new("seed", "seed", "seed", AuthState.Unknown));
        const int writerCount = 6;
        const int accountsPerWriter = 128;
        var writersFinished = 0;
        using var start = new ManualResetEventSlim();
        var writers = Enumerable.Range(0, writerCount).Select(writer => Task.Factory.StartNew(() =>
        {
            start.Wait();
            try
            {
                for (var index = 0; index < accountsPerWriter; index++)
                {
                    var id = $"owned-account-{writer}-{index}";
                    bridge.RegisterAccount("profile", new(id, id, id, AuthState.Unknown));
                }
            }
            finally { Interlocked.Increment(ref writersFinished); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var reader = Task.Factory.StartNew(async () =>
        {
            start.Wait();
            do
            {
                var snapshot = await bridge.DiscoverAccountsAsync("profile");
                Assert.Equal(snapshot.Count, snapshot.Select(account => account.Id).Distinct().Count());
            } while (Volatile.Read(ref writersFinished) != writerCount);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        start.Set();
        await Task.WhenAll(writers.Append(reader)).WaitAsync(TimeSpan.FromSeconds(20));
        var accounts = await bridge.DiscoverAccountsAsync("profile");
        Assert.Equal(1 + writerCount * accountsPerWriter, accounts.Count);
        Assert.Equal(accounts.Count, accounts.Select(account => account.Id).Distinct().Count());
        for (var writer = 0; writer < writerCount; writer++)
            for (var index = 0; index < accountsPerWriter; index++)
                Assert.Contains(accounts, account => account.Id == $"owned-account-{writer}-{index}");
    }

    [Fact]
    public async Task MockAccountBridge_DiscoversAccounts_AndManagesAuthState()
    {
        var bridge = new MockAccountBridge();

        var account1 = new DiscoveredAccountInfo("acc-1", "Work Account", "user-1", AuthState.Valid, "user@work.com", true);
        var account2 = new DiscoveredAccountInfo("acc-2", "Personal Account", "user-2", AuthState.Valid, "user@home.com", false);

        bridge.RegisterAccount("prov-1", account1);
        bridge.RegisterAccount("prov-1", account2);

        var discovered = await bridge.DiscoverAccountsAsync("prov-1");
        Assert.Equal(2, discovered.Count);
        Assert.Contains(discovered, a => a.Id == "acc-1");
        Assert.Contains(discovered, a => a.Id == "acc-2");

        // Probe auth
        var probeResult = await bridge.ProbeAuthAsync("prov-1", "acc-1");
        Assert.Equal(AuthState.Valid, probeResult.State);

        // Update auth state to invalid
        bridge.SetAuthState("acc-1", AuthState.Invalid);
        var probeAfter = await bridge.ProbeAuthAsync("prov-1", "acc-1");
        Assert.Equal(AuthState.Invalid, probeAfter.State);
        Assert.NotNull(probeAfter.ErrorMessage);
    }

    [Fact]
    public async Task MockAccountBridge_PinsAccount_Successfully()
    {
        var bridge = new MockAccountBridge();
        var binding = new SessionBinding(
            BackendType.OpenCode,
            "prov-1",
            "acc-1",
            "gpt-4o",
            null,
            null,
            null);

        var result = await bridge.PinAccountAsync("prov-1", "acc-1", binding);
        Assert.True(result.IsPinned);
        Assert.Null(result.FailureReason);
        Assert.Same(binding, result.ConfirmedBinding);
        Assert.Same(binding, bridge.GetPinnedBinding("acc-1"));
    }

    [Fact]
    public async Task MockAccountBridge_RejectsPinning_WhenPinningDisabled()
    {
        var bridge = new MockAccountBridge { SupportsPinning = false };
        var binding = new SessionBinding(
            BackendType.OpenCode,
            "prov-1",
            "acc-1",
            "gpt-4o",
            null,
            null,
            null);

        var result = await bridge.PinAccountAsync("prov-1", "acc-1", binding);
        Assert.False(result.IsPinned);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("does not support explicit account pinning", result.FailureReason);
    }

    [Fact]
    public async Task OpenCodePluginAccountBridge_DeclaredContractCannotFabricateAuthOrPinAcknowledgement()
    {
        var bridge = new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: true);
        var binding = new SessionBinding(BackendType.OpenCode, "prov-opencode", "acc-1", "model-1", null, null, null);
        Assert.False(bridge.SupportsPinning); Assert.False(bridge.SupportsObservedRoute);
        var pin = await bridge.PinAccountAsync("prov-opencode", "acc-1", binding);
        Assert.False(pin.IsPinned); Assert.Null(pin.ConfirmedBinding);
        Assert.Contains("no native account pin adapter", pin.FailureReason);
        Assert.Equal(AuthState.Unknown, (await bridge.ProbeAuthAsync("prov-opencode", "acc-1")).State);
        Assert.Empty(await bridge.DiscoverAccountsAsync("prov-opencode"));
    }

    [Fact]
    public async Task OpenCodePluginAccountBridge_DoesNotFabricateAccount_WhenNoDiscoveryContract()
    {
        var bridge = new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: false);

        Assert.False(bridge.SupportsPinning);
        Assert.False(bridge.SupportsObservedRoute);

        var accounts = await bridge.DiscoverAccountsAsync("prov-opencode");
        Assert.Empty(accounts);
    }

    [Fact]
    public async Task OpenCodePluginAccountBridge_RejectsPinning_WithAdr0004Explanation()
    {
        var bridge = new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: false);
        var binding = new SessionBinding(
            BackendType.OpenCode,
            "prov-opencode",
            "acc-1",
            "claude-3-5-sonnet",
            null,
            null,
            null);

        var result = await bridge.PinAccountAsync("prov-opencode", "acc-1", binding);
        Assert.False(result.IsPinned);
        Assert.Contains("ADR-0004 §2.3", result.FailureReason);
    }

    [Fact]
    public async Task OpenCodePluginAccountBridge_ReportsUnknownAuthProbe_WhenNoPlugin()
    {
        var bridge = new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: false);

        var probeResult = await bridge.ProbeAuthAsync("prov-opencode", "acc-1");
        Assert.Equal(AuthState.Unknown, probeResult.State);
        Assert.Contains("Authentication probe is unsupported", probeResult.ErrorMessage);
    }

    [Theory]
    [InlineData("agy")]
    [InlineData("prov-agy")]
    [InlineData("agy-personal")]
    [InlineData("codex-work")]
    [InlineData("antigravity-cli")]
    [InlineData("AGY_PROFILE")]
    public void OpenCodePluginAccountBridge_ClassifiesAgyAndCodexProviderIds(string providerProfileId)
    {
        Assert.True(OpenCodePluginAccountBridge.IsAgyOrCodexProvider(providerProfileId));
    }

    [Theory]
    [InlineData("prov-opencode")]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("")]
    public void OpenCodePluginAccountBridge_DoesNotClassifyOtherProviderIds(string providerProfileId)
    {
        Assert.False(OpenCodePluginAccountBridge.IsAgyOrCodexProvider(providerProfileId));
    }

    [Theory]
    [InlineData("prov-agy")]
    [InlineData("codex-work")]
    [InlineData("antigravity")]
    public async Task OpenCodePluginAccountBridge_RefusesPinningAgyOrCodex_WithSpecRefusal(
        string providerProfileId)
    {
        var bridge = new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: true);
        var binding = new SessionBinding(
            BackendType.OpenCode,
            providerProfileId,
            "acc-1",
            "claude-3-5-sonnet",
            null,
            null,
            null);

        var result = await bridge.PinAccountAsync(providerProfileId, "acc-1", binding);

        Assert.False(result.IsPinned);
        Assert.Null(result.ConfirmedBinding);
        Assert.Equal(OpenCodePluginAccountBridge.AgyCodexPluginRefusalReason, result.FailureReason);
        Assert.Contains("OpenCode plugins are not permitted to switch AGY or Codex accounts", result.FailureReason);
        Assert.Contains("Use the star-cliproxy gateway with agy-profile / CODEX_HOME account contexts instead (ADR-0007).", result.FailureReason);
    }

    [Fact]
    public async Task OpenCodePluginAccountBridge_RefusesPinningStarCliProxyBackend_EvenForNeutralProviderId()
    {
        var bridge = new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: true);
        var binding = new SessionBinding(
            BackendType.StarCliProxy,
            "prov-neutral",
            "codex:work",
            "gpt-5.5",
            null,
            null,
            null);

        var result = await bridge.PinAccountAsync("prov-neutral", "codex:work", binding);

        Assert.False(result.IsPinned);
        Assert.Equal(OpenCodePluginAccountBridge.AgyCodexPluginRefusalReason, result.FailureReason);
    }

    [Fact]
    public async Task OpenCodePluginAccountBridge_RefusesPinningAgyBackend_EvenForNeutralProviderId()
    {
        var bridge = new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: true);
        var binding = new SessionBinding(
            BackendType.Agy,
            "prov-neutral",
            "acc-1",
            "gemini-2.5-pro",
            null,
            null,
            null);

        var result = await bridge.PinAccountAsync("prov-neutral", "acc-1", binding);

        Assert.False(result.IsPinned);
        Assert.Equal(OpenCodePluginAccountBridge.AgyCodexPluginRefusalReason, result.FailureReason);
    }

    [Theory]
    [InlineData("prov-agy")]
    [InlineData("codex-work")]
    public async Task OpenCodePluginAccountBridge_RefusesAgyOrCodexAuthProbe(string providerProfileId)
    {
        var bridge = new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: true);

        var probeResult = await bridge.ProbeAuthAsync(providerProfileId, "acc-1");

        Assert.Equal(AuthState.Unknown, probeResult.State);
        Assert.Equal(OpenCodePluginAccountBridge.AgyCodexPluginRefusalReason, probeResult.ErrorMessage);
    }

    [Theory]
    [InlineData("prov-agy")]
    [InlineData("codex-work")]
    public async Task OpenCodePluginAccountBridge_HidesAgyOrCodexDiscoveredAccounts(string providerProfileId)
    {
        var bridge = new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: true);

        var accounts = await bridge.DiscoverAccountsAsync(providerProfileId);

        Assert.Empty(accounts);
    }
}
