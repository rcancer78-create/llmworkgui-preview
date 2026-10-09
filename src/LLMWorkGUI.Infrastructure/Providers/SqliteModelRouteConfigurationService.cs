using System.Globalization;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Providers;

public sealed class SqliteModelRouteConfigurationService(
    ISqliteConnectionFactory factory, IApplicationInstanceGuard instanceGuard, SensitiveDataFilter filter,
    TimeProvider timeProvider) : IModelRouteConfigurationService
{
    public async Task<ModelRouteConfiguration> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = await ReadAsync(connection, transaction, cancellationToken);
        transaction.Commit();
        return result;
    }

    public async Task<string> SaveModelAsync(SaveModelConfiguration request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        instanceGuard.EnsureSupervisorPermitted();
        if (!BackendModelIdPolicy.TryNormalize(request.NativeModelId, out var nativeId)
            || filter.Redact(nativeId) != nativeId)
            throw new InvalidOperationException("Укажите ID модели, без путей, ключей и секретов.");
        var name = ValidateName(request.Name);
        if (!Enum.IsDefined(request.Capability)) throw new InvalidOperationException("Неизвестное состояние поддержки модели.");

        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var snapshot = await ReadAsync(connection, transaction, cancellationToken);
        var profile = snapshot.Profiles.SingleOrDefault(p => p.Id == request.ProfileId)
            ?? throw new InvalidOperationException("Профиль удалён. Обновите список.");
        var previous = request.Expected is null ? null : snapshot.Models.SingleOrDefault(m => m.Id == request.Expected.Id);
        if (request.Expected is not null && previous != request.Expected)
            throw new InvalidOperationException("Модель изменена другим действием. Обновите список перед сохранением.");
        if (previous is not null && (previous.ProfileId != profile.Id || previous.Backend != profile.Backend || previous.NativeModelId != nativeId))
            throw new InvalidOperationException("Профиль и ID сохранённой модели неизменяемы. Создайте новую модель.");
        if (snapshot.Models.Any(m => m.Id != previous?.Id && m.ProfileId == profile.Id && m.NativeModelId == nativeId))
            throw new InvalidOperationException("Эта модель уже сохранена в профиле. Выберите её для редактирования.");

        var id = previous?.Id ?? Guid.NewGuid().ToString("D");
        var provenance = previous is null || previous.Capability != request.Capability
            ? ModelProvenance.UserDefined : previous.Provenance;
        await using var command = Command(connection, transaction, previous is null ? """
            INSERT INTO Models (Id,Backend,ProviderProfileId,ProviderModelId,DisplayName,CapabilityState,
                Provenance,IsEnabled,Health,DiscoveredAtUtc)
            VALUES ($id,$backend,$profile,$native,$name,$capability,$provenance,$enabled,'Healthy',$now)
            """ : """
            UPDATE Models SET DisplayName=$name,CapabilityState=$capability,Provenance=$provenance,IsEnabled=$enabled
            WHERE Id=$id
            """);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$backend", profile.Backend.ToString());
        command.Parameters.AddWithValue("$profile", profile.Id);
        command.Parameters.AddWithValue("$native", nativeId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$capability", request.Capability.ToString());
        command.Parameters.AddWithValue("$provenance", provenance.ToString());
        command.Parameters.AddWithValue("$enabled", request.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
        return id;
    }

    public async Task<string> SaveRouteAsync(SaveRouteConfiguration request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        instanceGuard.EnsureSupervisorPermitted();
        if (request.Priority < 0 || !Enum.IsDefined(request.MaxDataClass))
            throw new InvalidOperationException("Проверьте приоритет и допустимую классификацию данных.");
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? null : request.Mode.Trim();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var snapshot = await ReadAsync(connection, transaction, cancellationToken);
        var profile = snapshot.Profiles.SingleOrDefault(p => p.Id == request.ProfileId)
            ?? throw new InvalidOperationException("Профиль удалён. Обновите список.");
        var account = snapshot.Accounts.SingleOrDefault(a => a.Id == request.AccountId && a.ProfileId == profile.Id)
            ?? throw new InvalidOperationException("Аккаунт не относится к выбранному профилю.");
        var model = snapshot.Models.SingleOrDefault(m => m.Id == request.ModelId && m.ProfileId == profile.Id && m.Backend == profile.Backend)
            ?? throw new InvalidOperationException("Модель не относится к выбранному профилю и бэкенду.");
        if (profile.Backend == BackendType.CursorAcp ? mode is not (null or "ask" or "plan" or "agent") : mode is not null)
            throw new InvalidOperationException("Этот режим не поддерживается редактором для выбранного бэкенда.");
        if ((int)request.MaxDataClass > (int)profile.MaxDataClass)
            throw new InvalidOperationException("Классификация маршрута превышает разрешённую профилем.");
        var previous = request.Expected is null ? null : snapshot.Routes.SingleOrDefault(r => r.Id == request.Expected.Id);
        if (request.Expected is not null && previous != request.Expected)
            throw new InvalidOperationException("Маршрут изменён другим действием. Обновите список перед сохранением.");
        if (previous is not null && (previous.Backend != profile.Backend || previous.ProfileId != profile.Id || previous.AccountId != account.Id || previous.ModelId != model.Id || previous.Mode != mode))
            throw new InvalidOperationException("Привязка сохранённого маршрута неизменяема. Создайте новый маршрут.");
        if (snapshot.Routes.Any(r => r.Id != previous?.Id && r.ProfileId == profile.Id && r.AccountId == account.Id
            && r.ModelId == model.Id && r.Mode == mode && r.ReasoningEffort is null && r.SpeedMode is null))
            throw new InvalidOperationException("Такой маршрут уже существует. Выберите его для редактирования.");
        var id = previous?.Id ?? Guid.NewGuid().ToString("D");
        await using var command = Command(connection, transaction, previous is null ? """
            INSERT INTO Routes (Id,Backend,ProviderProfileId,AccountId,ModelId,ExecutionMode,MaxDataClass,
                IsEnabled,Health,ManualPriority,CreatedAtUtc,UpdatedAtUtc)
            VALUES ($id,$backend,$profile,$account,$model,$mode,$classification,$enabled,'Healthy',$priority,$now,$now)
            """ : """
            UPDATE Routes SET MaxDataClass=$classification,IsEnabled=$enabled,ManualPriority=$priority,UpdatedAtUtc=$now
            WHERE Id=$id
            """);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$backend", profile.Backend.ToString());
        command.Parameters.AddWithValue("$profile", profile.Id);
        command.Parameters.AddWithValue("$account", account.Id);
        command.Parameters.AddWithValue("$model", model.Id);
        command.Parameters.AddWithValue("$mode", (object?)mode ?? DBNull.Value);
        command.Parameters.AddWithValue("$classification", request.MaxDataClass.ToString());
        command.Parameters.AddWithValue("$enabled", request.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$priority", request.Priority);
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
        return id;
    }

    private async Task<ModelRouteConfiguration> ReadAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var profiles = new List<ModelProfileOption>();
        var accounts = new List<ModelAccountOption>();
        var models = new List<ConfiguredModel>();
        var routes = new List<ConfiguredRoute>();
        await using (var command = Command(connection, transaction, "SELECT Id,DisplayName,Backend,MaxDataClass,IsEnabled FROM ProviderProfiles ORDER BY DisplayName,Id"))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) profiles.Add(new(reader.GetString(0), reader.GetString(1), Parse<BackendType>(reader, 2), Parse<DataClassification>(reader, 3), reader.GetBoolean(4)));
        await using (var command = Command(connection, transaction, "SELECT Id,ProviderProfileId,DisplayName,AuthState,IsEnabled,CapabilityRevision FROM Accounts ORDER BY DisplayName,Id"))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) accounts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse<AuthState>(reader, 3), reader.GetBoolean(4)) { CapabilityRevision = reader.GetString(5) });
        await using (var command = Command(connection, transaction, "SELECT Id,ProviderProfileId,Backend,ProviderModelId,DisplayName,CapabilityState,Provenance,IsEnabled,CapabilityRevision FROM Models ORDER BY DisplayName,Id"))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) models.Add(new(reader.GetString(0), reader.GetString(1), Parse<BackendType>(reader, 2), reader.GetString(3), reader.GetString(4), Parse<CapabilityState>(reader, 5), Parse<ModelProvenance>(reader, 6), reader.GetBoolean(7)) { CapabilityRevision = reader.GetString(8) });
        await using (var command = Command(connection, transaction, "SELECT Id,ProviderProfileId,AccountId,ModelId,ExecutionMode,MaxDataClass,IsEnabled,ManualPriority,ReasoningEffort,SpeedMode,Backend FROM Routes ORDER BY ManualPriority,Id"))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) routes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Nullable(reader, 4), Parse<DataClassification>(reader, 5), reader.GetBoolean(6), reader.GetInt32(7), Nullable(reader, 8), Nullable(reader, 9), Parse<BackendType>(reader, 10)));
        var evidence = await SqliteModelCapabilityEvidenceStore.ReadAsync(connection, transaction, filter, token);
        return new(profiles, accounts, models, routes)
        {
            Capabilities = evidence.Where(item => models.Any(model => model.Id == item.ModelId
                && profiles.Any(profile => profile.Id == model.ProfileId && profile.Backend == model.Backend)
                && accounts.Any(account => account.Id == item.AccountId && account.ProfileId == model.ProfileId)))
                .ToArray()
        };
    }

    private string ValidateName(string value)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl) || filter.Redact(name) != name)
            throw new InvalidOperationException("Укажите название модели длиной до 200 символов, без секретов и управляющих символов.");
        return name;
    }
    private string Now() => timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    private static T Parse<T>(SqliteDataReader reader, int index) where T : struct, Enum =>
        Enum.TryParse<T>(reader.GetString(index), out var value) && Enum.IsDefined(value) ? value
            : throw new InvalidDataException("Неизвестное значение в сохранённой конфигурации моделей.");
    private static string? Nullable(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command;
    }
}
