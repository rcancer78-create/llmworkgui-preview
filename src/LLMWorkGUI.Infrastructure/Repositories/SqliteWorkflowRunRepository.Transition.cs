using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed partial class SqliteWorkflowRunRepository
{
    public async Task SaveTransitionAsync(WorkflowRun expected, WorkflowRun next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(next);
        if (expected.Id != next.Id) throw new WorkflowValidationException("A transition cannot change its run identity.");
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Take SQLite's write reservation before reading authority. A different connection cannot change
        // the execution, session, artifacts or run between the checks and the transition commit.
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await GetByIdAsync(connection, transaction, expected.Id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.IsTerminal || current.State != expected.State || current.SessionId != expected.SessionId
            || current.ProjectId != expected.ProjectId || SerializeEvidence(current) != SerializeEvidence(expected)
            || !current.Artifacts.Select(a => a.ArtifactId).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Artifacts.Select(a => a.ArtifactId).Order(StringComparer.Ordinal)))
            throw new WorkflowValidationException("The run or its artifact evidence changed before transition commit.");

        var oldTransitionIds = expected.Transitions.Select(t => t.TransitionId).ToHashSet(StringComparer.Ordinal);
        foreach (var transition in next.Transitions.Where(t => !oldTransitionIds.Contains(t.TransitionId)))
        {
            if (transition.AuthorizingArtifactId is not { } artifactId) continue;
            var artifact = current.Artifacts.SingleOrDefault(a => a.ArtifactId == artifactId)
                ?? throw new WorkflowValidationException("The transition's artifact no longer exists.");
            if (artifact.ExecutionId is { } executionId)
                await ValidateArtifactExecutionAsync(connection, transaction, current, executionId, cancellationToken).ConfigureAwait(false);
        }
        await UpsertRunAsync(connection, transaction, next, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
