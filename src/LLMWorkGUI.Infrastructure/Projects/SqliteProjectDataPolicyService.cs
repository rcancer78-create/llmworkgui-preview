using System.Globalization;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Projects;

/// <summary>Explicit operator policy change, atomically audited and refused during owned work.</summary>
public sealed class SqliteProjectDataPolicyService(ISqliteConnectionFactory factory,
    IApplicationInstanceGuard guard, TimeProvider clock) : IProjectDataPolicyService
{
    public async Task<IReadOnlyList<ProjectDataPolicySnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,DisplayName,RootPath,DataClassification,UpdatedAtUtc FROM Projects ORDER BY DisplayName,Id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ProjectDataPolicySnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Enum.TryParse<DataClassification>(reader.GetString(3), out var classification) || !Enum.IsDefined(classification))
                throw new InvalidOperationException("Класс данных проекта не распознан.");
            result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),classification,reader.GetString(4)));
        }
        return result;
    }

    public async Task ChangeAsync(ProjectDataPolicySnapshot expected, DataClassification requested, bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        guard.EnsureSupervisorPermitted();
        if (!confirmed) throw new InvalidOperationException("Подтвердите изменение класса данных выбранного проекта.");
        if (!Enum.IsDefined(requested) || !Enum.IsDefined(expected.DataClassification))
            throw new ArgumentOutOfRangeException(nameof(requested));
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalRoot = ProjectLock.CanonicalizeRoot(expected.RootPath);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await using (var blocked = connection.CreateCommand())
        {
            blocked.Transaction = transaction;
            blocked.CommandText = """
                SELECT EXISTS(SELECT 1 FROM ProjectLocks l
                    WHERE l.CanonicalRootPath=$root COLLATE NOCASE AND l.ReleasedAtUtc IS NULL)
                OR EXISTS(SELECT 1 FROM Sessions s WHERE s.ProjectId=$id AND s.ActiveExecutionId IS NOT NULL)
                OR EXISTS(SELECT 1 FROM Executions e JOIN Sessions s ON s.Id=e.SessionId WHERE s.ProjectId=$id
                    AND (e.EndedAtUtc IS NULL OR e.State NOT IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch')))
                """;
            blocked.Parameters.AddWithValue("$id",expected.ProjectId);
            blocked.Parameters.AddWithValue("$root",canonicalRoot);
            if (Convert.ToInt64(await blocked.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),CultureInfo.InvariantCulture)!=0)
                throw new InvalidOperationException("Дождитесь завершения или восстановления текущей работы проекта перед изменением класса данных.");
        }
        var observedAt = clock.GetUtcNow().ToUniversalTime();
        if (!DateTimeOffset.TryParse(expected.Revision,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var previousUpdate))
            throw new InvalidOperationException("Версия проекта не распознана. Обновите список проектов.");
        // A fixed or backward-moving wall clock must not make a stale A → B → A snapshot valid again.
        var nextRevision = observedAt > previousUpdate ? observedAt : previousUpdate.AddTicks(1);
        var now = observedAt.ToString("O",CultureInfo.InvariantCulture);
        await using (var change = connection.CreateCommand())
        {
            change.Transaction = transaction;
            change.CommandText = """
                UPDATE Projects SET DataClassification=$class,UpdatedAtUtc=$now
                WHERE Id=$id AND DisplayName=$name AND RootPath=$root AND UpdatedAtUtc=$revision AND DataClassification=$previous
                """;
            change.Parameters.AddWithValue("$class",requested.ToString());
            change.Parameters.AddWithValue("$now",nextRevision.ToString("O",CultureInfo.InvariantCulture));
            change.Parameters.AddWithValue("$id",expected.ProjectId);
            change.Parameters.AddWithValue("$name",expected.DisplayName);
            change.Parameters.AddWithValue("$root",expected.RootPath);
            change.Parameters.AddWithValue("$revision",expected.Revision);
            change.Parameters.AddWithValue("$previous",expected.DataClassification.ToString());
            if (await change.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)!=1)
                throw new InvalidOperationException("Настройки проекта изменились. Обновите список и подтвердите новый выбор.");
        }
        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO ProjectDataPolicyChanges(Id,ProjectId,PreviousClass,NewClass,ChangedAtUtc,Reason)
                VALUES($audit,$project,$previous,$next,$now,'Explicit operator confirmation; routes and egress checks remain required.')
                """;
            audit.Parameters.AddWithValue("$audit",Guid.NewGuid().ToString("N"));
            audit.Parameters.AddWithValue("$project",expected.ProjectId);
            audit.Parameters.AddWithValue("$previous",expected.DataClassification.ToString());
            audit.Parameters.AddWithValue("$next",requested.ToString());
            audit.Parameters.AddWithValue("$now",now);
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        guard.EnsureSupervisorPermitted();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
