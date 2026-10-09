using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Application.Tests.Quotas;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Routing;

public sealed class RoutingEngineTests
{
    [Theory]
    [InlineData(RoutingPolicy.Balanced, QuotaProvenance.Stale)]
    [InlineData(RoutingPolicy.Balanced, QuotaProvenance.Estimated)]
    [InlineData(RoutingPolicy.QuotaFirst, QuotaProvenance.Estimated)]
    [InlineData(RoutingPolicy.PriorityFirst, QuotaProvenance.Estimated)]
    public async Task ExpiredQuotaCannotInfluenceSelectionDespiteEstimatedOptIn(RoutingPolicy policy,
        QuotaProvenance provenance)
    {
        var engine = CreateEngine();
        foreach (var id in new[] { "acc-a", "acc-b" })
        {
            await _accountRepo.SaveAsync(new Account(id, "prov-1", id, null,
                AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));
            await _snapshotRepo.SaveAsync(new QuotaSnapshot("snapshot-" + id, id, provenance,
                _timeProvider.GetUtcNow().AddMinutes(-30),
                [new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000,
                    id == "acc-a" ? 900 : 100, id == "acc-a" ? 100 : 900)], "prov-1"));
        }
        var decision = await engine.SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode, ProviderProfileId = "prov-1", ModelId = "model",
            Policy = policy, OptInEstimatedQuota = true
        });
        // QuotaFirst/Balanced may refuse an expired-only catalog. If a non-quota fallback remains
        // eligible, stale remaining must not outrank the deterministic account-ID tie break.
        if (decision.IsSuccess)
        {
            Assert.Equal("acc-a", decision.SelectedAccount!.Id);
            if (decision.Score is { } score)
            {
                Assert.Equal(0d, score.QuotaScore);
                Assert.Equal(0d, score.ReserveScore);
            }
        }
        else Assert.Null(decision.SelectedBinding);
    }

    [Fact]
    public async Task MissingProfileRepositoryCannotAuthorizeEvenAnEnabledPublicAccount()
    {
        var accounts = new InMemoryAccountRepository();
        await accounts.SaveAsync(new Account("account", "provider", "Account", null,
            AuthState.Valid, 0, true, HealthState.Healthy, null, null, 1, null));
        var engine = new RoutingEngine(accounts, new InMemoryQuotaSnapshotRepository());
        var decision = await engine.SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode, ProviderProfileId = "provider", ModelId = "model",
            Policy = RoutingPolicy.ManualOnly, PinnedAccountId = "account"
        });
        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedBinding);
    }

    private readonly TestTimeProvider _timeProvider = new();
    private readonly InMemoryAccountRepository _accountRepo = new();
    private readonly InMemoryQuotaSnapshotRepository _snapshotRepo = new();
    private readonly InMemoryProviderProfileRepository _profileRepo = new();

    [Fact]
    public async Task LimitedGrokBotCannotEnterAutomaticRouting()
    {
        var decision = await CreateEngine().SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.NativeGateway,
            ProviderProfileId = LLMWorkGUI.Application.Providers.GrokBotRestrictions.ProviderProfileId,
            ModelId = "grok-bot", Policy = RoutingPolicy.QuotaFirst
        });
        Assert.False(decision.IsSuccess);
        Assert.Contains("вручную", decision.ExplanationText);
    }

    [Fact]
    public async Task ModelSpecificQuotaCannotAuthorizeAnotherModel()
    {
        await _accountRepo.SaveAsync(new Account("account", "provider", "Account", null,
            AuthState.Valid, 0, true, HealthState.Healthy, null, null, 1, null));
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("model-b-quota", "account",
            QuotaProvenance.ExactProviderReported, _timeProvider.GetUtcNow(),
            [new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 200, 800)],
            providerProfileId: "provider", modelId: "model-b", expiresAt: _timeProvider.GetUtcNow().AddMinutes(5)));
        var decision = await CreateEngine().SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode, ProviderProfileId = "provider", ModelId = "model-a",
            Policy = RoutingPolicy.QuotaFirst
        });
        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedBinding);
    }

    private RoutingEngine CreateEngine(
        BalancedScoringWeights? weights = null,
        IEnumerable<IAccountBridge>? accountBridges = null,
        bool withProfileRepository = false)
    {
        var profiles = _profileRepo;
        if (!withProfileRepository)
        {
            profiles = new InMemoryProviderProfileRepository();
            foreach (var id in new[] { "prov-1", "provider", "prov-empty" })
                profiles.Save(new ProviderProfile(id, id, BackendType.OpenCode, null, null, DataClassification.Restricted, true));
        }
        return new RoutingEngine(
            _accountRepo,
            _snapshotRepo,
            weights is not null ? Options.Create(weights) : null,
            _timeProvider,
            logger: null,
            providerProfileRepository: profiles,
            accountBridges: accountBridges);
    }

    [Fact]
    public void BalancedScoringWeights_Validate_ThrowsWhenSumNotOne()
    {
        var weights = new BalancedScoringWeights
        {
            QuotaWeight = 0.5,
            HealthWeight = 0.5,
            PriorityWeight = 0.5 // sum = 1.5
        };

        Assert.Throws<InvalidOperationException>(() => weights.Validate());
    }

    [Fact]
    public async Task SelectRouteAsync_NoAccountsConfigured_ReturnsFailure()
    {
        var engine = CreateEngine();
        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-empty",
            ModelId = "gpt-4o"
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Contains("No accounts configured", decision.ExplanationText);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("backend-mismatch")]
    public async Task SelectRouteAsync_WithProfileRepository_RejectsUnavailableOrMismatchedProfile(string scenario)
    {
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account", null,
            AuthState.Valid, 0, true, HealthState.Healthy, null, null, 1, null));
        if (scenario != "missing")
        {
            _profileRepo.Save(new ProviderProfile("prov-1", "Provider",
                scenario == "backend-mismatch" ? BackendType.Agy : BackendType.OpenCode,
                null, null, DataClassification.PrivateSource, scenario != "disabled"));
        }

        var decision = await CreateEngine(withProfileRepository: true).SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode, ProviderProfileId = "prov-1", ModelId = "model",
            Policy = RoutingPolicy.PriorityFirst
        });

        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedBinding);
        Assert.Contains("profile", decision.ExplanationText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("reasoning")]
    [InlineData("speed")]
    [InlineData("execution")]
    public async Task SelectRouteAsync_StickyTupleMismatch_RequiresReplacementWithoutReturningOldRoute(string mismatch)
    {
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account", null,
            AuthState.Valid, 0, true, HealthState.Healthy, null, null, 1, null));
        var binding = new SessionBinding(
            mismatch == "backend" ? BackendType.Agy : BackendType.OpenCode,
            mismatch == "provider" ? "other-provider" : "prov-1", "acc-1",
            mismatch == "model" ? "other-model" : "model",
            mismatch == "reasoning" ? "high" : null,
            mismatch == "speed" ? "fast" : null,
            mismatch == "execution" ? "other-mode" : null);

        var decision = await CreateEngine().SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode, ProviderProfileId = "prov-1", ModelId = "model",
            Policy = RoutingPolicy.SessionSticky, ExistingStickyBinding = binding
        });

        Assert.False(decision.IsSuccess);
        Assert.True(decision.RequiresReplacementSession);
        Assert.Null(decision.SelectedBinding);
    }

    [Theory]
    [InlineData("quota")]
    [InlineData("health")]
    [InlineData("priority")]
    [InlineData("load")]
    [InlineData("latency")]
    [InlineData("reserve")]
    public void BalancedScoringWeights_RejectsNaNInEveryComponent(string component)
    {
        var weights = new BalancedScoringWeights
        {
            QuotaWeight = component == "quota" ? double.NaN : 0.30,
            HealthWeight = component == "health" ? double.NaN : 0.25,
            PriorityWeight = component == "priority" ? double.NaN : 0.15,
            LoadWeight = component == "load" ? double.NaN : 0.10,
            LatencyWeight = component == "latency" ? double.NaN : 0.10,
            ReserveWeight = component == "reserve" ? double.NaN : 0.10
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => weights.Validate());
    }

    [Fact]
    public async Task SelectRouteAsync_NegativeActiveExecutionCount_IsNotTreatedAsFreeCapacity()
    {
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account", null,
            AuthState.Valid, 0, true, HealthState.Healthy, null, null, 1, null));

        var decision = await CreateEngine().SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode, ProviderProfileId = "prov-1", ModelId = "model",
            Policy = RoutingPolicy.PriorityFirst,
            ActiveExecutionsPerAccount = new Dictionary<string, int> { ["acc-1"] = -1 }
        });

        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedBinding);
        Assert.Contains("negative", Assert.Single(decision.RejectedCandidates).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectRouteAsync_PinnedPolicy_SelectsRequestedPinnedAccount()
    {
        var engine = CreateEngine();
        var acc1 = new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        var acc2 = new Account("acc-2", "prov-1", "Account 2", null, AuthState.Valid, 20, true, HealthState.Healthy, null, null, 2, null);
        await _accountRepo.SaveAsync(acc1);
        await _accountRepo.SaveAsync(acc2);

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Pinned,
            PolicySource = "ExplicitUser",
            PinnedAccountId = "acc-1"
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.NotNull(decision.SelectedAccount);
        Assert.Equal("acc-1", decision.SelectedAccount!.Id);
        Assert.Equal("acc-1", decision.SelectedBinding!.AccountId);
        Assert.Contains("Pinned policy", decision.ExplanationText);
    }

    [Fact]
    public async Task SelectRouteAsync_PinnedPolicy_AccountDisabled_FailsWithoutFailover()
    {
        var engine = CreateEngine();
        var acc1 = new Account("acc-1", "prov-1", "Account 1 Disabled", null, AuthState.Valid, 10, false, HealthState.Healthy, null, null, 2, null);
        var acc2 = new Account("acc-2", "prov-1", "Account 2 Enabled", null, AuthState.Valid, 20, true, HealthState.Healthy, null, null, 2, null);
        await _accountRepo.SaveAsync(acc1);
        await _accountRepo.SaveAsync(acc2);

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Pinned,
            PinnedAccountId = "acc-1"
        };

        var decision = await engine.SelectRouteAsync(request);

        // Pinned must never failover to acc-2!
        Assert.False(decision.IsSuccess);
        Assert.Contains("Automatic failover is forbidden", decision.ExplanationText);
    }

    [Fact]
    public async Task SelectRouteAsync_SessionSticky_ReusesExistingBindingWhenEligible()
    {
        var engine = CreateEngine();
        var acc1 = new Account("acc-sticky", "prov-1", "Sticky Acc", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        await _accountRepo.SaveAsync(acc1);

        var stickyBinding = new SessionBinding(BackendType.OpenCode, "prov-1", "acc-sticky", "gpt-4o", null, null, null);
        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.SessionSticky,
            ExistingStickyBinding = stickyBinding
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-sticky", decision.SelectedBinding!.AccountId);
        Assert.False(decision.RequiresReplacementSession);
        Assert.Contains("Reusing confirmed sticky binding", decision.ExplanationText);
    }

    [Fact]
    public async Task SelectRouteAsync_SessionSticky_IneligibleAccount_StopsTurnAndRequiresReplacement()
    {
        var engine = CreateEngine();
        // Account has Invalid AuthState
        var acc1 = new Account("acc-sticky", "prov-1", "Sticky Acc Invalid", null, AuthState.Invalid, 10, true, HealthState.Healthy, null, null, 2, null);
        await _accountRepo.SaveAsync(acc1);

        var stickyBinding = new SessionBinding(BackendType.OpenCode, "prov-1", "acc-sticky", "gpt-4o", null, null, null);
        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.SessionSticky,
            ExistingStickyBinding = stickyBinding
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.True(decision.RequiresReplacementSession);
        Assert.Contains("Turn stopped; user confirmation required to create replacement session", decision.ExplanationText);
    }

    [Fact]
    public async Task SelectRouteAsync_HardReserveViolation_RejectsCandidate()
    {
        var engine = CreateEngine();
        var acc = new Account("acc-res", "prov-1", "Reserve Violator", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        await _accountRepo.SaveAsync(acc);

        var now = _timeProvider.GetUtcNow();
        // Remaining 50 <= HardReserve 100
        var bucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 950, 50, null, hardReserve: 100);
        var snap = new QuotaSnapshot("snap-res", "acc-res", QuotaProvenance.ExactProviderReported, now, new[] { bucket }, "prov-1");
        await _snapshotRepo.SaveAsync(snap);

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.QuotaFirst
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Single(decision.RejectedCandidates);
        Assert.Contains("Hard reserve threshold violated", decision.RejectedCandidates[0].Reason);
    }

    [Fact]
    public async Task SelectRouteAsync_QuotaFirst_UntrustedOrStaleQuota_RejectsCandidate()
    {
        var engine = CreateEngine();
        var acc = new Account("acc-untrusted", "prov-1", "Unsupported Quota Acc", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        await _accountRepo.SaveAsync(acc);

        var snap = QuotaSnapshot.CreateUnsupported("acc-untrusted", "prov-1");
        await _snapshotRepo.SaveAsync(snap);

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.QuotaFirst
        };

        var decision = await engine.SelectRouteAsync(request);

        // QuotaFirst strictly rejects Unsupported per ТЗ §6.6
        Assert.False(decision.IsSuccess);
        Assert.Contains("cannot be used for automatic scoring without fresh trusted quota", decision.RejectedCandidates[0].Reason);
    }

    [Fact]
    public async Task SelectRouteAsync_PriorityFirst_SortsByPriorityAndTieBreaks()
    {
        var engine = CreateEngine();
        var accLow = new Account("acc-low", "prov-1", "Low Priority", null, AuthState.Valid, 5, true, HealthState.Healthy, null, null, 2, null);
        var accHigh1 = new Account("acc-high1", "prov-1", "High Priority 1", null, AuthState.Valid, 50, true, HealthState.Healthy, null, null, 2, null);
        var accHigh2 = new Account("acc-high2", "prov-1", "High Priority 2", null, AuthState.Valid, 50, true, HealthState.Healthy, null, null, 2, null);

        await _accountRepo.SaveAsync(accLow);
        await _accountRepo.SaveAsync(accHigh1);
        await _accountRepo.SaveAsync(accHigh2);

        var now = _timeProvider.GetUtcNow();
        // High2 has more remaining than High1 (3000 vs 1000)
        var bucket1 = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 5000, 4000, 1000);
        var bucket2 = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 5000, 2000, 3000);

        await _snapshotRepo.SaveAsync(new QuotaSnapshot("s1", "acc-high1", QuotaProvenance.ExactProviderReported, now, new[] { bucket1 }, "prov-1"));
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("s2", "acc-high2", QuotaProvenance.ExactProviderReported, now, new[] { bucket2 }, "prov-1"));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        // Priority tie (50 == 50) is broken by remaining value (3000 > 1000) -> acc-high2 wins!
        Assert.Equal("acc-high2", decision.SelectedAccount!.Id);
        Assert.Contains("Tie broken by trusted remaining value", decision.ExplanationText);
        Assert.Equal("s2", decision.QuotaSnapshotId);
    }

    [Fact]
    public async Task SelectRouteAsync_Balanced_ComputesWeightedScoreAndSelectsHighest()
    {
        var engine = CreateEngine();
        var accA = new Account("acc-a", "prov-1", "Account A", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        var accB = new Account("acc-b", "prov-1", "Account B", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        await _accountRepo.SaveAsync(accA);
        await _accountRepo.SaveAsync(accB);

        var now = _timeProvider.GetUtcNow();
        // Acc A: 90% remaining quota (900/1000)
        var bucketA = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900, null, 50);
        // Acc B: 30% remaining quota (300/1000)
        var bucketB = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 700, 300, null, 50);

        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-a", "acc-a", QuotaProvenance.ExactProviderReported, now, new[] { bucketA }, "prov-1"));
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-b", "acc-b", QuotaProvenance.ExactProviderReported, now, new[] { bucketB }, "prov-1"));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Balanced
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-a", decision.SelectedAccount!.Id);
        Assert.NotNull(decision.Score);
        Assert.True(decision.Score!.TotalScore > 70.0);
        Assert.Equal("snap-a", decision.QuotaSnapshotId);
    }

    [Fact]
    public async Task SelectRouteAsync_ConcurrencyLimitReached_RejectsCandidate()
    {
        var engine = CreateEngine();
        // MaxConcurrentExecutions = 1
        var acc = new Account("acc-busy", "prov-1", "Busy Account", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 1, null);
        await _accountRepo.SaveAsync(acc);

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst,
            ActiveExecutionsPerAccount = new Dictionary<string, int> { ["acc-busy"] = 1 }
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Single(decision.RejectedCandidates);
        Assert.Contains("Concurrency limit reached", decision.RejectedCandidates[0].Reason);
    }

    [Fact]
    public async Task SelectRouteAsync_ProjectDataClassExceedsProviderMaxDataClass_FailsClosed()
    {
        var engine = CreateEngine(withProfileRepository: true);
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));
        _profileRepo.Save(new ProviderProfile("prov-1", "Public Provider", BackendType.OpenCode, null, null, DataClassification.PublicSource, true));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Balanced,
            ProjectDataClass = DataClassification.PrivateSource
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedAccount);
        Assert.Contains("Data classification violation", decision.ExplanationText);
        Assert.Contains("PublicSource", decision.ExplanationText);
    }

    [Fact]
    public async Task SelectRouteAsync_RestrictedProject_AutomaticPolicy_FailsClosed()
    {
        var engine = CreateEngine(withProfileRepository: true);
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));
        _profileRepo.Save(new ProviderProfile("prov-1", "Restricted Provider", BackendType.OpenCode, null, null, DataClassification.Restricted, true));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst,
            ProjectDataClass = DataClassification.Restricted
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedAccount);
        Assert.Contains("Restricted project data", decision.ExplanationText);
        Assert.Contains("ManualOnly", decision.ExplanationText);
    }

    [Fact]
    public async Task SelectRouteAsync_RestrictedProject_AutomaticPolicy_FailsClosedWithoutProfileRepository()
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.QuotaFirst,
            ProjectDataClass = DataClassification.Restricted
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedAccount);
        Assert.Contains("Restricted project data", decision.ExplanationText);
    }

    [Fact]
    public async Task SelectRouteAsync_RestrictedProject_ManualOnly_IsAllowed()
    {
        var engine = CreateEngine(withProfileRepository: true);
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));
        _profileRepo.Save(new ProviderProfile("prov-1", "Restricted Provider", BackendType.OpenCode, null, null, DataClassification.Restricted, true));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.ManualOnly,
            PinnedAccountId = "acc-1",
            ProjectDataClass = DataClassification.Restricted
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-1", decision.SelectedAccount!.Id);
    }

    [Fact]
    public async Task SelectRouteAsync_BlankModelId_RejectsCandidate()
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "  ",
            Policy = RoutingPolicy.PriorityFirst
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Contains("Requested model ID is blank", decision.RejectedCandidates[0].Reason);
    }

    [Fact]
    public async Task SelectRouteAsync_OpaqueBridge_AutomaticPolicy_FailsClosed()
    {
        var engine = CreateEngine(accountBridges: new[] { new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: false) });
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Balanced
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedAccount);
        Assert.Contains("Fail-Closed", decision.ExplanationText);
        Assert.Contains("ManualOnly", decision.ExplanationText);
    }

    [Fact]
    public async Task SelectRouteAsync_OpaqueBridge_PinnedPolicy_FailsClosed()
    {
        var engine = CreateEngine(accountBridges: new[] { new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: false) });
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Pinned,
            PinnedAccountId = "acc-1"
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Contains("Fail-Closed", decision.ExplanationText);
    }

    [Fact]
    public async Task SelectRouteAsync_OpaqueBridge_ManualOnly_IsAllowed()
    {
        var engine = CreateEngine(accountBridges: new[] { new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: false) });
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.ManualOnly,
            PinnedAccountId = "acc-1"
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-1", decision.SelectedAccount!.Id);
    }

    [Fact]
    public async Task SelectRouteAsync_DeclaredOpenCodeContractWithoutNativeAdapter_IsRefusedInAutomaticModes()
    {
        var engine = CreateEngine(accountBridges: new[] { new OpenCodePluginAccountBridge(hasMultiAccountPluginContract: true) });
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedAccount);
        Assert.Contains("Fail-Closed", decision.ExplanationText);
    }

    [Fact]
    public async Task SelectRouteAsync_ImplementedSyntheticBridge_IsAllowedInAutomaticModes()
    {
        var engine = CreateEngine(accountBridges: new[] { new ImplementedSyntheticOpenCodeBridge() });
        await _accountRepo.SaveAsync(new Account("acc-1", "prov-1", "Account 1", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));
        var decision = await engine.SelectRouteAsync(new RouteSelectionRequest
        { Backend = BackendType.OpenCode, ProviderProfileId = "prov-1", ModelId = "gpt-4o", Policy = RoutingPolicy.PriorityFirst });
        Assert.True(decision.IsSuccess); Assert.Equal("acc-1", decision.SelectedAccount!.Id);
    }

    private sealed class ImplementedSyntheticOpenCodeBridge : IAccountBridge
    {
        private readonly MockAccountBridge _inner = new();
        public string BackendId => "opencode";
        public bool SupportsPinning => _inner.SupportsPinning;
        public bool SupportsObservedRoute => _inner.SupportsObservedRoute;
        public Task<IReadOnlyList<DiscoveredAccountInfo>> DiscoverAccountsAsync(string profile, CancellationToken token = default) => _inner.DiscoverAccountsAsync(profile, token);
        public Task<AccountPinResult> PinAccountAsync(string profile, string account, LLMWorkGUI.Domain.ValueObjects.SessionBinding binding, CancellationToken token = default) => _inner.PinAccountAsync(profile, account, binding, token);
        public Task<AccountAuthProbeResult> ProbeAuthAsync(string profile, string account, CancellationToken token = default) => _inner.ProbeAuthAsync(profile, account, token);
    }

    [Fact]
    public async Task SelectRouteAsync_Balanced_TrustedFreshQuotaOutranksHigherEstimatedQuota()
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-trusted", "prov-1", "Trusted Acc", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));
        await _accountRepo.SaveAsync(new Account("acc-estimated", "prov-1", "Estimated Acc", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var now = _timeProvider.GetUtcNow();
        var trustedBucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 200, 800);
        var estimatedBucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900, confidence: QuotaConfidence.Low);

        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-trusted", "acc-trusted", QuotaProvenance.ExactProviderReported, now, new[] { trustedBucket }, "prov-1", null, now.AddMinutes(10)));
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-estimated", "acc-estimated", QuotaProvenance.LocallyCalculated, now, new[] { estimatedBucket }, "prov-1", null, now.AddMinutes(10)));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Balanced,
            OptInEstimatedQuota = true
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-trusted", decision.SelectedAccount!.Id);
        Assert.Equal("snap-trusted", decision.QuotaSnapshotId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectRouteAsync_PriorityFirst_ManualPriorityOutranksQuotaTrust(bool optInEstimatedQuota)
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-trusted", "prov-1", "Trusted Acc", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));
        await _accountRepo.SaveAsync(new Account("acc-estimated", "prov-1", "Estimated Acc", null, AuthState.Valid, 50, true, HealthState.Healthy, null, null, 2, null));

        var now = _timeProvider.GetUtcNow();
        var trustedBucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 900, 100);
        var estimatedBucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900, confidence: QuotaConfidence.Low);

        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-trusted", "acc-trusted", QuotaProvenance.ExactProviderReported, now, new[] { trustedBucket }, "prov-1", null, now.AddMinutes(10)));
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-estimated", "acc-estimated", QuotaProvenance.LocallyCalculated, now, new[] { estimatedBucket }, "prov-1", null, now.AddMinutes(10)));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst,
            OptInEstimatedQuota = optInEstimatedQuota
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-estimated", decision.SelectedAccount!.Id);
    }

    [Fact]
    public async Task SelectRouteAsync_Balanced_EstimatedQuotaWithoutLimit_GetsZeroQuotaScore()
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-nolimit", "prov-1", "No Limit Acc", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var now = _timeProvider.GetUtcNow();
        var bucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, limitValue: null, usedValue: null, remainingValue: 500, confidence: QuotaConfidence.Low);
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-nolimit", "acc-nolimit", QuotaProvenance.LocallyCalculated, now, new[] { bucket }, "prov-1", null, now.AddMinutes(10)));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Balanced,
            OptInEstimatedQuota = true
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.NotNull(decision.Score);
        Assert.Equal(0.0, decision.Score!.QuotaScore);
    }

    [Fact]
    public async Task SelectRouteAsync_Balanced_UnmeasuredLatencyDoesNotOutrankMeasuredAccount()
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-measured", "prov-1", "Measured Acc", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));
        await _accountRepo.SaveAsync(new Account("acc-unmeasured", "prov-1", "Unmeasured Acc", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var now = _timeProvider.GetUtcNow();
        var bucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 200, 800);
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-measured", "acc-measured", QuotaProvenance.ExactProviderReported, now, new[] { bucket }, "prov-1", null, now.AddMinutes(10)));
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-unmeasured", "acc-unmeasured", QuotaProvenance.ExactProviderReported, now, new[] { bucket }, "prov-1", null, now.AddMinutes(10)));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Balanced,
            LatencyEmaPerAccount = new Dictionary<string, double> { ["acc-measured"] = 250.0 }
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-measured", decision.SelectedAccount!.Id);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]
    public async Task SelectRouteAsync_Balanced_InvalidLatencyDoesNotOutrankMeasuredAccount(double invalidLatency)
    {
        foreach (var id in new[] { "acc-measured", "acc-invalid" })
        {
            await _accountRepo.SaveAsync(new Account(id, "prov-1", id, null,
                AuthState.Valid, 0, true, HealthState.Healthy, null, null, 2, null));
            var bucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 200, 800);
            await _snapshotRepo.SaveAsync(new QuotaSnapshot($"snap-{id}", id,
                QuotaProvenance.ExactProviderReported, _timeProvider.GetUtcNow(), new[] { bucket },
                "prov-1", null, _timeProvider.GetUtcNow().AddMinutes(10)));
        }

        var decision = await CreateEngine().SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode, ProviderProfileId = "prov-1", ModelId = "model",
            Policy = RoutingPolicy.Balanced,
            LatencyEmaPerAccount = new Dictionary<string, double>
            {
                ["acc-measured"] = 250.0,
                ["acc-invalid"] = invalidLatency
            }
        });

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-measured", decision.SelectedAccount!.Id);
        Assert.True(double.IsFinite(decision.Score!.TotalScore));
    }

    [Fact]
    public async Task SelectRouteAsync_HardReserveViolationOnStaleSnapshot_DoesNotBlockCandidate()
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-stale-reserve", "prov-1", "Stale Reserve Acc", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var now = _timeProvider.GetUtcNow();
        var bucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 950, 50, null, hardReserve: 100);
        await _snapshotRepo.SaveAsync(new QuotaSnapshot(
            "snap-stale-reserve",
            "acc-stale-reserve",
            QuotaProvenance.Stale,
            now.AddMinutes(-30),
            new[] { bucket },
            "prov-1",
            null,
            now.AddMinutes(-10)));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-stale-reserve", decision.SelectedAccount!.Id);
    }

    [Theory]
    [InlineData(QuotaProvenance.Stale)]
    [InlineData(QuotaProvenance.ExactProviderReported)]
    public async Task SelectRouteAsync_PriorityFirst_StaleOrExpiredSnapshots_TieBreakByAccountIdNotStaleRemaining(QuotaProvenance provenance)
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-a", "prov-1", "Stale A", null, AuthState.Valid, 50, true, HealthState.Healthy, null, null, 2, null));
        await _accountRepo.SaveAsync(new Account("acc-b", "prov-1", "Stale B", null, AuthState.Valid, 50, true, HealthState.Healthy, null, null, 2, null));

        var now = _timeProvider.GetUtcNow();
        // TTL-expired snapshots (captured 30 minutes ago, TTL is 5 minutes).
        var bucketA = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 900, 100);
        var bucketB = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900);

        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-a", "acc-a", provenance, now.AddMinutes(-30), new[] { bucketA }, "prov-1"));
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-b", "acc-b", provenance, now.AddMinutes(-30), new[] { bucketB }, "prov-1"));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        // Stale/TTL-expired remaining values must never drive the tie-break: acc-a wins by account ID ASC.
        Assert.Equal("acc-a", decision.SelectedAccount!.Id);
        Assert.Contains("Tie broken by account ID", decision.ExplanationText);
        Assert.DoesNotContain("remaining", decision.ExplanationText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectRouteAsync_PriorityFirst_EstimatedQuotaWithoutOptIn_DoesNotUseRemaining()
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-a", "prov-1", "Estimated A", null, AuthState.Valid, 50, true, HealthState.Healthy, null, null, 2, null));
        await _accountRepo.SaveAsync(new Account("acc-b", "prov-1", "Estimated B", null, AuthState.Valid, 50, true, HealthState.Healthy, null, null, 2, null));

        var now = _timeProvider.GetUtcNow();
        var bucketA = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 900, 100, confidence: QuotaConfidence.Low);
        var bucketB = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900, confidence: QuotaConfidence.Low);

        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-a", "acc-a", QuotaProvenance.LocallyCalculated, now, new[] { bucketA }, "prov-1", null, now.AddMinutes(10)));
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-b", "acc-b", QuotaProvenance.LocallyCalculated, now, new[] { bucketB }, "prov-1", null, now.AddMinutes(10)));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-a", decision.SelectedAccount!.Id);
        Assert.Contains("Tie broken by account ID", decision.ExplanationText);
        Assert.DoesNotContain("remaining", decision.ExplanationText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectRouteAsync_PriorityFirst_OptInEstimatedQuota_TieBreaksByEstimatedRemaining()
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-a", "prov-1", "Estimated A", null, AuthState.Valid, 50, true, HealthState.Healthy, null, null, 2, null));
        await _accountRepo.SaveAsync(new Account("acc-b", "prov-1", "Estimated B", null, AuthState.Valid, 50, true, HealthState.Healthy, null, null, 2, null));

        var now = _timeProvider.GetUtcNow();
        var bucketA = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 900, 100, confidence: QuotaConfidence.Low);
        var bucketB = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900, confidence: QuotaConfidence.Low);

        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-a", "acc-a", QuotaProvenance.LocallyCalculated, now, new[] { bucketA }, "prov-1", null, now.AddMinutes(10)));
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-b", "acc-b", QuotaProvenance.LocallyCalculated, now, new[] { bucketB }, "prov-1", null, now.AddMinutes(10)));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst,
            OptInEstimatedQuota = true
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-b", decision.SelectedAccount!.Id);
        Assert.Contains("Tie broken by estimated remaining value", decision.ExplanationText);
        Assert.DoesNotContain("trusted remaining", decision.ExplanationText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectRouteAsync_Balanced_MissingNumericQuotaData_GetsZeroReserveScore()
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-nodata", "prov-1", "No Numeric Data", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var now = _timeProvider.GetUtcNow();
        var bucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, limitValue: null, usedValue: null, remainingValue: 500, confidence: QuotaConfidence.Low);
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-nodata", "acc-nodata", QuotaProvenance.LocallyCalculated, now, new[] { bucket }, "prov-1", null, now.AddMinutes(10)));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Balanced,
            OptInEstimatedQuota = true
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.NotNull(decision.Score);
        Assert.Equal(0.0, decision.Score!.QuotaScore);
        Assert.Equal(0.0, decision.Score.ReserveScore);
    }

    [Fact]
    public async Task SelectRouteAsync_Balanced_MeasuredLowRemainingOutranksMissingNumericData()
    {
        var engine = CreateEngine();
        await _accountRepo.SaveAsync(new Account("acc-measured", "prov-1", "Measured Low", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));
        await _accountRepo.SaveAsync(new Account("acc-nodata", "prov-1", "No Numeric Data", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var now = _timeProvider.GetUtcNow();
        var measuredBucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 950, 50, confidence: QuotaConfidence.Low);
        var noDataBucket = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, limitValue: null, usedValue: null, remainingValue: 500, confidence: QuotaConfidence.Low);

        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-measured", "acc-measured", QuotaProvenance.LocallyCalculated, now, new[] { measuredBucket }, "prov-1", null, now.AddMinutes(10)));
        await _snapshotRepo.SaveAsync(new QuotaSnapshot("snap-nodata", "acc-nodata", QuotaProvenance.LocallyCalculated, now, new[] { noDataBucket }, "prov-1", null, now.AddMinutes(10)));

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-1",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Balanced,
            OptInEstimatedQuota = true
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-measured", decision.SelectedAccount!.Id);
        Assert.NotNull(decision.Score);
        Assert.Equal(5.0, decision.Score!.ReserveScore);
    }
}

internal sealed class InMemoryProviderProfileRepository : IProviderProfileRepository
{
    private readonly Dictionary<string, ProviderProfile> _profiles = new(StringComparer.Ordinal);

    /// <summary>
    /// Opaque secret reference returned for every profile. Tests set it to prove that only the
    /// reference travels outwards and never a resolved key.
    /// </summary>
    public string? SecretReference { get; set; }

    public void Save(ProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profiles[profile.Id] = profile;
    }

    public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ProviderProfile>>(_profiles.Values.ToList());
    }

    public Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        _profiles.TryGetValue(id, out var profile);
        return Task.FromResult(profile);
    }

    public Task UpsertAsync(ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default)
    {
        Save(profile);
        return Task.CompletedTask;
    }

    public Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SecretReference);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_profiles.Remove(id));
    }
}
