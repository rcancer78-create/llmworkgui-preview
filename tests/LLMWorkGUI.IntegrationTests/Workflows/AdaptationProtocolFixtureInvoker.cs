using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Historical protocol fixture only; never compiled into the application. It resolves the account behind the adaptation route from the
/// local repositories, takes the backend from the owning provider profile and, for the OpenCode
/// backend only, runs one ephemeral prompt turn against the live server. Every other backend fails
/// closed with <see cref="WorkflowValidationException"/> so the adaptation engine never touches a
/// process it does not own.
/// </summary>
/// <remarks>
/// The account and profile are always resolved before anything is classified, so an account whose id
/// happens to be a reserved literal such as "cursor", "default" or "opencode" is still routed by its
/// own profile. The model sent to OpenCode is the real backend-native model id the route carries, or
/// the one the local account record holds; an account id is never substituted for a missing model id
/// and a value that is not a model id at all (a home path, a profile name) is refused.
/// </remarks>
internal sealed class AdaptationProtocolFixtureInvoker : IAdaptationModelInvoker
{
    private readonly IOpenCodeSessionLifecycleService? _openCodeLifecycle;
    private readonly IOpenCodeClient? _openCodeClient;
    private readonly IProviderProfileRepository? _providerProfileRepository;
    private readonly IAccountRepository? _accountRepository;
    private readonly ILogger<AdaptationProtocolFixtureInvoker> _logger;
    private readonly IAdaptationEgressPolicy? _egressPolicy;

    public AdaptationProtocolFixtureInvoker(
        IOpenCodeSessionLifecycleService? openCodeLifecycle = null,
        IOpenCodeClient? openCodeClient = null,
        IProviderProfileRepository? providerProfileRepository = null,
        IAccountRepository? accountRepository = null,
        ILogger<AdaptationProtocolFixtureInvoker>? logger = null,
        IAdaptationEgressPolicy? egressPolicy = null)
    {
        _openCodeLifecycle = openCodeLifecycle;
        _openCodeClient = openCodeClient;
        _providerProfileRepository = providerProfileRepository;
        _accountRepository = accountRepository;
        _logger = logger ?? NullLogger<AdaptationProtocolFixtureInvoker>.Instance;
        _egressPolicy = egressPolicy;
    }

    public async Task<AdaptationModelResponse> InvokeModelAsync(
        AdaptationModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var identity = await ResolveRouteIdentityAsync(request.RouteId, cancellationToken)
            .ConfigureAwait(false);

        if (!identity.IsAdaptationCapable)
        {
            throw new WorkflowValidationException(
                $"Adaptation model invocation is currently only supported for OpenCode backend. Route '{request.RouteId}' "
                + $"resolves to account '{identity.AccountId}' on backend '{identity.Backend}'.");
        }

        if (!identity.HasBackendModelId)
        {
            throw new WorkflowValidationException(
                $"Adaptation route '{request.RouteId}' resolves to account '{identity.AccountId}', which has no backend-native "
                + "model id recorded. The account id is never sent as a model id, so the route is refused.");
        }

        if (_openCodeLifecycle is null || _openCodeClient is null)
        {
            throw new WorkflowValidationException(
                $"OpenCode backend lifecycle and client must both be configured for adaptation model invocation on route '{request.RouteId}'.");
        }

        if (_egressPolicy is null) throw new WorkflowValidationException("Адаптация не передана: проверка сохранённой политики данных не настроена.");
        return await InvokeOpenCodeAsync(request, identity, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the route to its account and provider profile through the repositories. A route id
    /// built by <see cref="AdaptationRouteIdentity"/> contributes the account id and the model the
    /// operator selected; anything else is treated as an account id, and the legacy
    /// "accountId:modelId" form as a fallback. Nothing is classified from the route string itself.
    /// </summary>
    private async Task<AdaptationRouteIdentity> ResolveRouteIdentityAsync(
        string routeId,
        CancellationToken cancellationToken)
    {
        if (_accountRepository is null || _providerProfileRepository is null)
        {
            throw new WorkflowValidationException(
                $"Account and provider profile repositories must both be configured to resolve adaptation route '{routeId}'.");
        }

        var routeCarriedModelId = AdaptationRouteIdentity.TryParse(routeId, out var parsedRoute)
            && BackendModelIdPolicy.TryNormalize(parsedRoute.BackendModelId, out var selectedModelId)
                ? selectedModelId
                : null;

        foreach (var accountId in AdaptationRouteIdentity.EnumerateCandidateAccountIds(routeId))
        {
            var account = await _accountRepository
                .GetByIdAsync(accountId, cancellationToken)
                .ConfigureAwait(false);

            if (account is null)
            {
                continue;
            }

            var profile = await _providerProfileRepository
                .GetByIdAsync(account.ProviderProfileId, cancellationToken)
                .ConfigureAwait(false);

            if (profile is null)
            {
                throw new WorkflowValidationException(
                    $"Adaptation route '{routeId}' resolves to account '{account.Id}', whose provider profile "
                    + $"'{account.ProviderProfileId}' is unknown.");
            }

            // The selected route model wins, because a normal OpenCode account record carries no
            // ProviderNativeId at all. The account record is only the fallback, and only when its value
            // is a real model id: a StarCli home path or an Agy profile name never becomes a Model.
            var backendModelId = routeCarriedModelId
                ?? (BackendModelIdPolicy.TryNormalize(account.ProviderNativeId, out var nativeModelId)
                    ? nativeModelId
                    : null);

            return new AdaptationRouteIdentity(
                account.Id,
                profile.Id,
                profile.Backend,
                backendModelId);
        }

        throw new WorkflowValidationException(
            $"Adaptation route '{routeId}' could not be resolved to a known account.");
    }

    private async Task<AdaptationModelResponse> InvokeOpenCodeAsync(
        AdaptationModelRequest request,
        AdaptationRouteIdentity identity,
        CancellationToken cancellationToken)
    {
        var prompt = AdaptationPromptEnvelope.Format(request);
        var policy = await _egressPolicy!.ValidateAsync(request,identity,prompt,null,cancellationToken).ConfigureAwait(false);
        var backendModelId = identity.BackendModelId!;
        var createRequest = new OpenCodeCreateSessionRequest
        {
            Title = $"workflow-adaptation-{Guid.NewGuid():N}",
            AdaptationAdmissionId = request.AdmissionId,
            Model = backendModelId,
            // Request the backend's read-only plan mode. This is a requested mode, not native permission proof.
            Agent = "plan"
        };

        var session = await _openCodeLifecycle!.CreateAndConfirmSessionAsync(createRequest, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _egressPolicy.ValidateAsync(request,identity,prompt,policy.Fingerprint,cancellationToken).ConfigureAwait(false);
            var promptRequest = new OpenCodePromptRequest
            {
                Prompt = prompt,
                AdaptationAdmissionId = request.AdmissionId,
                Model = backendModelId,
                Agent = "plan"
            };

            var turnResult = await _openCodeLifecycle.ExecuteTurnAsync(session.Id, promptRequest, cancellationToken)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested
                || string.Equals(turnResult.Status, TurnResult.CancelledStatus, StringComparison.Ordinal))
            {
                throw new OperationCanceledException(
                    $"Adaptation turn on session '{session.Id}' was cancelled.");
            }

            if (!string.Equals(turnResult.Status, TurnResult.CompletedStatus, StringComparison.Ordinal))
            {
                throw new WorkflowValidationException(
                    $"Adaptation model invocation failed on route '{request.RouteId}': {turnResult.ErrorMessage ?? turnResult.Status}");
            }

            int? promptTokens = turnResult.Tokens is { } promptUsage
                ? (int)Math.Clamp(promptUsage.Input, 0L, int.MaxValue)
                : null;

            int? completionTokens = turnResult.Tokens is { } completionUsage
                ? (int)Math.Clamp(completionUsage.Output, 0L, int.MaxValue)
                : null;

            return new AdaptationModelResponse(
                turnResult.OutputText,
                promptTokens,
                completionTokens);
        }
        finally
        {
            try
            {
                await _openCodeClient!.AbortSessionAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _logger.LogDebug("Ephemeral adaptation cleanup was not confirmed; native ownership must remain unresolved.");
            }
        }
    }

}
