using System.Text.Json;
using LLMWorkGUI.Application.Repositories;

namespace LLMWorkGUI.Application.Health;

public sealed partial class HealthCenterService
{
    private async Task<HealthProbeAttemptCompletion?> TryResumeAuthenticationProbeCompletionAsync(
        HealthProbeAttempt attempt, CancellationToken cancellationToken)
    {
        if (_transitionStore is not IHealthAuthenticationFanoutStore store) return null;
        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var pending = (await store.ListPendingAsync(cancellationToken).ConfigureAwait(false))
            .Where(projection => HasProbeAttempt(projection.EvidenceRedactedJson, attempt.Id)).ToArray();
        var durable = (await _eventRepository.ListByScopeAsync(attempt.Scope.ScopeType, attempt.Scope.ScopeId,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(observation => HasProbeAttempt(observation.EvidenceRedactedJson, attempt.Id));
        if (pending.Length == 0 && durable is null) return null;

        var current = _activeProbeAttempts.TryGetValue(attempt.Scope, out var activeId) && activeId == attempt.Id;
        if (pending.Length > 0)
        {
            // Enqueue is already the durable admission barrier. Complete those exact projections,
            // including siblings, rather than admitting a second copy of the observed failure.
            await ProjectAuthenticationAsync(transition, pending, cancellationToken).ConfigureAwait(false);
            if ((await store.ListPendingAsync(cancellationToken).ConfigureAwait(false))
                .Any(projection => HasProbeAttempt(projection.EvidenceRedactedJson, attempt.Id)))
                throw new InvalidOperationException("Authentication blocking is incomplete; the original probe observation remains pending.");
        }

        var (record, barrierPending) = await store.ReadSnapshotAsync(attempt.Scope, cancellationToken).ConfigureAwait(false);
        if (record is null)
            throw new InvalidOperationException("The admitted authentication observation has no durable health projection.");
        // Retire only our own in-memory lease. A newer attempt or health decision is untouched.
        if (_activeProbeAttempts.TryGetValue(attempt.Scope, out activeId) && activeId == attempt.Id)
            _activeProbeAttempts.Remove(attempt.Scope);
        return new(ToSnapshot(attempt.Scope, record) with { AuthenticationFanoutPending = barrierPending }, current, false);
    }

    private static bool HasProbeAttempt(string? evidence, string attemptId)
    {
        if (evidence is null) return false;
        try
        {
            using var document = JsonDocument.Parse(evidence);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("probeAttemptId", out var id) &&
                id.ValueKind == JsonValueKind.String && id.GetString() == attemptId;
        }
        catch (JsonException) { return false; }
    }
}
