using System.Globalization;
using LLMGateway.Core;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Providers;

public sealed class SqliteGatewayCatalogImportService(
    Func<ILlmGateway> gatewayFactory, GatewayCatalogMapper mapper, ISqliteConnectionFactory factory,
    IApplicationInstanceGuard instanceGuard, TimeProvider timeProvider) : IGatewayCatalogImportService
{
    public async Task<GatewayCatalogSnapshot> ImportAsync(CancellationToken cancellationToken = default)
    {
        instanceGuard.EnsureSupervisorPermitted();
        cancellationToken.ThrowIfCancellationRequested();
        // Gateway construction can synchronously wait for native account-store initialization.
        // Resolve it only on an explicit import, outside the caller's UI synchronization context.
        var gateway = await Task.Run(gatewayFactory, cancellationToken).ConfigureAwait(false);
        var providers = await gateway.GetProvidersAsync(cancellationToken);
        var accounts = await gateway.GetAccountsAsync(cancellationToken);
        var models = await gateway.GetModelsAsync(refresh: true, cancellationToken: cancellationToken);
        var quotas = await gateway.GetQuotasAsync(refresh: true, cancellationToken: cancellationToken);
        var snapshot = mapper.Map(providers, accounts, models, quotas, timeProvider.GetUtcNow());
        cancellationToken.ThrowIfCancellationRequested();
        instanceGuard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        async Task Insert(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        async Task Verify(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Сохранённая привязка LLMGateway конфликтует с каталогом.");
        }
        foreach (var p in snapshot.Providers)
        {
            await Insert("""
                INSERT INTO ProviderProfiles (Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
                VALUES ($id,$name,'NativeGateway','PublicSource',0,$now,$now) ON CONFLICT(Id) DO NOTHING
                """, ("$id", p.Id), ("$name", p.DisplayName), ("$now", now));
            await Verify("SELECT COUNT(*) FROM ProviderProfiles WHERE Id=$id AND Backend='NativeGateway'", ("$id", p.Id));
        }
        foreach (var a in snapshot.Accounts)
        {
            await Insert("""
                INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,ProviderNativeId,AuthState,IsEnabled,Health,CreatedAtUtc,UpdatedAtUtc)
                VALUES ($id,$provider,$name,$native,'Unknown',0,'ProbeRequired',$now,$now) ON CONFLICT(Id) DO NOTHING
                """, ("$id", a.Id), ("$provider", a.ProviderProfileId), ("$name", a.DisplayName),
                ("$native", a.ProviderNativeId), ("$now", now));
            await Verify("SELECT COUNT(*) FROM Accounts WHERE Id=$id AND ProviderProfileId=$provider AND ProviderNativeId=$native",
                ("$id", a.Id), ("$provider", a.ProviderProfileId), ("$native", a.ProviderNativeId));
        }
        foreach (var m in snapshot.Models)
        {
            await Insert("""
                INSERT INTO Models (Id,Backend,ProviderProfileId,ProviderModelId,DisplayName,CapabilityState,Provenance,IsEnabled,Health,DiscoveredAtUtc)
                VALUES ($id,'NativeGateway',$provider,$native,$name,'Unknown','PluginReported',0,'ProbeRequired',$now)
                ON CONFLICT(Id) DO NOTHING
                """, ("$id", m.Id), ("$provider", m.ProviderProfileId), ("$native", m.ProviderModelId),
                ("$name", m.DisplayName), ("$now", now));
            await Verify("SELECT COUNT(*) FROM Models WHERE Id=$id AND Backend='NativeGateway' AND ProviderProfileId=$provider AND ProviderModelId=$native",
                ("$id", m.Id), ("$provider", m.ProviderProfileId), ("$native", m.ProviderModelId));
        }
        foreach (var r in snapshot.Routes)
        {
            await Insert("""
                INSERT INTO Routes (Id,Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,IsEnabled,Health,CreatedAtUtc,UpdatedAtUtc)
                VALUES ($id,'NativeGateway',$provider,$account,$model,'PublicSource',0,'ProbeRequired',$now,$now)
                ON CONFLICT(Id) DO NOTHING
                """, ("$id", r.Id), ("$provider", r.Binding.ProviderProfileId), ("$account", r.Binding.AccountId),
                ("$model", r.Binding.ModelId), ("$now", now));
            await Verify("""
                SELECT COUNT(*) FROM Routes WHERE Id=$id AND Backend='NativeGateway' AND ProviderProfileId=$provider
                AND AccountId=$account AND ModelId=$model AND ReasoningEffort IS NULL AND SpeedMode IS NULL AND ExecutionMode IS NULL
                """, ("$id", r.Id), ("$provider", r.Binding.ProviderProfileId), ("$account", r.Binding.AccountId), ("$model", r.Binding.ModelId));
        }
        foreach (var q in snapshot.Quotas)
            await Insert("""
                INSERT INTO QuotaSnapshots (Id,AccountId,Freshness,Source,CapturedAtUtc,RawRedactedPayloadJson)
                VALUES ($id,$account,$provenance,$provider,$captured,$payload)
                ON CONFLICT(Id) DO UPDATE SET Freshness=excluded.Freshness, RawRedactedPayloadJson=excluded.RawRedactedPayloadJson
                """, ("$id", q.Id), ("$account", q.AccountId), ("$provenance", q.Provenance.ToString()),
                ("$provider", q.ProviderProfileId), ("$captured", q.CapturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                ("$payload", SqliteQuotaSnapshotRepository.SerializePayload(q)));
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return snapshot;
    }
}
