using System.Globalization;
using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Providers;

public sealed class SqliteAccountConfigurationService(ISqliteConnectionFactory factory,
    IApplicationInstanceGuard guard, SensitiveDataFilter filter, TimeProvider clock,
    IEnumerable<IAccountBridge> bridges) : IAccountConfigurationService
{
    public async Task<AccountConfiguration> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = await Read(connection, transaction, cancellationToken);
        result = await AttachSessionBindings(connection, transaction, result, cancellationToken);
        transaction.Commit();
        return result;
    }

    public async Task<string> SaveAsync(SaveAccountSettings request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        guard.EnsureSupervisorPermitted();
        var name = SafeName(request.Name);
        if (request.Priority < 0 || request.MaxConcurrentExecutions < 1
            || request.ReserveThreshold is { } reserve && (!double.IsFinite(reserve) || reserve < 0 || reserve > 1))
            throw new InvalidOperationException("Приоритет ≥ 0, лимит ≥ 1, резерв от 0 до 1.");
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        guard.EnsureSupervisorPermitted();
        var snapshot = await Read(connection, transaction, cancellationToken);
        var profile = Profile(snapshot, request.ProfileId);
        var previous = request.Expected is null ? null : snapshot.Accounts.SingleOrDefault(a => a.Settings.Id == request.Expected.Id)?.Settings;
        if (request.Expected is not null && previous != request.Expected)
            throw new InvalidOperationException("Настройки аккаунта изменены. Обновите список.");
        if (previous is not null && previous.ProfileId != profile.Id)
            throw new InvalidOperationException("Профиль сохранённого аккаунта неизменяем.");
        if (previous is null && profile.Backend is not (BackendType.OpenCode or BackendType.CursorAcp))
            throw new InvalidOperationException("Для этого бэкенда импортируйте существующий нативный контекст.");
        if (snapshot.Accounts.Any(a => a.Settings.Id != previous?.Id && a.Settings.ProfileId == profile.Id
            && string.Equals(a.Settings.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Такое имя аккаунта уже используется в профиле.");
        var id = previous?.Id ?? Guid.NewGuid().ToString("D");
        await using var command = Command(connection, transaction, previous is null ? """
            INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,AuthState,Health,ManualPriority,
                IsEnabled,MaxConcurrentExecutions,ReserveThreshold,CreatedAtUtc,UpdatedAtUtc)
            VALUES ($id,$profile,$name,'Unknown','Healthy',$priority,$enabled,$limit,$reserve,$now,$now)
            """ : """
            UPDATE Accounts SET DisplayName=$name,ManualPriority=$priority,IsEnabled=$enabled,
                MaxConcurrentExecutions=$limit,ReserveThreshold=$reserve,UpdatedAtUtc=$now WHERE Id=$id
            """);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$profile", profile.Id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$priority", request.Priority);
        command.Parameters.AddWithValue("$enabled", request.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$limit", request.MaxConcurrentExecutions);
        command.Parameters.AddWithValue("$reserve", (object?)request.ReserveThreshold ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
        return id;
    }

    public async Task<IReadOnlyList<AccountImportCandidate>> DiscoverAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profile = Profile(await ReadAsync(cancellationToken), profileId);
        return await Discover(profile, cancellationToken);
    }

    public async Task<string> ImportAsync(string profileId, AccountImportCandidate expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        guard.EnsureSupervisorPermitted();
        var profile = Profile(await ReadAsync(cancellationToken), profileId);
        var candidates = await Discover(profile, cancellationToken);
        if (!candidates.Contains(expected))
            throw new InvalidOperationException("Нативный контекст изменён или отсутствует. Повторите обнаружение.");
        // No transaction is held over native I/O. Recheck the profile under the writer transaction.
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        guard.EnsureSupervisorPermitted();
        var snapshot = await Read(connection, transaction, cancellationToken);
        if (Profile(snapshot, profileId) != profile)
            throw new InvalidOperationException("Профиль изменён во время обнаружения. Повторите импорт.");
        if (snapshot.Accounts.Any(a => a.Settings.Id == expected.Id
            || a.Settings.ProfileId == profileId && (expected.NativeId is not null && a.Settings.NativeId == expected.NativeId
                || string.Equals(a.Settings.Name, expected.Name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Этот контекст или имя уже сохранены. Выберите существующий аккаунт.");
        await using var command = Command(connection, transaction, """
            INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,ProviderNativeId,AuthState,Health,
                ManualPriority,IsEnabled,MaxConcurrentExecutions,CreatedAtUtc,UpdatedAtUtc)
            VALUES ($id,$profile,$name,$native,'Unknown','Healthy',0,0,1,$now,$now)
            """);
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$name", expected.Name);
        command.Parameters.AddWithValue("$native", (object?)expected.NativeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
        return expected.Id;
    }

    private async Task<IReadOnlyList<AccountImportCandidate>> Discover(ModelProfileOption profile, CancellationToken token)
    {
        var backendId = profile.Backend switch { BackendType.OpenCode => "opencode", BackendType.StarCliProxy => "star-cliproxy", _ => null };
        var matching = bridges.Where(b => backendId is not null && b.BackendId == backendId).ToArray();
        if (matching.Length != 1)
            throw new InvalidOperationException("Обнаружение аккаунтов для этого бэкенда недоступно.");
        var discovered = await matching[0].DiscoverAccountsAsync(profile.Id, token);
        var candidates = new List<AccountImportCandidate>();
        foreach (var item in discovered)
        {
            // Native paths are metadata from the bridge. Never read or copy their credential files.
            if (!SafeIdentity(item.Id) || item.ProviderNativeId is { } native && !SafeIdentity(native))
                throw new InvalidOperationException("Обнаружение вернуло небезопасные метаданные аккаунта.");
            candidates.Add(new(item.Id, SafeName(item.DisplayName), item.ProviderNativeId));
        }
        if (candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            throw new InvalidOperationException("Обнаружение вернуло неоднозначные аккаунты.");
        return candidates;
    }

    private string SafeName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl) || filter.Redact(name) != name)
            throw new InvalidOperationException("Укажите имя до 200 символов без секретов и управляющих символов.");
        return name;
    }
    private bool SafeIdentity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048
        && !value.Any(char.IsControl) && filter.Redact(value) == value;
    private static ModelProfileOption Profile(AccountConfiguration snapshot, string id) =>
        snapshot.Profiles.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Профиль удалён. Обновите список.");
    private static async Task<AccountConfiguration> Read(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var profiles = new List<ModelProfileOption>();
        var accounts = new List<AccountConfigurationRow>();
        await using (var command = Command(connection, transaction, "SELECT Id,DisplayName,Backend,MaxDataClass,IsEnabled FROM ProviderProfiles ORDER BY DisplayName,Id"))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) profiles.Add(new(reader.GetString(0), reader.GetString(1), Parse<BackendType>(reader, 2), Parse<DataClassification>(reader, 3), reader.GetBoolean(4)));
        await using (var command = Command(connection, transaction, """
            SELECT Id,ProviderProfileId,DisplayName,ProviderNativeId,ManualPriority,IsEnabled,MaxConcurrentExecutions,
                ReserveThreshold,AuthState,Health,CooldownUntilUtc,DisabledUntilUtc FROM Accounts ORDER BY DisplayName,Id
            """))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) accounts.Add(new(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), Nullable(reader, 3),
                reader.GetInt32(4), reader.GetBoolean(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetDouble(7)),
                Parse<AuthState>(reader, 8), Parse<HealthState>(reader, 9), Nullable(reader, 10), Nullable(reader, 11)));
        return new(profiles, accounts);
    }
    private static T Parse<T>(SqliteDataReader reader, int index) where T : struct, Enum =>
        Enum.TryParse<T>(reader.GetString(index), out var value) && Enum.IsDefined(value) ? value : throw new InvalidDataException("Неизвестное состояние аккаунта.");
    private async Task<AccountConfiguration> AttachSessionBindings(SqliteConnection connection, SqliteTransaction transaction,
        AccountConfiguration snapshot, CancellationToken token)
    {
        // A bounded sample per account, with an exact count. Closed rows are included only while
        // local execution/lock ownership remains; hiding them would conceal a retained slot.
        await using var command = Command(connection, transaction, """
            WITH bindings AS (
                SELECT s.*,ROW_NUMBER() OVER (PARTITION BY s.AccountId ORDER BY s.LastEventAtUtc DESC,s.Id DESC) AS RowNumber,
                    COUNT(*) OVER (PARTITION BY s.AccountId) AS BindingCount
                FROM Sessions s JOIN Accounts a ON a.Id=s.AccountId AND a.ProviderProfileId=s.ProviderProfileId
                    JOIN ProviderProfiles p ON p.Id=s.ProviderProfileId AND p.Backend=s.Backend
                WHERE s.State<>'Closed' OR s.ActiveExecutionId IS NOT NULL
                    OR EXISTS (SELECT 1 FROM Executions e WHERE e.SessionId=s.Id AND e.EndedAtUtc IS NULL)
                    OR EXISTS (SELECT 1 FROM Executions e JOIN ProjectLocks l ON l.ExecutionId=e.Id
                        WHERE e.SessionId=s.Id AND l.ReleasedAtUtc IS NULL)
            )
            SELECT AccountId,Id,ProjectId,Backend,ModelId,ReasoningEffort,SpeedMode,ExecutionMode,NativeSessionId,
                State,ActiveExecutionId,BindingCount FROM bindings WHERE RowNumber<=20
            ORDER BY AccountId,RowNumber
            """);
        var bindings = new Dictionary<string, List<AccountSessionBinding>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var accountId = reader.GetString(0);
            if (!bindings.TryGetValue(accountId, out var rows)) bindings[accountId] = rows = [];
            rows.Add(new(Clean(reader.GetString(1)), Clean(reader.GetString(2)), Parse<BackendType>(reader, 3),
                Clean(reader.GetString(4)), CleanOptional(Nullable(reader, 5)), CleanOptional(Nullable(reader, 6)), CleanOptional(Nullable(reader, 7)),
                CleanOptional(Nullable(reader, 8)), Parse<SessionState>(reader, 9), CleanOptional(Nullable(reader, 10))));
            counts[accountId] = reader.GetInt32(11);
        }
        return snapshot with { Accounts = snapshot.Accounts.Select(row => row with
            { Sessions = bindings.TryGetValue(row.Settings.Id, out var rows) ? rows : [],
                SessionCount = counts.GetValueOrDefault(row.Settings.Id) }).ToArray() };
    }
    private string Clean(string value) => filter.Redact(value);
    private string? CleanOptional(string? value) => value is null ? null : filter.Redact(value);
    private static string? Nullable(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private string Now() => clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
}
