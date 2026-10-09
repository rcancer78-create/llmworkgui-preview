using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

/// <summary>
/// The read-only route store: the only production code that has ever read the <c>Routes</c> table.
/// <para>
/// A route is persisted together with the account, the provider profile and the model it selects, and this
/// reader reports all three outcomes separately. The row's own foreign keys already refuse an insert with
/// a dangling identity, so a missing identity here is either a row written before those constraints, or a
/// library that was edited outside this process. Either way the route cannot be executed, and the refusal
/// says which identity is gone rather than collapsing them into "invalid route".
/// </para>
/// </summary>
public sealed class SqliteRouteRepository : IRouteRepository
{
    internal const string SelectAssignmentSql = """
        SELECT
            r.Id, r.Backend, r.ProviderProfileId, r.AccountId, r.ModelId,
            r.ReasoningEffort, r.SpeedMode, r.ExecutionMode,
            r.MaxDataClass, r.IsEnabled, r.Health, r.ManualPriority,
            (SELECT COUNT(*) FROM ProviderProfiles p WHERE p.Id = r.ProviderProfileId) AS ProfileCount,
            (SELECT COUNT(*) FROM Accounts a WHERE a.Id = r.AccountId) AS AccountCount,
            (SELECT COUNT(*) FROM Models m WHERE m.Id = r.ModelId) AS ModelCount
        FROM Routes r
        WHERE r.Id = $routeId;
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteRouteRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<WorkflowRouteAssignment?> GetAssignmentAsync(
        string routeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = SelectAssignmentSql;
        command.Parameters.AddWithValue("$routeId", routeId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadAssignment(reader);
    }

    internal static WorkflowRouteAssignment ReadAssignment(SqliteDataReader reader)
    {
        var missing = new List<WorkflowRouteIdentity>(3);

        if (reader.GetInt64(12) == 0)
        {
            missing.Add(WorkflowRouteIdentity.ProviderProfile);
        }

        if (reader.GetInt64(13) == 0)
        {
            missing.Add(WorkflowRouteIdentity.Account);
        }

        if (reader.GetInt64(14) == 0)
        {
            missing.Add(WorkflowRouteIdentity.Model);
        }

        var route = new Route(
            reader.GetString(0),
            new SessionBinding(
                ParseBackend(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                SqliteRepositorySupport.GetNullableString(reader, 5),
                SqliteRepositorySupport.GetNullableString(reader, 6),
                SqliteRepositorySupport.GetNullableString(reader, 7)),
            SqliteRepositorySupport.ParseEnum<DataClassification>(reader.GetString(8)),
            reader.GetInt64(9) != 0,
            SqliteRepositorySupport.ParseEnum<HealthState>(reader.GetString(10)),
            checked((int)reader.GetInt64(11)));

        return missing.Count == 0
            ? WorkflowRouteAssignment.Complete(route)
            : new WorkflowRouteAssignment(route, missing);
    }

    /// <summary>
    /// The backend by its persisted name only, and only when this build knows that name.
    /// <para>
    /// An unknown value used to become <see cref="BackendType.OpenCode"/>. That silently re-pointed a row
    /// written by another or newer build at the one backend this application happens to have, so a route
    /// stored as something else was read as - and then dispatched as - an OpenCode route, with no trace of
    /// the substitution anywhere in the session, the execution or the reviewer evidence. A read that cannot
    /// say which backend a row selects must refuse instead: the row is still real, so the refusal names the
    /// column rather than reporting the route as absent, and nothing is dispatched to it.
    /// </para>
    /// </summary>
    private static BackendType ParseBackend(string value) =>
        Enum.TryParse<BackendType>(value, ignoreCase: false, out var backend) && Enum.IsDefined(backend)
            ? backend
            : throw new InvalidDataException(
                $"Routes.Backend value '{value}' is not a backend this build knows, so the route is refused "
                    + "rather than read as a different backend.");
}
