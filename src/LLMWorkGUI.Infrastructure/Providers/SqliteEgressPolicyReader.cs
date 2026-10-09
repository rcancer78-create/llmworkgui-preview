using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Providers;

internal sealed record EgressPolicyRows(Project Project, Route Route, ProviderProfile Profile)
{
    public string Fingerprint => EgressPolicyFingerprint.Compute(Project, Route, Profile);
}

/// <summary>Uses the same repository decoders, within the caller's writer transaction.</summary>
internal static class SqliteEgressPolicyReader
{
    internal static async Task<EgressPolicyRows> ReadAsync(SqliteConnection connection, SqliteTransaction transaction,
        string projectId, string routeId, CancellationToken token)
    {
        Project project;
        Route route;
        ProviderProfile profile;
        await using (var command = Command(connection, transaction,
            $"SELECT {SqliteProjectRepository.SelectColumns} FROM Projects WHERE Id=$id", projectId))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new EgressApprovalException();
            project = SqliteProjectRepository.Read(reader);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SqliteRouteRepository.SelectAssignmentSql;
            command.Parameters.AddWithValue("$routeId", routeId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new EgressApprovalException();
            var assignment = SqliteRouteRepository.ReadAssignment(reader);
            if (!assignment.HasEveryIdentity) throw new EgressApprovalException();
            route = assignment.Route;
        }
        await using (var command = Command(connection, transaction,
            $"SELECT {SqliteProviderProfileRepository.SelectColumns} FROM ProviderProfiles WHERE Id=$id", route.Binding.ProviderProfileId))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new EgressApprovalException();
            profile = SqliteProviderProfileRepository.ReadProfile(reader);
        }
        return new(project, route, profile);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, string id)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id); return command;
    }
}
