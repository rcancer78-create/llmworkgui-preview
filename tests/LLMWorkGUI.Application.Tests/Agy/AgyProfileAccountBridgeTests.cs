using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Agy;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Agy;

public sealed class AgyProfileAccountBridgeTests
{
    private const string MissingUtilityBlocker =
        "agy-profile utility was not found; install it from https://github.com/haclongkim/agy-profile";

    [Fact]
    public void Constructor_RejectsNullService()
    {
        Assert.Throws<ArgumentNullException>(() => new AgyProfileAccountBridge(null!));
    }

    [Fact]
    public void Capabilities_PinWithUtilityButNeverClaimPerTurnObservedEvidence()
    {
        var available = new FakeAgyProfileService { IsAvailable = true };
        var unavailable = new FakeAgyProfileService { IsAvailable = false };

        var availableBridge = new AgyProfileAccountBridge(available);
        var unavailableBridge = new AgyProfileAccountBridge(unavailable);

        Assert.Equal("agy", availableBridge.BackendId);
        Assert.True(availableBridge.SupportsPinning);
        Assert.False(availableBridge.SupportsObservedRoute);

        Assert.False(unavailableBridge.SupportsPinning);
        Assert.False(unavailableBridge.SupportsObservedRoute);
    }

    [Fact]
    public async Task DiscoverAccounts_WhenUnavailable_ReturnsEmptyList()
    {
        var service = new FakeAgyProfileService { IsAvailable = false };
        var bridge = new AgyProfileAccountBridge(service);

        var accounts = await bridge.DiscoverAccountsAsync("prov-agy");

        Assert.Empty(accounts);
    }

    [Fact]
    public async Task DiscoverAccounts_MapsProfilesAndMarksActiveProfile()
    {
        var service = new FakeAgyProfileService
        {
            IsAvailable = true,
            Profiles =
            [
                new AgyProfileSummary("work", IsActive: true),
                new AgyProfileSummary("personal", IsActive: false)
            ]
        };

        var bridge = new AgyProfileAccountBridge(service);

        var accounts = await bridge.DiscoverAccountsAsync("prov-agy");

        Assert.Equal(2, accounts.Count);

        var active = accounts.Single(account => account.Id == "work");
        Assert.True(active.IsDefault);
        Assert.Equal(AuthState.Unknown, active.AuthState);
        Assert.Equal("work", active.ProviderNativeId);

        var inactive = accounts.Single(account => account.Id == "personal");
        Assert.False(inactive.IsDefault);
        Assert.Equal(AuthState.Unknown, inactive.AuthState);
    }

    [Fact]
    public async Task PinAccount_WhenAlreadyActive_ReturnsSuccessWithoutSwitching()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "work" };
        var bridge = new AgyProfileAccountBridge(service);
        var binding = CreateBinding(accountId: "work");

        var result = await bridge.PinAccountAsync("prov-agy", "work", binding);

        Assert.True(result.IsPinned);
        Assert.False(result.RequiresNewSession);
        Assert.Empty(service.SwitchCalls);
        Assert.NotNull(result.ConfirmedBinding);
        Assert.NotSame(binding, result.ConfirmedBinding);
        Assert.Equal(BackendType.Agy, result.ConfirmedBinding!.Backend);
        Assert.Equal("work", result.ConfirmedBinding.AccountId);
        Assert.Equal(binding.ModelId, result.ConfirmedBinding.ModelId);
        Assert.Equal(binding.ReasoningEffort, result.ConfirmedBinding.ReasoningEffort);
        Assert.Equal(binding.SpeedMode, result.ConfirmedBinding.SpeedMode);
        Assert.Equal(binding.ExecutionMode, result.ConfirmedBinding.ExecutionMode);
    }

    [Fact]
    public async Task PinAccount_WhenAccountNotActive_SwitchesProfileAndReturnsFreshBinding()
    {
        var service = new FakeAgyProfileService
        {
            IsAvailable = true,
            ActiveProfile = "personal",
            SwitchResult = AgyProfileSwitchResult.Success("work")
        };
        var bridge = new AgyProfileAccountBridge(service);
        var binding = CreateBinding(accountId: "personal");

        var result = await bridge.PinAccountAsync("prov-agy", "work", binding);

        Assert.True(result.IsPinned);
        Assert.True(result.RequiresNewSession);
        Assert.Equal(new[] { "work" }, service.SwitchCalls);
        Assert.Equal("work", result.ConfirmedBinding!.AccountId);
        Assert.Equal("prov-agy", result.ConfirmedBinding.ProviderProfileId);

        // Invariant (ТЗ §6.11a): switching the account creates a new session; the previous
        // binding is not rewritten in place and no native conversation is carried over.
        Assert.Equal("personal", binding.AccountId);
    }

    [Theory]
    [InlineData("prov-agy", "personal")]
    [InlineData("other-provider-profile", "work")]
    [InlineData("other-provider-profile", "personal")]
    public async Task PinAccount_WhenTargetAlreadyActiveButBindingIdentityChanges_RequiresNewSession(
        string previousProviderProfileId, string previousAccountId)
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "work" };
        var bridge = new AgyProfileAccountBridge(service);
        var previous = new SessionBinding(BackendType.Agy, previousProviderProfileId, previousAccountId,
            "gemini-2.5-pro", "high", "fast", "accept-edits");

        var result = await bridge.PinAccountAsync("prov-agy", "work", previous);

        // The native profile may have changed outside this bridge since the caller's
        // session was bound. Its previous conversation still cannot be reused.
        Assert.True(result.IsPinned);
        Assert.True(result.RequiresNewSession);
        Assert.Empty(service.SwitchCalls);
        Assert.NotSame(previous, result.ConfirmedBinding);
        Assert.Equal("prov-agy", result.ConfirmedBinding!.ProviderProfileId);
        Assert.Equal("work", result.ConfirmedBinding.AccountId);
        Assert.Equal(previous.ModelId, result.ConfirmedBinding.ModelId);
        Assert.Equal(previous.ReasoningEffort, result.ConfirmedBinding.ReasoningEffort);
        Assert.Equal(previous.SpeedMode, result.ConfirmedBinding.SpeedMode);
        Assert.Equal(previous.ExecutionMode, result.ConfirmedBinding.ExecutionMode);
        Assert.Equal(previousProviderProfileId, previous.ProviderProfileId);
        Assert.Equal(previousAccountId, previous.AccountId);
        Assert.False(bridge.SupportsObservedRoute);
    }

    [Fact]
    public async Task PinAccount_WhenSwitchFails_ReturnsFailure()
    {
        var service = new FakeAgyProfileService
        {
            IsAvailable = true,
            ActiveProfile = "personal",
            SwitchResult = AgyProfileSwitchResult.Failure("agy is running; switch refused")
        };
        var bridge = new AgyProfileAccountBridge(service);
        var binding = CreateBinding(accountId: "personal");

        var result = await bridge.PinAccountAsync("prov-agy", "work", binding);

        Assert.False(result.IsPinned);
        Assert.Contains("agy is running", result.FailureReason);
        Assert.Null(result.ConfirmedBinding);
    }

    [Fact]
    public async Task PinAccount_WhenSwitchConfirmationDiffers_ReturnsFailure()
    {
        var service = new FakeAgyProfileService
        {
            IsAvailable = true,
            ActiveProfile = "personal",
            SwitchResult = AgyProfileSwitchResult.Success("other")
        };
        var bridge = new AgyProfileAccountBridge(service);
        var binding = CreateBinding(accountId: "personal");

        var result = await bridge.PinAccountAsync("prov-agy", "work", binding);

        Assert.False(result.IsPinned);
        Assert.Contains("not confirmed", result.FailureReason);
    }

    [Fact]
    public async Task PinAccount_WhenUtilityUnavailable_ReturnsBlocker()
    {
        var service = new FakeAgyProfileService { IsAvailable = false };
        var bridge = new AgyProfileAccountBridge(service);
        var binding = CreateBinding(accountId: "work");

        var result = await bridge.PinAccountAsync("prov-agy", "work", binding);

        Assert.False(result.IsPinned);
        Assert.Contains("https://github.com/haclongkim/agy-profile", result.FailureReason);
        Assert.Empty(service.SwitchCalls);
    }

    [Fact]
    public async Task PinAccount_RejectsNonAgyBackendBinding()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "work" };
        var bridge = new AgyProfileAccountBridge(service);
        var binding = CreateBinding(accountId: "work", backend: BackendType.OpenCode);

        var result = await bridge.PinAccountAsync("prov-agy", "work", binding);

        Assert.False(result.IsPinned);
        Assert.Contains("BackendType.Agy", result.FailureReason);
    }

    [Theory]
    [InlineData("work;next")]
    [InlineData("work with spaces")]
    [InlineData("work/../escape")]
    [InlineData("next")]
    [InlineData("RANDOM")]
    [InlineData("-Force")]
    [InlineData("-force")]
    [InlineData("--help")]
    [InlineData("-work")]
    public async Task PinAccount_RefusesUnsafeOrForbiddenNameBeforeUtilityInteraction(string accountId)
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = accountId };
        var bridge = new AgyProfileAccountBridge(service);

        var result = await bridge.PinAccountAsync("prov-agy", accountId, CreateBinding(accountId));

        Assert.False(result.IsPinned);
        Assert.Null(result.ConfirmedBinding);
        Assert.Empty(service.SwitchCalls);
        Assert.Equal(0, service.CurrentCalls);
    }

    [Fact]
    public async Task PinAccount_RejectsNullArguments()
    {
        var bridge = new AgyProfileAccountBridge(new FakeAgyProfileService());
        var binding = CreateBinding();

        await Assert.ThrowsAsync<ArgumentNullException>(() => bridge.PinAccountAsync(null!, "work", binding));
        await Assert.ThrowsAsync<ArgumentNullException>(() => bridge.PinAccountAsync("prov-agy", null!, binding));
        await Assert.ThrowsAsync<ArgumentNullException>(() => bridge.PinAccountAsync("prov-agy", "work", null!));
    }

    [Fact]
    public async Task ProbeAuth_WhenUtilityUnavailable_ReturnsUnknownWithBlocker()
    {
        var bridge = new AgyProfileAccountBridge(new FakeAgyProfileService { IsAvailable = false });

        var result = await bridge.ProbeAuthAsync("prov-agy", "work");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.Contains("https://github.com/haclongkim/agy-profile", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAuth_WhenNoActiveProfile_ReturnsUnknown()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = null };
        var bridge = new AgyProfileAccountBridge(service);

        var result = await bridge.ProbeAuthAsync("prov-agy", "work");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.Contains("No valid active AGY profile", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAuth_WhenAccountIsNotActive_ReturnsUnknown()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "personal" };
        var bridge = new AgyProfileAccountBridge(service);

        var result = await bridge.ProbeAuthAsync("prov-agy", "work");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.Contains("not the active profile", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAuth_ActiveProfileNameDoesNotProveNativeAuthentication()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "work" };
        var bridge = new AgyProfileAccountBridge(service);

        var result = await bridge.ProbeAuthAsync("prov-agy", "work");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    private static SessionBinding CreateBinding(
        string accountId = "personal",
        BackendType backend = BackendType.Agy)
    {
        return new SessionBinding(
            backend,
            "prov-agy",
            accountId,
            "gemini-2.5-pro",
            "high",
            "fast",
            "accept-edits");
    }

    private sealed class FakeAgyProfileService : IAgyProfileService
    {
        public bool IsAvailable { get; set; } = true;

        public string? ExecutablePath { get; set; } = @"C:\tools\agy-profile.cmd";

        public string? AvailabilityBlocker => IsAvailable ? null : MissingUtilityBlocker;

        public string? ActiveProfile { get; set; } = "personal";

        public IReadOnlyList<AgyProfileSummary> Profiles { get; set; } = Array.Empty<AgyProfileSummary>();

        public AgyProfileSwitchResult SwitchResult { get; set; } =
            AgyProfileSwitchResult.Success("work");

        public List<string> SwitchCalls { get; } = new();
        public int CurrentCalls { get; private set; }

        public Task<string?> GetActiveProfileAsync(CancellationToken cancellationToken = default)
        {
            CurrentCalls++;
            return Task.FromResult(ActiveProfile);
        }

        public Task<IReadOnlyList<AgyProfileSummary>> ListProfilesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Profiles);
        }

        public Task<AgyProfileSwitchResult> SwitchProfileAsync(
            string profileName,
            CancellationToken cancellationToken = default)
        {
            SwitchCalls.Add(profileName);

            if (SwitchResult.IsSwitched)
            {
                ActiveProfile = SwitchResult.ActiveProfile;
            }

            return Task.FromResult(SwitchResult);
        }
    }
}
