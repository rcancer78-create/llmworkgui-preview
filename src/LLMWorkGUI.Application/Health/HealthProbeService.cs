using System.Collections.Concurrent;
using System.Text.Json;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Health;

/// <summary>
/// Admits supported probes durably before provider I/O and records their observed result. Preflight
/// refusals leave health untouched; admitted calls retain an audit even when interrupted, and only
/// the current attempt can recover its scope (ROADMAP Phase 7).
///
/// A passing connection probe confirms <em>only</em> that the endpoint answers: it never verifies a
/// recovery. Only the pinned model probe may do that, and it is the only probe the state machine lets
/// reach <see cref="HealthState.Healthy"/>.
/// </summary>
public sealed class HealthProbeService : IHealthProbeService
{
    private readonly IHealthCenterService _healthCenter;
    private readonly IProviderProfileRepository? _providerProfiles;
    private readonly IAccountRepository? _accounts;
    private readonly IProviderConnectionTestService? _connectionTest;
    private readonly IModelProbeExecutor? _modelProbe;
    private readonly ISecretLifecycleService? _secretLifecycle;

    /// <summary>
    /// One active marker per scope. A probe observes a single scope, so a second concurrent probe
    /// must be refused rather than allowed to interleave with the first.
    /// </summary>
    private readonly ConcurrentDictionary<HealthScope, byte> _activeProbes = new();
    private readonly ConcurrentDictionary<HealthScope, Func<CancellationToken, Task<HealthProbeOutcome>>> _pendingCompletions = new();

    public HealthProbeService(
        IHealthCenterService healthCenter,
        IProviderProfileRepository? providerProfiles = null,
        IAccountRepository? accounts = null,
        IProviderConnectionTestService? connectionTest = null,
        IModelProbeExecutor? modelProbe = null,
        ISecretLifecycleService? secretLifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(healthCenter);

        _healthCenter = healthCenter;
        _providerProfiles = providerProfiles;
        _accounts = accounts;
        _connectionTest = connectionTest;
        _modelProbe = modelProbe;
        _secretLifecycle = secretLifecycle;
    }

    public bool SupportsModelProbe => _modelProbe is not null;

    public async Task<int> RetryPendingCompletionsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var scope in _pendingCompletions.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_activeProbes.TryAdd(scope, 0)) continue;
            try
            {
                if (_pendingCompletions.TryGetValue(scope, out var pending))
                    await PersistPendingCompletionAsync(scope, pending).ConfigureAwait(false);
            }
            finally { _activeProbes.TryRemove(scope, out _); }
        }
        return _pendingCompletions.Count;
    }

    public async Task<HealthProbeOutcome> ProbeConnectionAsync(
        HealthScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // Two concurrent probes of one scope would interleave two observations and could record the
        // same recovery twice, so the second caller is refused instead of queued.
        if (!_activeProbes.TryAdd(scope, 0))
        {
            return Refuse(
                scope,
                await _healthCenter.GetSnapshotAsync(scope, cancellationToken).ConfigureAwait(false),
                HealthProbeKind.Connection,
                HealthProbeRefusal.ProbeAlreadyRunning,
                $"A probe is already running for {scope.ScopeType} '{scope.ScopeId}', so this probe was not started.");
        }

        try
        {
            if (_pendingCompletions.TryGetValue(scope, out var pending))
                return await PersistPendingCompletionAsync(scope, pending).ConfigureAwait(false);
            var observed = await _healthCenter.GetSnapshotAsync(scope, cancellationToken).ConfigureAwait(false);
            return await ExecuteConnectionProbeAsync(scope, observed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _activeProbes.TryRemove(scope, out _);
        }
    }

    private async Task<HealthProbeOutcome> ExecuteConnectionProbeAsync(
        HealthScope scope,
        HealthSnapshot observed,
        CancellationToken cancellationToken)
    {
        if (observed.AuthenticationFanoutPending)
            return Refuse(scope, observed, HealthProbeKind.Connection, HealthProbeRefusal.AuthenticationFanoutPending,
                "Authentication blocking is pending. Refresh the Health Center to retry it before probing.");
        // A scope that owes no probe must not get one: a pass would fabricate recovery evidence for a
        // route nobody asked to verify.
        if (observed.State != HealthState.ProbeRequired)
        {
            return Refuse(
                scope,
                observed,
                HealthProbeKind.Connection,
                HealthProbeRefusal.NoProbePending,
                $"The scope is {observed.State}, which is not awaiting a probe, so no probe was run.");
        }

        if (_connectionTest is null)
        {
            return Refuse(
                scope,
                observed,
                HealthProbeKind.Connection,
                HealthProbeRefusal.ProbeExecutorUnavailable,
                "No connection-test service is configured, so the probe could not be executed.");
        }

        var (settings, resolutionRefusal, resolutionDetail) =
            await ResolveTargetAsync(scope, requireHttpEndpoint: true, cancellationToken).ConfigureAwait(false);

        if (settings is null)
        {
            return Refuse(scope, observed, HealthProbeKind.Connection, resolutionRefusal, resolutionDetail);
        }

        var attempt = await _healthCenter.TryBeginProbeAttemptAsync(scope, modelProbe: false,
            "A connection probe was admitted before the provider call.", cancellationToken).ConfigureAwait(false);
        if (attempt is null)
            return Refuse(scope, await _healthCenter.GetSnapshotAsync(scope, cancellationToken).ConfigureAwait(false),
                HealthProbeKind.Connection, HealthProbeRefusal.NoProbePending,
                "The health state changed before admission, so no connection probe was sent.");

        ProviderConnectionTestResult result;
        try
        {
            result = await _connectionTest.TestConnectionAsync(settings, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RecordInterruptedProbeAsync(attempt, HealthProbeKind.Connection, exception,
                cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            throw;
        }
        var cancelled = cancellationToken.IsCancellationRequested;
        return await RetainAndPersistCompletionAsync(scope,
            token => CompleteConnectionObservationAsync(scope, attempt, result, cancelled, token)).ConfigureAwait(false);
    }

    private async Task<HealthProbeOutcome> CompleteConnectionObservationAsync(HealthScope scope,
        HealthProbeAttempt attempt, ProviderConnectionTestResult result, bool cancelled, CancellationToken cancellationToken)
    {
        if (!cancelled && result.Status == ProviderConnectionStatus.SecretUnavailable)
        {
            var refused = await _healthCenter.CompleteProbeAttemptAsync(attempt,
                HealthProbeCompletionKind.Inconclusive, "A local credential gate refused the connection check before dispatch.",
                BuildConnectionEvidence(result, null), cancellationToken).ConfigureAwait(false);
            return Refuse(scope, refused.Snapshot, HealthProbeKind.Connection, HealthProbeRefusal.SecretReferenceUnavailable,
                "The local credential is unavailable; no provider failure was observed.");
        }

        HealthErrorClass? errorClass = cancelled ? HealthErrorClass.UserCancellation
            : result.IsSuccessful ? null : ClassifyFailure(result.Status);
        var evidence = BuildConnectionEvidence(result, errorClass);

        // A pass confirms only the connection, so it is recorded without leaving Recovering; only the
        // pinned model probe may complete a recovery.
        var reason = result.IsSuccessful
            ? $"The connection to {result.SanitizedEndpointUrl} was confirmed, but a connection probe does " +
              "not verify a recovery: a pinned model probe confirming auth, the selected model and a " +
              "minimal turn is still required."
            : $"The connection probe against {result.SanitizedEndpointUrl} failed with {result.Status}, so " +
              "the scope was quarantined.";

        var completion = await _healthCenter.CompleteProbeAttemptAsync(attempt,
                cancelled ? HealthProbeCompletionKind.Inconclusive : result.IsSuccessful
                    ? HealthProbeCompletionKind.ConnectionSucceeded : errorClass == HealthErrorClass.AuthenticationOrRefresh
                        ? HealthProbeCompletionKind.AuthenticationFailed : HealthProbeCompletionKind.Failed,
                cancelled ? "The caller cancelled the connection probe; recovery remains unverified." : reason,
                evidence, cancellationToken).ConfigureAwait(false);
        var snapshot = completion.Snapshot;

        return new HealthProbeOutcome
        {
            Scope = scope,
            Kind = HealthProbeKind.Connection,
            Succeeded = result.IsSuccessful && !cancelled,
            ConfirmsVerifiedRecovery = false,
            Snapshot = snapshot,
            ErrorClass = errorClass,
            LatencyMs = result.LatencyMs,
            ProbedEndpoint = result.SanitizedEndpointUrl,
            Explanation = !completion.WasCurrent
                ? "The connection result arrived after the health state changed; the newer state was preserved."
                : cancelled
                ? "The caller cancelled the connection probe; its late result did not verify recovery."
                : result.IsSuccessful
                ? $"The connection probe reached {result.SanitizedEndpointUrl} in {result.LatencyMs} ms. " +
                  "This confirms the connection only; a pinned model probe is still required before the " +
                  "recovery counts as verified."
                : $"The connection probe against {result.SanitizedEndpointUrl} failed with {result.Status}, " +
                  "so the scope was quarantined."
        };
    }

    public async Task<HealthProbeOutcome> ProbeModelAsync(
        HealthScope scope,
        HealthProbeConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(confirmation);

        // The pinned probe spends provider quota and is the only path to a verified recovery, so two
        // concurrent executions of it must never overlap.
        if (!_activeProbes.TryAdd(scope, 0))
        {
            return Refuse(
                scope,
                await _healthCenter.GetSnapshotAsync(scope, cancellationToken).ConfigureAwait(false),
                HealthProbeKind.Model,
                HealthProbeRefusal.ProbeAlreadyRunning,
                $"A probe is already running for {scope.ScopeType} '{scope.ScopeId}', so this probe was not started.");
        }

        try
        {
            if (_pendingCompletions.TryGetValue(scope, out var pending))
                return await PersistPendingCompletionAsync(scope, pending).ConfigureAwait(false);
            var observed = await _healthCenter.GetSnapshotAsync(scope, cancellationToken).ConfigureAwait(false);
            return await ExecuteModelProbeAsync(scope, observed, confirmation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _activeProbes.TryRemove(scope, out _);
        }
    }

    private async Task<HealthProbeOutcome> ExecuteModelProbeAsync(
        HealthScope scope,
        HealthSnapshot observed,
        HealthProbeConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        if (observed.AuthenticationFanoutPending)
            return Refuse(scope, observed, HealthProbeKind.Model, HealthProbeRefusal.AuthenticationFanoutPending,
                "Authentication blocking is pending. Refresh the Health Center to retry it before probing.");
        if (observed.State is not (HealthState.ProbeRequired or HealthState.Recovering or HealthState.ForcedEnabled))
        {
            return Refuse(
                scope,
                observed,
                HealthProbeKind.Model,
                HealthProbeRefusal.NoProbePending,
                $"The scope is {observed.State}, which is not awaiting a probe, so no probe was run.");
        }

        // A model probe may spend provider quota, so it never runs without an explicit confirmation.
        if (!confirmation.CostPreviewAcknowledged)
        {
            return Refuse(
                scope,
                observed,
                HealthProbeKind.Model,
                HealthProbeRefusal.ConfirmationRequired,
                "The model probe may spend provider quota, so it requires an explicit confirmation with a " +
                "cost preview before it may run.");
        }

        // A model-route scope names the model it stands for, so a route probe does not require the model
        // to be repeated: the route is exactly the scope that must be verified (ТЗ §6.10).
        // If a model is explicitly provided in confirmation, it must match the model named by the route scope;
        // otherwise the probe is refused without executing or altering the route's state.
        if (scope.ScopeType == HealthScope.ModelRouteScopeType)
        {
            var routeModelId = SplitRouteScope(scope).ModelId;

            if (routeModelId is not null)
            {
                if (!string.IsNullOrEmpty(confirmation.ModelId) &&
                    !string.Equals(confirmation.ModelId, routeModelId, StringComparison.Ordinal))
                {
                    return Refuse(
                        scope,
                        observed,
                        HealthProbeKind.Model,
                        HealthProbeRefusal.ProbeModelUnsupported,
                        $"The route scope '{scope.ScopeId}' names model '{routeModelId}', but the probe confirmation " +
                        $"requested '{confirmation.ModelId}'. A route probe must confirm the model of the route scope itself.");
                }
                // The canonical pair owns the actual request, including case and whitespace.
                confirmation = confirmation with { ModelId = routeModelId };
            }
        }

        if (string.IsNullOrWhiteSpace(confirmation.ModelId))
        {
            return Refuse(
                scope,
                observed,
                HealthProbeKind.Model,
                HealthProbeRefusal.ProbeModelUnsupported,
                "No model was selected for the pinned probe, so a verified recovery cannot be established.");
        }

        if (_modelProbe is null)
        {
            return Refuse(
                scope,
                observed,
                HealthProbeKind.Model,
                HealthProbeRefusal.ProbeModelUnsupported,
                "No pinned model probe executor is configured, so the recovery cannot be verified. The " +
                "scope stays out of routing instead of being shown as recovered.");
        }

        var (settings, resolutionRefusal, resolutionDetail) =
            await ResolveTargetAsync(scope, requireHttpEndpoint: false, cancellationToken).ConfigureAwait(false);

        if (settings is null)
        {
            return Refuse(scope, observed, HealthProbeKind.Model, resolutionRefusal, resolutionDetail);
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            // A CLI-backed backend has no HTTP endpoint a pinned model probe could exercise.
            return Refuse(
                scope,
                observed,
                HealthProbeKind.Model,
                HealthProbeRefusal.ProbeModelUnsupported,
                $"Provider profile '{settings.ProviderId}' is not backed by an HTTP endpoint, so a pinned " +
                "model probe cannot be executed for it.");
        }

        var request = new ModelProbeRequest
        {
            Scope = scope,
            ProviderProfileId = settings.ProviderId,
            BaseUrl = settings.BaseUrl,
            ModelId = confirmation.ModelId,
            ApiKeySecretReference = settings.ApiKeySecretRef,
            AccountId = settings.AccountId,
            CustomHeaders = settings.CustomHeaders
        };

        // Capability is checked without provider I/O. An executor that cannot make this promise
        // fails closed before any start state/audit is created.
        if (!_modelProbe.CanExecute(request))
            return Refuse(scope, observed, HealthProbeKind.Model, HealthProbeRefusal.ProbeModelUnsupported,
                "The executor cannot run this pinned probe; no health transition or provider call was made.");

        var attempt = await _healthCenter.TryBeginProbeAttemptAsync(scope, modelProbe: true,
            $"A pinned model probe for '{confirmation.ModelId}' was admitted before the provider call.",
            cancellationToken).ConfigureAwait(false);
        if (attempt is null)
            return Refuse(scope, await _healthCenter.GetSnapshotAsync(scope, cancellationToken).ConfigureAwait(false),
                HealthProbeKind.Model, HealthProbeRefusal.NoProbePending,
                "The health state changed before admission, so no model probe was sent.");

        ModelProbeResult result;
        try
        {
            result = await _modelProbe.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RecordInterruptedProbeAsync(attempt, HealthProbeKind.Model, exception,
                cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            throw;
        }
        if (cancellationToken.IsCancellationRequested)
            result = ModelProbeResult.Failed(HealthErrorClass.UserCancellation, result.SanitizedEndpoint,
                result.LatencyMs, "The caller cancelled the probe; the late result cannot verify recovery.");
        var observation = result;
        var progress = new ProbePersistenceProgress();
        return await RetainAndPersistCompletionAsync(scope,
            token => CompleteModelObservationAsync(scope, attempt, confirmation, observation, progress, token))
            .ConfigureAwait(false);
    }

    private sealed class ProbePersistenceProgress
    {
        public bool ModelRouteFailureSaved { get; set; }
    }

    private async Task<HealthProbeOutcome> CompleteModelObservationAsync(HealthScope scope,
        HealthProbeAttempt attempt, HealthProbeConfirmation confirmation, ModelProbeResult result,
        ProbePersistenceProgress progress, CancellationToken cancellationToken)
    {
        if (result.Outcome is ModelProbeOutcome.Unsupported or ModelProbeOutcome.CredentialUnavailable)
        {
            // A changing/inconsistent executor contradicted its own preflight capability. Preserve
            // this admitted attempt's audit without promoting health or claiming a provider failure.
            var credentialRefused = result.Outcome == ModelProbeOutcome.CredentialUnavailable;
            var unsupported = await _healthCenter.CompleteProbeAttemptAsync(attempt,
                HealthProbeCompletionKind.Inconclusive, credentialRefused
                    ? "A local credential gate refused the probe before dispatch."
                    : "The executor withdrew its advertised probe capability.",
                BuildModelEvidence(result, confirmation.ModelId, null), cancellationToken).ConfigureAwait(false);
            return Refuse(scope, unsupported.Snapshot, HealthProbeKind.Model, credentialRefused
                    ? HealthProbeRefusal.SecretReferenceUnavailable : HealthProbeRefusal.ProbeModelUnsupported,
                result.Detail ?? "The executor withdrew its advertised capability; recovery remains unverified.");
        }

        var succeeded = result.Outcome == ModelProbeOutcome.Succeeded;
        HealthErrorClass? errorClass = succeeded
            ? null
            : result.FailureClass ?? HealthErrorClass.UnknownOrAmbiguousCompletion;

        var evidence = BuildModelEvidence(result, confirmation.ModelId, errorClass);

        // ТЗ §6.10: a model mismatch blocks only the concrete model route, while an auth failure blocks
        // every route of the account. An account probe that observed a mismatch must therefore not
        // quarantine the account: the failure is isolated on the model route, and the account itself
        // still owes a passing probe before it may return to routing.
        if (errorClass == HealthErrorClass.ModelUnavailableOrMismatch &&
            scope.ScopeType == HealthScope.AccountScopeType)
        {
            var routeScope = HealthScope.ForModelRoute(scope.ScopeId, confirmation.ModelId);

            if (!progress.ModelRouteFailureSaved)
            {
                await _healthCenter.ReportProbeModelMismatchAsync(
                    attempt, confirmation.ModelId,
                    $"The pinned model probe for '{confirmation.ModelId}' observed a model mismatch, so only " +
                    $"the model route '{routeScope.ScopeId}' was blocked; the account was not quarantined.",
                    cancellationToken)
                .ConfigureAwait(false);

                progress.ModelRouteFailureSaved = true;
            }
            var accountCompletion = await _healthCenter.CompleteProbeAttemptAsync(attempt,
                HealthProbeCompletionKind.Inconclusive,
                "The model route failed; the account still requires a passing pinned probe.", evidence,
                cancellationToken).ConfigureAwait(false);
            var accountSnapshot = accountCompletion.Snapshot;

            return new HealthProbeOutcome
            {
                Scope = scope,
                Kind = HealthProbeKind.Model,
                Succeeded = false,
                ConfirmsVerifiedRecovery = false,
                Snapshot = accountSnapshot,
                ErrorClass = errorClass,
                LatencyMs = result.LatencyMs,
                ProbedEndpoint = result.SanitizedEndpoint,
                Explanation = $"The pinned model probe for '{confirmation.ModelId}' observed a model " +
                    "mismatch. The observation was recorded without verifying account recovery; only a " +
                    "still-current attempt may change the concrete model route (ТЗ §6.10)."
            };
        }

        var completion = await _healthCenter.CompleteProbeAttemptAsync(attempt,
                succeeded ? HealthProbeCompletionKind.ModelSucceeded
                    : errorClass == HealthErrorClass.AuthenticationOrRefresh ? HealthProbeCompletionKind.AuthenticationFailed
                    : errorClass is HealthErrorClass.UserCancellation or HealthErrorClass.UnknownOrAmbiguousCompletion
                        ? HealthProbeCompletionKind.Inconclusive : HealthProbeCompletionKind.Failed,
                succeeded ? "The pinned model probe passed." : "The pinned model probe did not verify recovery.",
                evidence, cancellationToken).ConfigureAwait(false);
        var snapshot = completion.Snapshot;

        return new HealthProbeOutcome
        {
            Scope = scope,
            Kind = HealthProbeKind.Model,
            Succeeded = succeeded,
            ConfirmsVerifiedRecovery = completion.VerifiedRecovery,
            Snapshot = snapshot,
            ErrorClass = errorClass,
            LatencyMs = result.LatencyMs,
            ProbedEndpoint = result.SanitizedEndpoint,
            Explanation = !completion.WasCurrent
                ? "The probe result arrived after the health state changed; the newer state was preserved."
                : errorClass == HealthErrorClass.UserCancellation
                ? "The caller cancelled the probe; its late result did not verify recovery."
                : errorClass == HealthErrorClass.UnknownOrAmbiguousCompletion
                ? "The probe outcome was ambiguous; recovery remains unverified and no automatic retry was made."
                : succeeded
                ? $"The pinned model probe confirmed auth, model '{confirmation.ModelId}' and a minimal " +
                  "turn, so the recovery is verified by observed evidence."
                : $"The pinned model probe for '{confirmation.ModelId}' failed, so the scope was quarantined " +
                  "instead of returning to routing."
        };
    }

    /// <summary>
    /// Maps the observed connection status onto the normative error taxonomy. Every status is mapped
    /// explicitly rather than through a catch-all default, so a new status cannot silently become
    /// "unknown" and thereby escape the breaker.
    /// </summary>
    public static HealthErrorClass ClassifyFailure(ProviderConnectionStatus status) => status switch
    {
        ProviderConnectionStatus.AuthenticationFailed => HealthErrorClass.AuthenticationOrRefresh,
        ProviderConnectionStatus.RemoteServerError => HealthErrorClass.Provider4xx5xx,
        ProviderConnectionStatus.EndpointNotFound => HealthErrorClass.Provider4xx5xx,
        ProviderConnectionStatus.ConnectionRefused => HealthErrorClass.NetworkOrTimeout,
        ProviderConnectionStatus.TimedOut => HealthErrorClass.NetworkOrTimeout,
        ProviderConnectionStatus.SslHandshakeError => HealthErrorClass.NetworkOrTimeout,

        // A misconfigured or insecure endpoint is a configuration fault, not a transient provider one.
        ProviderConnectionStatus.InvalidUrlFormat => HealthErrorClass.StartupOrSessionCreation,
        ProviderConnectionStatus.InsecureRemoteHttp => HealthErrorClass.StartupOrSessionCreation,

        // An unusable credential reference is a local configuration fault: the request was never
        // sent, so it says nothing about the provider and must not be counted as an auth failure
        // that cascades to the account (ADR-0005 §5.2).
        ProviderConnectionStatus.SecretUnavailable => HealthErrorClass.StartupOrSessionCreation,

        ProviderConnectionStatus.UnknownError => HealthErrorClass.UnknownOrAmbiguousCompletion,

        // Success is not a failure; reaching here would be a caller error.
        ProviderConnectionStatus.Success => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "A successful connection test has no failure class."),
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Unmapped provider connection status; add it to the health taxonomy explicitly.")
    };

    /// <summary>
    /// Resolves the provider endpoint a scope should be probed against. An account scope is resolved
    /// through its provider profile; a model-route scope is resolved through the account encoded in its
    /// id, so the route can be probed and recovered independently of the account (ТЗ §6.10); a backend
    /// scope is not probeable by an HTTP models call.
    /// </summary>
    private async Task<(CustomProviderSettings? Settings, HealthProbeRefusal Refusal, string Detail)> ResolveTargetAsync(
        HealthScope scope,
        bool requireHttpEndpoint,
        CancellationToken cancellationToken)
    {
        if (scope.ScopeType == HealthScope.BackendScopeType)
        {
            return (
                null,
                HealthProbeRefusal.ScopeKindNotProbeable,
                "A backend scope is a managed process, not an HTTP provider endpoint, so a connection probe cannot verify it.");
        }

        var accountId = scope.ScopeType switch
        {
            HealthScope.AccountScopeType => scope.ScopeId,
            HealthScope.ModelRouteScopeType => SplitRouteScope(scope).AccountId,
            _ => null
        };

        if (accountId is null)
        {
            return (
                null,
                HealthProbeRefusal.ProbeTargetUnknown,
                $"No provider profile could be resolved for {scope.ScopeType} '{scope.ScopeId}', so there is no endpoint to probe.");
        }

        var providerProfileId = await ResolveProviderProfileIdForAccountAsync(accountId, cancellationToken)
            .ConfigureAwait(false);

        if (providerProfileId is null)
        {
            return (
                null,
                HealthProbeRefusal.ProbeTargetUnknown,
                $"No provider profile could be resolved for {scope.ScopeType} '{scope.ScopeId}', so there is no endpoint to probe.");
        }

        if (_providerProfiles is null)
        {
            return (
                null,
                HealthProbeRefusal.ProbeTargetUnknown,
                "No provider profile repository is configured, so the probe target could not be resolved.");
        }

        var profile = await _providerProfiles
            .GetByIdAsync(providerProfileId, cancellationToken)
            .ConfigureAwait(false);

        if (profile is null)
        {
            return (
                null,
                HealthProbeRefusal.ProbeTargetUnknown,
                $"Provider profile '{providerProfileId}' was not found, so there is no endpoint to probe.");
        }

        if (requireHttpEndpoint && string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            return (
                null,
                HealthProbeRefusal.ProbeTargetHasNoEndpoint,
                $"Provider profile '{profile.Id}' has no base URL, so a connection probe cannot reach it.");
        }

        // The account may carry its own credential, which is the more specific reference for the account
        // and for its model routes; the profile-level reference is the fallback.
        string? secretReference = null;

        if (_accounts is not null)
        {
            secretReference = await _accounts
                .GetSecretReferenceAsync(accountId, cancellationToken)
                .ConfigureAwait(false);
        }

        secretReference ??= await _providerProfiles
            .GetApiKeySecretReferenceAsync(profile.Id, cancellationToken)
            .ConfigureAwait(false);

        // The selected reference is checked before the probe is handed on. A reference that is
        // Missing or Revoked can never authenticate the call, so probing it would send an
        // unauthenticated request and report the provider's rejection instead of the real reason
        // (ADR-0005 §5.2). A provider with no reference at all stays probeable.
        if (!string.IsNullOrWhiteSpace(secretReference) && _secretLifecycle is not null)
        {
            var status = await _secretLifecycle
                .GetStatusAsync(secretReference, cancellationToken)
                .ConfigureAwait(false);

            if (!status.IsUsable)
            {
                return (
                    null,
                    HealthProbeRefusal.SecretReferenceUnavailable,
                    $"The secret reference selected for {scope.ScopeType} '{scope.ScopeId}' cannot be used: {status.DescribeState()}.");
            }
        }

        // Only the opaque secret reference is passed on. The key itself is resolved inside the
        // connection-test/model-probe executor and never travels through the health layer or the audit.
        var settings = new CustomProviderSettings(
            profile.Id,
            profile.DisplayName,
            profile.BaseUrl ?? string.Empty,
            secretReference,
            customHeaders: (profile.CustomHeaders ?? []).Select(h => new CustomProviderHeader(h.Name,
                h.Value ?? string.Empty, h.SecretReference is not null, h.SecretReference)).ToArray(),
            validateUrl: false, accountId: accountId);

        return (
            settings,
            HealthProbeRefusal.None,
            string.Empty);
    }

    private static (string? AccountId, string? ModelId) SplitRouteScope(HealthScope scope) =>
        scope.TryGetModelRoute(out var account, out var model) ? (account, model) : (null, null);

    private async Task<string?> ResolveProviderProfileIdForAccountAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        if (_accounts is null)
        {
            return null;
        }

        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        return account?.ProviderProfileId;
    }

    /// <summary>
    /// Redacted connection-probe evidence. Only the sanitized endpoint, the classified status, the
    /// latency and the discovered model count are stored — never the response body, headers or a
    /// credential.
    /// </summary>
    private static string BuildConnectionEvidence(
        ProviderConnectionTestResult result,
        HealthErrorClass? errorClass) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["probe"] = "providerConnection",
            ["kind"] = nameof(HealthProbeKind.Connection),
            ["endpoint"] = result.SanitizedEndpointUrl,
            ["status"] = result.Status.ToString(),
            ["statusCode"] = result.StatusCode,
            ["latencyMs"] = result.LatencyMs,
            ["discoveredModelCount"] = result.DiscoveredModels.Count,
            ["errorClass"] = errorClass?.ToString()
        });

    /// <summary>
    /// Redacted model-probe evidence. Only the sanitized endpoint, the probed model, the classified
    /// failure and the latency are stored.
    /// </summary>
    private static string BuildModelEvidence(
        ModelProbeResult result,
        string modelId,
        HealthErrorClass? errorClass) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["probe"] = "providerModel",
            ["kind"] = nameof(HealthProbeKind.Model),
            ["endpoint"] = result.SanitizedEndpoint,
            ["model"] = modelId,
            ["outcome"] = result.Outcome.ToString(),
            ["latencyMs"] = result.LatencyMs,
            ["errorClass"] = errorClass?.ToString(),
            ["detail"] = result.Detail
        });

    private async Task RecordInterruptedProbeAsync(HealthProbeAttempt attempt, HealthProbeKind kind,
        Exception exception, bool callerCancelled)
    {
        var evidence = JsonSerializer.Serialize(new
        {
            probe = kind == HealthProbeKind.Model ? "providerModel" : "providerConnection",
            kind = kind.ToString(),
            outcome = callerCancelled ? "Cancelled" : "Interrupted",
            errorClass = callerCancelled ? nameof(HealthErrorClass.UserCancellation)
                : nameof(HealthErrorClass.UnknownOrAmbiguousCompletion),
            exceptionType = exception.GetType().Name
        });
        await RetainAndPersistCompletionAsync(attempt.Scope, async token =>
        {
            var completion = await _healthCenter.CompleteProbeAttemptAsync(attempt,
                HealthProbeCompletionKind.Inconclusive,
                "The admitted provider call was interrupted; recovery remains unverified and no automatic retry was made.",
                evidence, token).ConfigureAwait(false);
            return new HealthProbeOutcome
            {
                Scope = attempt.Scope,
                Kind = kind,
                Succeeded = false,
                Snapshot = completion.Snapshot,
                ErrorClass = callerCancelled ? HealthErrorClass.UserCancellation
                    : HealthErrorClass.UnknownOrAmbiguousCompletion,
                Explanation = completion.WasCurrent
                    ? "The interrupted provider observation was saved; recovery remains unverified and the provider call was not repeated."
                    : "The interrupted observation was saved without changing the newer health state."
            };
        }).ConfigureAwait(false);
    }

    private Task<HealthProbeOutcome> RetainAndPersistCompletionAsync(HealthScope scope,
        Func<CancellationToken, Task<HealthProbeOutcome>> persist)
    {
        // The scope's active marker is held by the caller throughout provider work and persistence.
        // Keep only the already observed result and its lease, never a provider request/credential.
        if (!_pendingCompletions.TryAdd(scope, persist))
            throw new InvalidOperationException("An earlier probe observation still requires persistence.");
        return PersistPendingCompletionAsync(scope, persist);
    }

    private async Task<HealthProbeOutcome> PersistPendingCompletionAsync(HealthScope scope,
        Func<CancellationToken, Task<HealthProbeOutcome>> persist)
    {
        // A completed provider call owes an audit even if its original caller cancelled. A failed
        // budget leaves the same operation available to the next explicit probe action, without I/O.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var outcome = await persist(deadline.Token).ConfigureAwait(false);
        _pendingCompletions.TryRemove(scope, out _);
        return outcome;
    }

    private static HealthProbeOutcome Refuse(
        HealthScope scope,
        HealthSnapshot observed,
        HealthProbeKind kind,
        HealthProbeRefusal refusal,
        string explanation) =>
        new()
        {
            Scope = scope,
            Kind = kind,
            Succeeded = false,
            Refusal = refusal,
            Snapshot = observed,
            Explanation = explanation
        };
}
