using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Agy;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.StarCliProxy;

public sealed class StarCliProxyAccountBridgeTests
{
    private const string MissingUtilityBlocker =
        "agy-profile utility was not found; install it from https://github.com/haclongkim/agy-profile";

    [Fact]
    public void Constructor_RejectsNullContextManager()
    {
        Assert.Throws<ArgumentNullException>(() => new StarCliProxyAccountBridge(null!));
    }

    [Fact]
    public void BackendId_IsStarCliProxy()
    {
        var bridge = CreateBridge(new AccountContextManager(new FakeAgyProfileService { IsAvailable = false }));

        Assert.Equal("star-cliproxy", bridge.BackendId);
    }

    [Fact]
    public void SupportsPinning_FollowsValidatedAccountContexts()
    {
        var empty = CreateBridge(new AccountContextManager(new FakeAgyProfileService { IsAvailable = false }));

        Assert.False(empty.SupportsPinning);
        Assert.False(empty.SupportsObservedRoute);

        var withUtility = CreateBridge(new AccountContextManager(new FakeAgyProfileService { IsAvailable = true }));

        Assert.True(withUtility.SupportsPinning);
        Assert.True(withUtility.SupportsObservedRoute);
    }

    [Fact]
    public async Task DiscoverAccounts_WhenNoContexts_ReturnsEmpty()
    {
        var bridge = CreateBridge(new AccountContextManager(new FakeAgyProfileService { IsAvailable = false }));

        var accounts = await bridge.DiscoverAccountsAsync("prov-star");

        Assert.Empty(accounts);
    }

    [Fact]
    public async Task DiscoverAccounts_CombinesCodexHomesAndAgyProfilesWithPrefixedIds()
    {
        using var codexHome = new TemporaryDirectory();

        var manager = new AccountContextManager(new FakeAgyProfileService
        {
            IsAvailable = true,
            ActiveProfile = "work",
            Profiles =
            [
                new AgyProfileSummary("work", IsActive: true),
                new AgyProfileSummary("personal", IsActive: false)
            ]
        });

        manager.RegisterCodexContext(new CodexAccountContext("codex-work", codexHome.Root, "Codex Work"));

        var bridge = CreateBridge(manager);

        var accounts = await bridge.DiscoverAccountsAsync("prov-star");

        Assert.Equal(3, accounts.Count);

        var codex = accounts.Single(account => account.Id == "codex:codex-work");
        Assert.Equal("Codex Work", codex.DisplayName);
        Assert.Equal(codexHome.Root, codex.ProviderNativeId);
        Assert.Equal(AuthState.Unknown, codex.AuthState);

        var activeAgy = accounts.Single(account => account.Id == "agy:work");
        Assert.True(activeAgy.IsDefault);
        Assert.Equal(AuthState.Unknown, activeAgy.AuthState);

        var inactiveAgy = accounts.Single(account => account.Id == "agy:personal");
        Assert.False(inactiveAgy.IsDefault);
        Assert.Equal(AuthState.Unknown, inactiveAgy.AuthState);
    }

    [Fact]
    public async Task DiscoverAccounts_CodexHomeMissing_MarksAuthUnknown()
    {
        using var codexHome = new TemporaryDirectory();
        var missing = Path.Combine(codexHome.Root, "missing");

        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        manager.RegisterCodexContext(new CodexAccountContext("work", missing));

        var accounts = await CreateBridge(manager).DiscoverAccountsAsync("prov-star");

        var account = Assert.Single(accounts);
        Assert.Equal(AuthState.Unknown, account.AuthState);
    }

    [Theory]
    [InlineData("codex:work", AccountContextKind.Codex, "work")]
    [InlineData("agy:work", AccountContextKind.Agy, "work")]
    [InlineData("CODEX:work", AccountContextKind.Codex, "work")]
    [InlineData("agy:", null, null)]
    [InlineData("opencode:work", null, null)]
    [InlineData("", null, null)]
    public void TryParseAccountId_ClassifiesIds(
        string accountId,
        AccountContextKind? expectedKind,
        string? expectedNativeId)
    {
        var parsed = StarCliProxyAccountBridge.TryParseAccountId(accountId, out var kind, out var nativeId);

        Assert.Equal(expectedKind is not null, parsed);

        if (expectedKind is not null)
        {
            Assert.Equal(expectedKind, kind);
            Assert.Equal(expectedNativeId, nativeId);
        }
    }

    [Fact]
    public async Task PinAccount_CodexContext_BindsSelectedHomeAndCarriesOverrides()
    {
        using var codexHome = new TemporaryDirectory();

        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        manager.RegisterCodexContext(new CodexAccountContext("codex-work", codexHome.Root));

        var bridge = CreateBridge(manager);
        var binding = CreateBinding(accountId: "codex:codex-work", modelId: "gpt-5.5");

        var result = await bridge.PinAccountAsync("prov-star", "codex:codex-work", binding);

        Assert.True(result.IsPinned);
        Assert.NotNull(result.ConfirmedBinding);
        Assert.NotSame(binding, result.ConfirmedBinding);
        Assert.Equal(BackendType.StarCliProxy, result.ConfirmedBinding!.Backend);
        Assert.Equal("codex:codex-work", result.ConfirmedBinding.AccountId);
        Assert.Equal("gpt-5.5", result.ConfirmedBinding.ModelId);
        Assert.Equal("high", result.ConfirmedBinding.ReasoningEffort);
        Assert.Equal("fast", result.ConfirmedBinding.SpeedMode);
        Assert.Equal("accept-edits", result.ConfirmedBinding.ExecutionMode);

        // Codex context selection does not change the account context, so no session reset is required.
        Assert.False(result.RequiresNewSession);

        // The original binding is never rewritten in place.
        Assert.Equal("codex:codex-work", binding.AccountId);
    }

    [Fact]
    public async Task PinAccount_AgyContext_SwitchesProfileOnlyOnceUnderSerializedLock()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "personal" };
        var manager = new AccountContextManager(service);

        var bridge = CreateBridge(manager);
        var binding = CreateBinding(accountId: "agy:personal");

        var result = await bridge.PinAccountAsync("prov-star", "agy:work", binding);

        Assert.True(result.IsPinned);
        Assert.Equal(new[] { "work" }, service.SwitchCalls);
        Assert.Equal("agy:work", result.ConfirmedBinding!.AccountId);
        Assert.Equal(BackendType.StarCliProxy, result.ConfirmedBinding.Backend);

        // Invariant (ТЗ §6.11a): an AGY profile switch must surface RequiresNewSession so the
        // caller resets the native session id instead of carrying it to the new profile.
        Assert.True(result.RequiresNewSession);
    }

    [Fact]
    public async Task PinAccount_AgyContext_AlreadyActive_DoesNotRequireNewSession()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "work" };
        var manager = new AccountContextManager(service);

        var bridge = CreateBridge(manager);
        var binding = CreateBinding(accountId: "agy:work");

        var result = await bridge.PinAccountAsync("prov-star", "agy:work", binding);

        Assert.True(result.IsPinned);
        Assert.Empty(service.SwitchCalls);
        Assert.False(result.RequiresNewSession);
    }

    [Theory]
    [InlineData("codex:personal", "prov-star", "codex:work")]
    [InlineData("codex:work", "other-provider-profile", "codex:work")]
    [InlineData("agy:personal", "prov-star", "agy:work")]
    [InlineData("agy:work", "other-provider-profile", "agy:work")]
    public async Task PinAccount_ChangedBindingIdentityRequiresNewSessionEvenWithoutNativeSwitch(
        string previousAccountId, string previousProfileId, string targetAccountId)
    {
        using var codexHome = new TemporaryDirectory();
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "work" };
        var manager = new AccountContextManager(service);
        manager.RegisterCodexContext(new CodexAccountContext("work", codexHome.Root));
        var previous = new SessionBinding(BackendType.StarCliProxy, previousProfileId, previousAccountId,
            "model", "high", "fast", "accept-edits");

        var result = await CreateBridge(manager).PinAccountAsync("prov-star", targetAccountId, previous);

        Assert.True(result.IsPinned);
        Assert.True(result.RequiresNewSession);
        Assert.Equal("prov-star", result.ConfirmedBinding!.ProviderProfileId);
        Assert.Equal(targetAccountId, result.ConfirmedBinding.AccountId);
        Assert.NotSame(previous, result.ConfirmedBinding);
        Assert.Equal(previous.ModelId, result.ConfirmedBinding.ModelId);
        Assert.Equal(previous.ReasoningEffort, result.ConfirmedBinding.ReasoningEffort);
        Assert.Equal(previous.SpeedMode, result.ConfirmedBinding.SpeedMode);
        Assert.Equal(previous.ExecutionMode, result.ConfirmedBinding.ExecutionMode);
        Assert.Equal(previousAccountId, previous.AccountId);
        Assert.Equal(previousProfileId, previous.ProviderProfileId);
        Assert.Empty(service.SwitchCalls);
    }

    [Fact]
    public async Task PinAccount_AgyContext_SwitchFailure_ReturnsFailure()
    {
        var service = new FakeAgyProfileService
        {
            IsAvailable = true,
            ActiveProfile = "personal",
            SwitchResult = AgyProfileSwitchResult.Failure("agy is running; switch refused")
        };

        var bridge = CreateBridge(new AccountContextManager(service));
        var binding = CreateBinding(accountId: "agy:personal");

        var result = await bridge.PinAccountAsync("prov-star", "agy:work", binding);

        Assert.False(result.IsPinned);
        Assert.Contains("agy is running", result.FailureReason);
        Assert.Null(result.ConfirmedBinding);
    }

    [Fact]
    public async Task PinAccount_UnknownCodexAccount_FailsClosed()
    {
        var bridge = CreateBridge(new AccountContextManager(new FakeAgyProfileService { IsAvailable = false }));

        var result = await bridge.PinAccountAsync(
            "prov-star",
            "codex:missing",
            CreateBinding(accountId: "codex:missing"));

        Assert.False(result.IsPinned);
        Assert.Contains("absolute CODEX_HOME", result.FailureReason);
    }

    [Fact]
    public async Task PinAccount_OpaqueAccountId_FailsClosed()
    {
        var bridge = CreateBridge(new AccountContextManager(new FakeAgyProfileService { IsAvailable = false }));

        var result = await bridge.PinAccountAsync(
            "prov-star",
            "plain-account",
            CreateBinding(accountId: "plain-account"));

        Assert.False(result.IsPinned);
        Assert.Contains("not a star-cliproxy account context id", result.FailureReason);
    }

    [Fact]
    public async Task PinAccount_RejectsNonStarCliProxyBackendBinding()
    {
        var bridge = CreateBridge(new AccountContextManager(new FakeAgyProfileService { IsAvailable = true }));

        var result = await bridge.PinAccountAsync(
            "prov-star",
            "agy:work",
            CreateBinding(accountId: "agy:work", backend: BackendType.OpenCode));

        Assert.False(result.IsPinned);
        Assert.Contains("BackendType.StarCliProxy", result.FailureReason);
    }

    [Fact]
    public async Task PinAccount_RejectsNullArguments()
    {
        var bridge = CreateBridge(new AccountContextManager(new FakeAgyProfileService { IsAvailable = true }));
        var binding = CreateBinding();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => bridge.PinAccountAsync(null!, "agy:work", binding));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => bridge.PinAccountAsync("prov-star", null!, binding));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => bridge.PinAccountAsync("prov-star", "agy:work", null!));
    }

    [Fact]
    public async Task ProbeAuth_WhenGatewayUnavailable_ReturnsUnknownWithBlocker()
    {
        var probe = new FakeAvailabilityProbe { IsAvailable = false, AvailabilityBlocker = "install star-cliproxy" };

        var bridge = new StarCliProxyAccountBridge(
            new AccountContextManager(new FakeAgyProfileService { IsAvailable = true }),
            probe);

        var result = await bridge.ProbeAuthAsync("prov-star", "agy:work");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.Contains("install star-cliproxy", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAuth_CodexContextResolved_ReturnsUnknownWithoutNativeAuthProof()
    {
        using var codexHome = new TemporaryDirectory();

        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        manager.RegisterCodexContext(new CodexAccountContext("codex-work", codexHome.Root));

        var result = await CreateBridge(manager).ProbeAuthAsync("prov-star", "codex:codex-work");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.Contains("native authentication", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAuth_CodexHomeMissing_ReturnsUnknown()
    {
        using var codexHome = new TemporaryDirectory();

        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        manager.RegisterCodexContext(new CodexAccountContext("codex-work", Path.Combine(codexHome.Root, "missing")));

        var result = await CreateBridge(manager).ProbeAuthAsync("prov-star", "codex:codex-work");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.Contains("does not exist", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAuth_AgyInactive_ReturnsUnknownWithoutSwitching()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "personal" };
        var bridge = CreateBridge(new AccountContextManager(service));

        var result = await bridge.ProbeAuthAsync("prov-star", "agy:work");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.Contains("not the active profile", result.ErrorMessage);
        Assert.Empty(service.SwitchCalls);
    }

    [Fact]
    public async Task ProbeAuth_AgyActive_ReturnsUnknownWithoutNativeAuthProof()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "work" };
        var bridge = CreateBridge(new AccountContextManager(service));

        var result = await bridge.ProbeAuthAsync("prov-star", "agy:work");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.Contains("native authentication", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAuth_UtilityUnavailable_ReturnsUnknownWithInstallUrl()
    {
        var bridge = CreateBridge(new AccountContextManager(new FakeAgyProfileService { IsAvailable = false }));

        var result = await bridge.ProbeAuthAsync("prov-star", "agy:work");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.Contains("https://github.com/haclongkim/agy-profile", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAuth_OpaqueAccountId_ReturnsUnknown()
    {
        var bridge = CreateBridge(new AccountContextManager(new FakeAgyProfileService { IsAvailable = true }));

        var result = await bridge.ProbeAuthAsync("prov-star", "opaque");

        Assert.Equal(AuthState.Unknown, result.State);
        Assert.Contains("not a star-cliproxy account context id", result.ErrorMessage);
    }

    private static StarCliProxyAccountBridge CreateBridge(IAccountContextManager manager, bool gatewayAvailable = true)
    {
        return new StarCliProxyAccountBridge(
            manager,
            new FakeAvailabilityProbe { IsAvailable = gatewayAvailable });
    }

    private static SessionBinding CreateBinding(
        string accountId = "agy:work",
        BackendType backend = BackendType.StarCliProxy,
        string modelId = "gemini-3.1-pro")
    {
        return new SessionBinding(
            backend,
            "prov-star",
            accountId,
            modelId,
            "high",
            "fast",
            "accept-edits");
    }

    private sealed class FakeAvailabilityProbe : IStarCliProxyAvailabilityProbe
    {
        public bool IsAvailable { get; set; } = true;

        public string? AvailabilityBlocker { get; set; }
    }

    private sealed class FakeAgyProfileService : IAgyProfileService
    {
        public bool IsAvailable { get; set; } = true;

        public string? ExecutablePath { get; set; } = @"C:\tools\agy-profile.cmd";

        public string? AvailabilityBlocker => IsAvailable ? null : MissingUtilityBlocker;

        public string? ActiveProfile { get; set; } = "personal";

        public IReadOnlyList<AgyProfileSummary> Profiles { get; set; } =
        [
            new AgyProfileSummary("personal", IsActive: false),
            new AgyProfileSummary("work", IsActive: false)
        ];

        public AgyProfileSwitchResult SwitchResult { get; set; } = AgyProfileSwitchResult.Success("work");

        public List<string> SwitchCalls { get; } = new();

        public Task<string?> GetActiveProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ActiveProfile);

        public Task<IReadOnlyList<AgyProfileSummary>> ListProfilesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Profiles);

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
