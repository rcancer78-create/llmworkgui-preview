using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Infrastructure.Data;

namespace LLMWorkGUI.Infrastructure.Repositories;

/// <summary>Production health persistence: snapshot and append-only audit share one SQLite commit.</summary>
public sealed partial class SqliteHealthTransitionStore(ISqliteConnectionFactory connectionFactory)
    : IHealthTransitionStore, IHealthAuthenticationFanoutStore
{
    public async Task SaveAsync(HealthStateRecord? state, HealthEventRecord? healthEvent,
        CancellationToken cancellationToken = default)
    {
        if (state is null && healthEvent is null) return;
        if (state is not null && healthEvent is not null &&
            (state.ScopeType != healthEvent.ScopeType || state.ScopeId != healthEvent.ScopeId ||
             state.State != healthEvent.NewState))
            throw new ArgumentException("The health snapshot and audit must describe the same scope and state.");

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        if (state is not null)
            await SqliteHealthStateRepository.UpsertAsync(connection, transaction, state, cancellationToken)
                .ConfigureAwait(false);
        if (healthEvent is not null)
            await SqliteHealthEventRepository.AppendAsync(connection, transaction, healthEvent, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
