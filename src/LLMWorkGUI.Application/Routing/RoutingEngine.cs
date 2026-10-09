using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Application.Routing;

/// <summary>
/// Multi-account routing engine implementing strict eligibility gates, deterministic scoring,
/// normative tie-breaking, and route explanation (ТЗ §4.2, §6.4, §6.5, §6.6, ADR-0004 §6).
/// </summary>
public sealed class RoutingEngine : IRoutingEngine
{
    private readonly TimeSpan _trustedQuotaTtl;

    private readonly IAccountRepository _accountRepository;
    private readonly IQuotaSnapshotRepository _snapshotRepository;
    private readonly IProviderProfileRepository? _providerProfileRepository;
    private readonly IHealthCenterService? _healthCenter;
    private readonly ISecretLifecycleService? _secretLifecycle;
    private readonly ConfiguredRouteModelEligibility? _modelEligibility;
    private readonly IReadOnlyList<IAccountBridge> _accountBridges;
    private readonly BalancedScoringWeights _weights;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RoutingEngine>? _logger;

    public RoutingEngine(
        IAccountRepository accountRepository,
        IQuotaSnapshotRepository snapshotRepository,
        IOptions<BalancedScoringWeights>? weights = null,
        TimeProvider? timeProvider = null,
        ILogger<RoutingEngine>? logger = null,
        IProviderProfileRepository? providerProfileRepository = null,
        IEnumerable<IAccountBridge>? accountBridges = null,
        IHealthCenterService? healthCenter = null,
        ISecretLifecycleService? secretLifecycle = null,
        ConfiguredRouteModelEligibility? modelEligibility = null,
        IOptions<QuotaSchedulerOptions>? quotaOptions = null)
    {
        _accountRepository = accountRepository ?? throw new ArgumentNullException(nameof(accountRepository));
        _snapshotRepository = snapshotRepository ?? throw new ArgumentNullException(nameof(snapshotRepository));
        _weights = weights?.Value ?? new BalancedScoringWeights();
        _weights.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _providerProfileRepository = providerProfileRepository;
        _accountBridges = accountBridges?.ToArray() ?? Array.Empty<IAccountBridge>();
        _healthCenter = healthCenter;
        _secretLifecycle = secretLifecycle;
        _modelEligibility = modelEligibility;
        _trustedQuotaTtl = quotaOptions?.Value.DefaultTtl ?? TimeSpan.FromMinutes(5);
        if (_trustedQuotaTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quotaOptions),
                _trustedQuotaTtl,
                "Quota snapshot TTL must be positive.");
        }
    }

    public async Task<RoutingDecision> SelectRouteAsync(
        RouteSelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _timeProvider.GetUtcNow();

        // Fail-Closed gate: unverified opaque plugins without a pin/observed-route contract are
        // allowed only in ManualOnly mode (multi-account-routing-contract.json §opaqueRoutePolicy).
        var opaqueBridge = _accountBridges.FirstOrDefault(bridge => IsBridgeForBackend(bridge, request.Backend));
        if (opaqueBridge is not null &&
            (!opaqueBridge.SupportsPinning || !opaqueBridge.SupportsObservedRoute) &&
            request.Policy != RoutingPolicy.ManualOnly)
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = request.Policy,
                PolicySource = request.PolicySource,
                ExplanationText = $"Fail-Closed: account bridge '{opaqueBridge.BackendId}' for backend '{request.Backend}' does not support pinning and/or observed route evidence. " +
                                  "Unverified opaque plugins are allowed only in ManualOnly mode (multi-account-routing-contract.json §opaqueRoutePolicy); automatic failover and account switching are forbidden.",
                DecidedAt = now
            };
        }

        // Restricted projects are forbidden from automatic routing and dispatch; only ManualOnly is permitted (ТЗ §6.5, ADR-0004 §6.2).
        if (request.ProjectDataClass == DataClassification.Restricted && request.Policy != RoutingPolicy.ManualOnly)
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = request.Policy,
                PolicySource = request.PolicySource,
                ExplanationText = $"Restricted project data is forbidden from automatic routing and dispatch via policy '{request.Policy}'; only ManualOnly is permitted (ТЗ §6.5, ADR-0004 §6.2).",
                DecidedAt = now
            };
        }

        if (request.ProviderProfileId == LLMWorkGUI.Application.Providers.GrokBotRestrictions.ProviderProfileId
            && request.Policy is not (RoutingPolicy.ManualOnly or RoutingPolicy.Pinned))
        {
            return new RoutingDecision
            {
                IsSuccess = false, Policy = request.Policy, PolicySource = request.PolicySource,
                ExplanationText = "Grok Bot — ограниченный провайдер для ревью; выберите его вручную. Автоматический выбор и failover недоступны.",
                DecidedAt = now
            };
        }

        var providerProfile = _providerProfileRepository is not null && !string.IsNullOrWhiteSpace(request.ProviderProfileId)
            ? await _providerProfileRepository.GetByIdAsync(request.ProviderProfileId, cancellationToken).ConfigureAwait(false)
            : null;

        var metadataDecision = ProviderDataPolicy.Evaluate(request.ProjectDataClass, request.ProviderProfileId, providerProfile, request.Backend);
        if (!metadataDecision.IsAllowed)
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = request.Policy,
                PolicySource = request.PolicySource,
                ExplanationText = metadataDecision.Explanation!,
                DecidedAt = now
            };
        }

        var allAccounts = await _accountRepository
            .ListByProviderProfileIdAsync(request.ProviderProfileId, cancellationToken)
            .ConfigureAwait(false);

        if (allAccounts.Count == 0)
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = request.Policy,
                PolicySource = request.PolicySource,
                ExplanationText = $"No accounts configured for provider profile '{request.ProviderProfileId}'.",
                DecidedAt = now
            };
        }

        // The profile-level credential is resolved once per selection instead of per candidate: it is
        // the fallback for every account of the profile that has no credential of its own.
        var profileSecretReference = await ResolveProfileSecretReferenceAsync(request.ProviderProfileId, cancellationToken)
            .ConfigureAwait(false);
        var modelDecision = _modelEligibility is null
            ? null
            : await _modelEligibility.LoadAsync(request, cancellationToken).ConfigureAwait(false);

        // 1. Policy: SessionSticky with existing binding
        if (request.Policy == RoutingPolicy.SessionSticky && request.ExistingStickyBinding is not null)
        {
            return await HandleSessionStickyAsync(request, allAccounts, now, profileSecretReference, modelDecision, cancellationToken).ConfigureAwait(false);
        }

        // 2. Policy: Pinned & ManualOnly
        if (request.Policy is RoutingPolicy.Pinned or RoutingPolicy.ManualOnly)
        {
            return await HandlePinnedOrManualAsync(request, allAccounts, now, profileSecretReference, modelDecision, cancellationToken).ConfigureAwait(false);
        }

        // 3. Automatic modes: QuotaFirst, PriorityFirst, Balanced (and initial SessionSticky)
        return await HandleAutomaticRoutingAsync(request, allAccounts, now, profileSecretReference, modelDecision, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveProfileSecretReferenceAsync(
        string providerProfileId,
        CancellationToken cancellationToken)
    {
        if (_providerProfileRepository is null)
        {
            return null;
        }

        var reference = await _providerProfileRepository
            .GetApiKeySecretReferenceAsync(providerProfileId, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(reference) ? null : reference;
    }

    private async Task<RoutingDecision> HandleSessionStickyAsync(
        RouteSelectionRequest request,
        IReadOnlyList<Account> allAccounts,
        DateTimeOffset now,
        string? profileSecretReference,
        RoutingModelDecision? modelDecision,
        CancellationToken cancellationToken)
    {
        var stickyBinding = request.ExistingStickyBinding!;
        if (stickyBinding.Backend != request.Backend ||
            stickyBinding.ProviderProfileId != request.ProviderProfileId ||
            stickyBinding.ModelId != request.ModelId ||
            stickyBinding.ReasoningEffort != request.ReasoningEffort ||
            stickyBinding.SpeedMode != request.SpeedMode ||
            stickyBinding.ExecutionMode != request.ExecutionMode)
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = RoutingPolicy.SessionSticky,
                PolicySource = request.PolicySource,
                RequiresReplacementSession = true,
                ExplanationText = "Sticky binding differs from the requested route or options. Turn stopped; replacement session required.",
                DecidedAt = now
            };
        }

        var stickyAccountId = stickyBinding.AccountId;
        var stickyAccount = allAccounts.FirstOrDefault(a => a.Id == stickyAccountId);

        if (stickyAccount is null)
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = RoutingPolicy.SessionSticky,
                PolicySource = request.PolicySource,
                RequiresReplacementSession = true,
                ExplanationText = $"Sticky session account '{stickyAccountId}' was not found in provider '{request.ProviderProfileId}'. Turn stopped; replacement session required.",
                DecidedAt = now
            };
        }

        var (isEligible, rejectionReason, snapshot) = await EvaluateAccountEligibilityAsync(
            stickyAccount,
            request,
            now,
            allowUntrustedQuota: true, // Sticky sessions keep binding if quota is untrusted, unless hard reserve violation
            cancellationToken,
            profileSecretReference,
            modelDecision).ConfigureAwait(false);

        if (!isEligible)
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = RoutingPolicy.SessionSticky,
                PolicySource = request.PolicySource,
                RequiresReplacementSession = true,
                QuotaSnapshotId = snapshot?.Id,
                ExplanationText = $"Sticky account '{stickyAccount.DisplayName}' ({stickyAccount.Id}) is no longer eligible: {rejectionReason}. Turn stopped; user confirmation required to create replacement session (ТЗ §6.5).",
                DecidedAt = now
            };
        }

        return new RoutingDecision
        {
            IsSuccess = true,
            SelectedBinding = request.ExistingStickyBinding,
            SelectedAccount = stickyAccount,
            QuotaSnapshotId = snapshot?.Id,
            Policy = RoutingPolicy.SessionSticky,
            PolicySource = request.PolicySource,
            ExplanationText = $"Reusing confirmed sticky binding with account '{stickyAccount.DisplayName}' ({stickyAccount.Id}).",
            DecidedAt = now
        };
    }

    private async Task<RoutingDecision> HandlePinnedOrManualAsync(
        RouteSelectionRequest request,
        IReadOnlyList<Account> allAccounts,
        DateTimeOffset now,
        string? profileSecretReference,
        RoutingModelDecision? modelDecision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PinnedAccountId))
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = request.Policy,
                PolicySource = request.PolicySource,
                ExplanationText = $"{request.Policy} policy requires PinnedAccountId to be specified.",
                DecidedAt = now
            };
        }

        var targetAccount = allAccounts.FirstOrDefault(a => a.Id == request.PinnedAccountId);
        if (targetAccount is null)
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = request.Policy,
                PolicySource = request.PolicySource,
                ExplanationText = $"Target account '{request.PinnedAccountId}' not found. Automatic failover is forbidden (ТЗ §6.5).",
                DecidedAt = now
            };
        }

        var (isEligible, rejectionReason, snapshot) = await EvaluateAccountEligibilityAsync(
            targetAccount,
            request,
            now,
            allowUntrustedQuota: true, // Pinned and ManualOnly allow untrusted/unverified quota with warning
            cancellationToken,
            profileSecretReference,
            modelDecision).ConfigureAwait(false);

        if (!isEligible)
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = request.Policy,
                PolicySource = request.PolicySource,
                QuotaSnapshotId = snapshot?.Id,
                ExplanationText = $"Target account '{targetAccount.DisplayName}' ({targetAccount.Id}) failed eligibility: {rejectionReason}. Automatic failover is forbidden for {request.Policy} (ТЗ §6.5).",
                DecidedAt = now
            };
        }

        var binding = new SessionBinding(
            request.Backend,
            request.ProviderProfileId,
            targetAccount.Id,
            request.ModelId,
            request.ReasoningEffort,
            request.SpeedMode,
            request.ExecutionMode);

        var warning = snapshot is not null && !snapshot.IsTrusted
            ? $" (Warning: quota state is '{snapshot.Provenance}')"
            : string.Empty;

        return new RoutingDecision
        {
            IsSuccess = true,
            SelectedBinding = binding,
            SelectedAccount = targetAccount,
            QuotaSnapshotId = snapshot?.Id,
            Policy = request.Policy,
            PolicySource = request.PolicySource,
            ExplanationText = $"Route selected explicitly for account '{targetAccount.DisplayName}' ({targetAccount.Id}) via {request.Policy} policy{warning}.",
            DecidedAt = now
        };
    }

    private async Task<RoutingDecision> HandleAutomaticRoutingAsync(
        RouteSelectionRequest request,
        IReadOnlyList<Account> allAccounts,
        DateTimeOffset now,
        string? profileSecretReference,
        RoutingModelDecision? modelDecision,
        CancellationToken cancellationToken)
    {
        var eligibleCandidates = new List<EvaluatedCandidate>();
        var rejectedExplanations = new List<RejectedCandidateExplanation>();

        var requireFreshTrustedQuota = request.Policy is RoutingPolicy.QuotaFirst or RoutingPolicy.Balanced;

        foreach (var account in allAccounts)
        {
            var (isEligible, rejectionReason, snapshot) = await EvaluateAccountEligibilityAsync(
                account,
                request,
                now,
                allowUntrustedQuota: !requireFreshTrustedQuota,
                cancellationToken,
                profileSecretReference,
                modelDecision).ConfigureAwait(false);

            if (!isEligible)
            {
                rejectedExplanations.Add(new RejectedCandidateExplanation(account.Id, rejectionReason!));
                continue;
            }

            var activeExecutions = request.ActiveExecutionsPerAccount is not null &&
                                  request.ActiveExecutionsPerAccount.TryGetValue(account.Id, out var count)
                ? count
                : 0;

            double? latencyMs = request.LatencyEmaPerAccount is not null &&
                                request.LatencyEmaPerAccount.TryGetValue(account.Id, out var latency)
                ? latency
                : null;

            var isTrustedFreshQuota = snapshot is not null &&
                                      snapshot.IsTrusted &&
                                      snapshot.IsFresh(now, _trustedQuotaTtl);

            eligibleCandidates.Add(new EvaluatedCandidate(account, snapshot, activeExecutions, latencyMs,
                isTrustedFreshQuota, request.OptInEstimatedQuota && snapshot?.IsFresh(now, _trustedQuotaTtl) == true));
        }

        if (eligibleCandidates.Count == 0)
        {
            return new RoutingDecision
            {
                IsSuccess = false,
                Policy = request.Policy,
                PolicySource = request.PolicySource,
                RejectedCandidates = rejectedExplanations,
                ExplanationText = $"No eligible accounts found for provider '{request.ProviderProfileId}'. All {allAccounts.Count} account(s) failed eligibility gates.",
                DecidedAt = now
            };
        }

        // Rank candidates based on policy
        EvaluatedCandidate selected;
        CandidateScore? selectedScore = null;
        string tieBreakReason = string.Empty;

        if (request.Policy == RoutingPolicy.PriorityFirst)
        {
            var ranked = eligibleCandidates
                .OrderByDescending(c => c.Account.ManualPriority)
                .ThenByDescending(c => c.IsTrustedFreshQuota)
                .ThenByDescending(c => c.PrimaryRemaining)
                .ThenBy(c => c.Account.Id, StringComparer.Ordinal)
                .ToList();

            selected = ranked[0];
            if (ranked.Count > 1 &&
                ranked[0].IsTrustedFreshQuota == ranked[1].IsTrustedFreshQuota &&
                ranked[0].Account.ManualPriority == ranked[1].Account.ManualPriority)
            {
                tieBreakReason = ranked[0].PrimaryRemaining != ranked[1].PrimaryRemaining
                    ? $"Tie broken by {RemainingSourceLabel(ranked[0])} remaining value ({ranked[0].PrimaryRemaining} vs {ranked[1].PrimaryRemaining})"
                    : $"Tie broken by account ID ({ranked[0].Account.Id} vs {ranked[1].Account.Id})";
            }
        }
        else if (request.Policy == RoutingPolicy.QuotaFirst)
        {
            var ranked = eligibleCandidates
                .OrderByDescending(c => c.IsTrustedFreshQuota)
                .ThenByDescending(c => c.PrimaryRemaining)
                .ThenByDescending(c => c.Account.ManualPriority)
                .ThenBy(c => c.Account.Id, StringComparer.Ordinal)
                .ToList();

            selected = ranked[0];
            if (ranked.Count > 1 &&
                ranked[0].IsTrustedFreshQuota == ranked[1].IsTrustedFreshQuota &&
                Math.Abs((ranked[0].PrimaryRemaining ?? 0.0) - (ranked[1].PrimaryRemaining ?? 0.0)) < 0.001)
            {
                tieBreakReason = ranked[0].Account.ManualPriority != ranked[1].Account.ManualPriority
                    ? $"Tie broken by manual priority ({ranked[0].Account.ManualPriority} vs {ranked[1].Account.ManualPriority})"
                    : $"Tie broken by account ID ({ranked[0].Account.Id} vs {ranked[1].Account.Id})";
            }
        }
        else // Balanced (default) or initial SessionSticky
        {
            var scoredList = eligibleCandidates.Select(c =>
            {
                var score = ComputeBalancedScore(c);
                return (Candidate: c, Score: score);
            })
            .OrderByDescending(x => x.Candidate.IsTrustedFreshQuota)
            .ThenByDescending(x => x.Score.TotalScore)
            .ThenByDescending(x => x.Candidate.Account.ManualPriority)
            .ThenByDescending(x => x.Candidate.PrimaryRemaining)
            .ThenBy(x => x.Candidate.Account.Id, StringComparer.Ordinal)
            .ToList();

            selected = scoredList[0].Candidate;
            selectedScore = scoredList[0].Score;

            if (scoredList.Count > 1 &&
                scoredList[0].Candidate.IsTrustedFreshQuota == scoredList[1].Candidate.IsTrustedFreshQuota &&
                Math.Abs(scoredList[0].Score.TotalScore - scoredList[1].Score.TotalScore) < 0.05)
            {
                if (scoredList[0].Candidate.Account.ManualPriority != scoredList[1].Candidate.Account.ManualPriority)
                {
                    tieBreakReason = $"Tie broken by manual priority ({scoredList[0].Candidate.Account.ManualPriority} vs {scoredList[1].Candidate.Account.ManualPriority})";
                }
                else if (Math.Abs((scoredList[0].Candidate.PrimaryRemaining ?? 0.0) - (scoredList[1].Candidate.PrimaryRemaining ?? 0.0)) >= 0.001)
                {
                    tieBreakReason = $"Tie broken by {RemainingSourceLabel(scoredList[0].Candidate)} remaining ({scoredList[0].Candidate.PrimaryRemaining} vs {scoredList[1].Candidate.PrimaryRemaining})";
                }
                else
                {
                    tieBreakReason = $"Tie broken by account ID ({scoredList[0].Candidate.Account.Id} vs {scoredList[1].Candidate.Account.Id})";
                }
            }
        }

        var chosenBinding = new SessionBinding(
            request.Backend,
            request.ProviderProfileId,
            selected.Account.Id,
            request.ModelId,
            request.ReasoningEffort,
            request.SpeedMode,
            request.ExecutionMode);

        var scoreText = selectedScore is not null ? $" {selectedScore}." : string.Empty;
        var tieBreakText = !string.IsNullOrWhiteSpace(tieBreakReason) ? $" [{tieBreakReason}]." : string.Empty;

        var explanation = $"Selected account '{selected.Account.DisplayName}' ({selected.Account.Id}) via policy '{request.Policy}' from {request.PolicySource}.{scoreText}{tieBreakText}";

        return new RoutingDecision
        {
            IsSuccess = true,
            SelectedBinding = chosenBinding,
            SelectedAccount = selected.Account,
            QuotaSnapshotId = selected.Snapshot?.Id,
            Policy = request.Policy,
            PolicySource = request.PolicySource,
            Score = selectedScore,
            RejectedCandidates = rejectedExplanations,
            ExplanationText = explanation,
            DecidedAt = now
        };
    }

    private async Task<(bool IsEligible, string? RejectionReason, QuotaSnapshot? Snapshot)> EvaluateAccountEligibilityAsync(
        Account account,
        RouteSelectionRequest request,
        DateTimeOffset now,
        bool allowUntrustedQuota,
        CancellationToken cancellationToken,
        string? profileSecretReference = null,
        RoutingModelDecision? modelDecision = null)
    {
        // Gate 1: Account enabled
        if (!account.IsEnabled)
        {
            return (false, "Account is disabled", null);
        }

        // Gate 2: Auth state
        if (account.AuthState != AuthState.Valid)
        {
            return (false, $"Account auth state is {account.AuthState}", null);
        }

        // Gate 3: Credential the account would actually authenticate with.
        // An account's own reference is the more specific one, and the profile reference is the
        // fallback. A reference that is Missing or Revoked can never authenticate a request, so the
        // account must stay out of routing until the value is entered again (ADR-0005 §5.2). An
        // account with no reference at all is a keyless provider and is left alone.
        var credentialRejection = await EvaluateSecretReferenceAsync(account, profileSecretReference, cancellationToken)
            .ConfigureAwait(false);

        if (credentialRejection is not null)
        {
            return (false, credentialRejection, null);
        }

        // Gate 4: Active cooldown
        if (account.CooldownUntil.HasValue && account.CooldownUntil.Value > now)
        {
            return (false, $"Account is in cooldown until {account.CooldownUntil.Value:O}", null);
        }

        // Gate 5: Active disabled until
        if (account.DisabledUntil.HasValue && account.DisabledUntil.Value > now)
        {
            return (false, $"Account is disabled until {account.DisabledUntil.Value:O}", null);
        }

        // Gate 6: Health state.
        // The Health Center owns the normative answer to "may routing use this scope?" including
        // cooldown and probe-pending exclusion, so it is consulted first when it is available. The
        // stored Account.Health is the fallback for graphs without a Health Center, and it is still
        // applied afterwards so a persisted exclusion is never widened by a missing health record.
        var allowForcedRoute = request.OptInForcedRoute
            || request.Policy is RoutingPolicy.Pinned or RoutingPolicy.ManualOnly
            || (request.Policy == RoutingPolicy.SessionSticky && request.ExistingStickyBinding is not null);
        var healthRejection = await EvaluateHealthAsync(account, request.ModelId, allowForcedRoute, cancellationToken).ConfigureAwait(false);

        if (healthRejection is not null)
        {
            return (false, healthRejection, null);
        }

        // Gate 7: Concurrency slots
        var active = request.ActiveExecutionsPerAccount is not null &&
                     request.ActiveExecutionsPerAccount.TryGetValue(account.Id, out var count)
            ? count
            : 0;

        if (active < 0)
        {
            return (false, "Active execution count is negative and cannot establish free capacity", null);
        }

        if (active >= account.MaxConcurrentExecutions)
        {
            return (false, $"Concurrency limit reached ({active}/{account.MaxConcurrentExecutions} slots in use)", null);
        }

        // Gate 8: the requested model must be configured for this account, and a requested
        // reasoning effort, speed, or execution mode must match that saved route exactly.
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            return (false, "Requested model ID is blank and cannot be routed", null);
        }

        if (modelDecision?.Reject(account.Id) is { } modelRejection)
        {
            return (false, modelRejection, null);
        }

        // Gate 9: Quota Snapshot & Hard Reserve
        var snapshot = await _snapshotRepository
            .GetLatestForAccountAsync(account.Id, request.ModelId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null || snapshot.AccountId != account.Id || snapshot.ModelId != request.ModelId
            || (snapshot.ProviderProfileId is not null && snapshot.ProviderProfileId != request.ProviderProfileId))
        {
            snapshot = await _snapshotRepository
                .GetLatestAccountWideAsync(account.Id, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is not null && (snapshot.AccountId != account.Id || snapshot.ModelId is not null
                || (snapshot.ProviderProfileId is not null && snapshot.ProviderProfileId != request.ProviderProfileId)))
                snapshot = null;
        }

        // Numeric hard reserve applies only to fresh trusted (Exact/Plugin) snapshots (ТЗ §6.6).
        if (snapshot is not null &&
            snapshot.IsTrusted &&
            snapshot.IsFresh(now, _trustedQuotaTtl) &&
            snapshot.HasHardReserveViolation)
        {
            var violator = snapshot.Buckets.FirstOrDefault(b => b.HasHardReserveViolation);
            var detail = violator is not null
                ? $"bucket '{violator.BucketName}' remaining {violator.RemainingValue} <= reserve {violator.HardReserve}"
                : "reserve limit violated";

            return (false, $"Hard reserve threshold violated: {detail}", snapshot);
        }

        if (!allowUntrustedQuota)
        {
            if (snapshot is null)
            {
                return (false, "No quota snapshot available for automatic quota scoring", null);
            }

            var canScore = snapshot.CanCalculateNumericScore(now, _trustedQuotaTtl);
            if (!canScore)
            {
                if (request.OptInEstimatedQuota && snapshot.IsFresh(now, _trustedQuotaTtl) &&
                    (snapshot.Provenance == QuotaProvenance.LocallyCalculated || snapshot.Provenance == QuotaProvenance.Estimated))
                {
                    // User explicitly opted in to estimated/local quota
                }
                else
                {
                    return (false, $"Quota state is '{snapshot.Provenance}' and cannot be used for automatic scoring without fresh trusted quota (ТЗ §6.6)", snapshot);
                }
            }
        }

        return (true, null, snapshot);
    }

    /// <summary>
    /// Returns a rejection reason when the credential the account would authenticate with cannot be
    /// resolved, or <c>null</c> when it can. The account's own reference wins over the profile
    /// reference, which is the fallback for accounts without one. A reference that the lifecycle
    /// reports as anything other than active keeps the account out of routing, and a reference the
    /// lifecycle cannot classify is treated as unusable rather than assumed to work.
    /// </summary>
    private async Task<string?> EvaluateSecretReferenceAsync(
        Account account,
        string? profileSecretReference,
        CancellationToken cancellationToken)
    {
        var reference = string.IsNullOrWhiteSpace(account.SecretReference)
            ? profileSecretReference
            : account.SecretReference;

        if (string.IsNullOrWhiteSpace(reference))
        {
            // An explicitly keyless provider stays keyless: no reference was ever configured, so
            // there is nothing that could have been revoked or lost.
            return null;
        }

        if (_secretLifecycle is null)
        {
            return "Configured credentials cannot be verified because the secret lifecycle service is unavailable.";
        }

        var status = await _secretLifecycle.GetStatusAsync(reference, cancellationToken).ConfigureAwait(false);

        return status.IsUsable
            ? null
            : $"Secret reference is {status.State}: {status.DescribeState()} (ADR-0005 §5.2)";
    }

    /// <summary>
    /// Returns a rejection reason when the account or the requested model route must stay out of routing,
    /// or <c>null</c> when it may participate. The observed Health Center snapshots and the persisted
    /// <see cref="Account.Health"/> are all honoured: whichever excludes the scope wins, so a scope the
    /// Health Center has never observed is not silently promoted, and a quarantine it did observe is not
    /// silently ignored. The account scope blocks every model of the account (auth failure, account
    /// cooldown), while the model-route scope blocks only the requested model (model mismatch), because
    /// the stricter of the two states applies (ТЗ §6.10).
    /// </summary>
    private async Task<string?> EvaluateHealthAsync(
        Account account,
        string? modelId,
        bool allowForcedRoute,
        CancellationToken cancellationToken)
    {
        if (_healthCenter is not null)
        {
            var accountSnapshot = await _healthCenter
                .GetSnapshotAsync(HealthScope.ForAccount(account.Id), cancellationToken)
                .ConfigureAwait(false);

            if (!accountSnapshot.IsRoutable)
            {
                // Cooldown and probe-pending scopes are excluded here but not by Account.Health, which
                // is exactly why the normalized snapshot has to be consulted (ROADMAP Phase 7).
                var detail = accountSnapshot.RequiresProbe
                    ? " and a probe must pass before it can return to routing"
                    : string.Empty;

                return $"Health Center reports account state {accountSnapshot.State}, which is excluded from routing{detail}";
            }

            if (accountSnapshot.State == HealthState.ForcedEnabled && !allowForcedRoute)
            {
                return $"Health Center reports account state {accountSnapshot.State}, which is excluded from automatic routing until the request opts in";
            }

            if (!string.IsNullOrWhiteSpace(modelId))
            {
                var routeSnapshot = await _healthCenter
                    .GetSnapshotAsync(HealthScope.ForModelRoute(account.Id, modelId), cancellationToken)
                    .ConfigureAwait(false);

                if (!routeSnapshot.IsRoutable)
                {
                    // A model mismatch or route cooldown blocks only this model route; other models of
                    // the same account remain eligible (ТЗ §6.10).
                    var detail = routeSnapshot.RequiresProbe
                        ? " and a probe must pass before it can return to routing"
                        : string.Empty;

                    return $"Health Center reports model route '{modelId}' state {routeSnapshot.State}, which is excluded from routing{detail}";
                }

                if (routeSnapshot.State == HealthState.ForcedEnabled && !allowForcedRoute)
                {
                    return $"Health Center reports model route '{modelId}' state {routeSnapshot.State}, which is excluded from automatic routing until the request opts in";
                }
            }
        }

        if (account.Health == HealthState.ForcedEnabled && !allowForcedRoute)
        {
            return $"Account health state is {account.Health}, which is excluded from automatic routing until the request opts in";
        }

        if (account.Health is not (HealthState.Healthy or HealthState.Degraded or HealthState.ForcedEnabled))
        {
            return $"Account health state is {account.Health}";
        }

        return null;
    }

    private static bool IsBridgeForBackend(IAccountBridge bridge, BackendType backend) =>
        string.Equals(
            NormalizeBackendId(bridge.BackendId),
            NormalizeBackendId(backend.ToString()),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBackendId(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray());

    private static string RemainingSourceLabel(EvaluatedCandidate candidate) =>
        candidate.IsTrustedFreshQuota ? "trusted" : "estimated";

    private CandidateScore ComputeBalancedScore(EvaluatedCandidate candidate)
    {
        // 1. Quota score (0..100): remaining / limit of primary bucket.
        // Missing numeric quota data must never synthesize a midpoint score.
        double quotaScore = 0.0;
        if (candidate.HasScorableRemaining && candidate.Snapshot?.PrimaryBucket?.LimitValue > 0 &&
            candidate.Snapshot.PrimaryBucket.RemainingValue.HasValue)
        {
            var rem = candidate.Snapshot.PrimaryBucket.RemainingValue.Value;
            var lim = candidate.Snapshot.PrimaryBucket.LimitValue.Value;
            quotaScore = Math.Clamp((rem / lim) * 100.0, 0.0, 100.0);
        }

        // 2. Health score (0..100): Healthy=100, Degraded=50, Unhealthy=0
        double healthScore = candidate.Account.Health switch
        {
            HealthState.Healthy => 100.0,
            HealthState.Degraded => 50.0,
            _ => 0.0
        };

        // 3. Priority score (0..100): manual priority normalized
        double priorityScore = Math.Clamp(candidate.Account.ManualPriority * 10.0, 0.0, 100.0);

        // 4. Load score (0..100): free concurrency slots
        var max = Math.Max(1, candidate.Account.MaxConcurrentExecutions);
        var freeSlots = Math.Max(0, max - candidate.ActiveExecutions);
        double loadScore = Math.Clamp((freeSlots / (double)max) * 100.0, 0.0, 100.0);

        // 5. Latency score (0..100): inverse scale from 200ms to 2000ms.
        // Accounts without measured latency receive a neutral midpoint instead of an ideal score.
        double latencyScore = 50.0;
        if (candidate.LatencyMs is double measuredLatencyMs &&
            double.IsFinite(measuredLatencyMs) && measuredLatencyMs >= 0)
        {
            latencyScore = measuredLatencyMs > 200.0
                ? Math.Clamp(100.0 - ((measuredLatencyMs - 200.0) / 18.0), 0.0, 100.0)
                : 100.0;
        }

        // 6. Reserve score (0..100): distance above hard reserve.
        // Missing numeric limit/remaining data must never synthesize an ideal score.
        double reserveScore = 0.0;
        if (candidate.HasScorableRemaining && candidate.Snapshot?.PrimaryBucket?.LimitValue > 0 &&
            candidate.Snapshot.PrimaryBucket.RemainingValue.HasValue)
        {
            var bucket = candidate.Snapshot.PrimaryBucket;
            var rem = bucket.RemainingValue.Value;
            var lim = bucket.LimitValue.Value;
            var hardReserve = bucket.HardReserve ?? 0.0;
            var margin = rem - hardReserve;
            reserveScore = Math.Clamp((margin / lim) * 100.0, 0.0, 100.0);
        }

        var totalScore =
            (_weights.QuotaWeight * quotaScore) +
            (_weights.HealthWeight * healthScore) +
            (_weights.PriorityWeight * priorityScore) +
            (_weights.LoadWeight * loadScore) +
            (_weights.LatencyWeight * latencyScore) +
            (_weights.ReserveWeight * reserveScore);

        return new CandidateScore
        {
            TotalScore = totalScore,
            QuotaScore = quotaScore,
            HealthScore = healthScore,
            PriorityScore = priorityScore,
            LoadScore = loadScore,
            LatencyScore = latencyScore,
            ReserveScore = reserveScore
        };
    }

    private sealed record EvaluatedCandidate(
        Account Account,
        QuotaSnapshot? Snapshot,
        int ActiveExecutions,
        double? LatencyMs,
        bool IsTrustedFreshQuota,
        bool AllowEstimatedQuota)
    {
        // Numeric remaining is exposed only for trusted fresh snapshots or, when the user opted in,
        // for LocallyCalculated/Estimated snapshots. Stale/TTL-expired snapshots never leak numeric
        // values into ordering or tie-break explanations (ТЗ §6.5, §6.6, EC9).
        public bool HasScorableRemaining =>
            Snapshot?.PrimaryBucket?.RemainingValue is not null &&
            (IsTrustedFreshQuota ||
             (AllowEstimatedQuota &&
              Snapshot.Provenance is QuotaProvenance.LocallyCalculated or QuotaProvenance.Estimated));

        public double? PrimaryRemaining => HasScorableRemaining ? Snapshot!.PrimaryBucket!.RemainingValue : null;
    }
}
