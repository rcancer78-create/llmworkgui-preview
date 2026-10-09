using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>Production adaptation uses an admitted dedicated runtime and its actual client/lifecycle only.</summary>
public sealed class BackendAdaptationModelInvoker(IProviderProfileRepository profiles, IAccountRepository accounts,
    IAdaptationEgressPolicy egress, OpenCodeAdaptationRuntimeRegistry runtimes, SqliteAdaptationTransportPolicy transport)
    : IAdaptationModelInvoker
{
    public async Task<AdaptationModelResponse> InvokeModelAsync(AdaptationModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        if (!AdaptationRouteIdentity.TryParse(request.RouteId, out var identity) || identity.Backend != BackendType.OpenCode
            || !identity.HasBackendModelId || request.ModelId != identity.BackendModelId) throw Refused();
        var account = await accounts.GetByIdAsync(identity.AccountId, cancellationToken).ConfigureAwait(false) ?? throw Refused();
        var profile = await profiles.GetByIdAsync(identity.ProviderProfileId, cancellationToken).ConfigureAwait(false) ?? throw Refused();
        if (account.ProviderProfileId != profile.Id || profile.Backend != BackendType.OpenCode) throw Refused();
        var prompt = AdaptationPromptEnvelope.Format(request);
        var policy = await egress.ValidateAsync(request, identity, prompt, null, cancellationToken).ConfigureAwait(false);
        var lease = await runtimes.StartAsync(request, identity, policy.Fingerprint, cancellationToken).ConfigureAwait(false);
        try
        {
            // Dispose the pin when this try scope exits, before finally awaits physical stop/release.
            using var operation = await runtimes.PinOperationAsync(lease, cancellationToken).ConfigureAwait(false);
            var client = lease.Runtime.CreateClient(transport);
            var lifecycle = new OpenCodeSessionLifecycleService(client);
            var session = await lifecycle.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest
            {
                Title = "workflow-adaptation-" + Guid.NewGuid().ToString("N"),
                AdaptationAdmissionId = request.AdmissionId, Model = identity.BackendModelId,
                Agent = "plan", Directory = lease.Runtime.WorkingDirectory
            }, cancellationToken).ConfigureAwait(false);
            await egress.ValidateAsync(request, identity, prompt, policy.Fingerprint, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            var result = await lifecycle.ExecuteTurnAsync(session.Id, new OpenCodePromptRequest
            {
                Prompt = prompt, AdaptationAdmissionId = request.AdmissionId,
                Model = identity.BackendModelId, Agent = "plan"
            }, deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Status == TurnResult.CancelledStatus) throw new OperationCanceledException(deadline.Token);
            if (result.Status != TurnResult.CompletedStatus) throw Refused();
            lease.RecordCompletedTurn();
            return new AdaptationModelResponse(result.OutputText,
                result.Tokens is { } usage ? (int)Math.Clamp(usage.Input, 0L, int.MaxValue) : null,
                result.Tokens is { } completion ? (int)Math.Clamp(completion.Output, 0L, int.MaxValue) : null);
        }
        catch (OperationCanceledException) { lease.RecordCancelledTurn(); throw; }
        catch { throw Refused(); } // Native diagnostics can contain prompt/credential data; do not return them to the product.
        finally
        {
            // This exact private owner is the authority. HTTP abort or a synthetic Completed result cannot free the slot.
            try { await runtimes.StopAndReleaseAsync(lease).ConfigureAwait(false); }
            catch { throw new WorkflowValidationException("Завершение собственного runtime или запись его остановки не подтверждены. Резерв сохранён; повторная отправка запрещена."); }
        }
    }
    private static WorkflowValidationException Refused() => new("Адаптация не выполнена: выбранный маршрут, собственный runtime или native-результат не подтверждены.");
}
