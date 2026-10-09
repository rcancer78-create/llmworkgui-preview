using LLMGateway.Core;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>Captures local scope before native I/O and publishes only explicitly reported options.</summary>
public sealed class NativeGatewayModelCapabilityDiscoveryService(Func<ILlmGateway> gatewayFactory,
    ISqliteConnectionFactory factory, IModelCapabilityEvidenceStore store, IApplicationInstanceGuard guard,
    TimeProvider clock) : IModelCapabilityDiscoveryService
{
    public async Task DiscoverAsync(string modelId, string accountId, CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        ModelCapabilityContext context;
        string nativeAccount, nativeModel, profile;
        await using (var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        using (var transaction = connection.BeginTransaction(deferred: true))
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT m.CapabilityRevision,a.CapabilityRevision,a.ProviderNativeId,m.ProviderModelId,p.Id
                FROM Models m JOIN Accounts a ON a.ProviderProfileId=m.ProviderProfileId
                JOIN ProviderProfiles p ON p.Id=m.ProviderProfileId AND p.Backend=m.Backend
                WHERE m.Id=$model AND a.Id=$account AND p.Backend='NativeGateway'
                    AND p.IsEnabled=1 AND a.IsEnabled=1 AND m.IsEnabled=1
                """;
            command.Parameters.AddWithValue("$model",modelId); command.Parameters.AddWithValue("$account",accountId);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(2))
                    throw new InvalidOperationException("Для discovery выберите включённую модель и аккаунт NativeGateway.");
                context = new(modelId,accountId,reader.GetString(0),reader.GetString(1));
                nativeAccount=reader.GetString(2); nativeModel=reader.GetString(3); profile=reader.GetString(4);
            }
            transaction.Commit();
        }
        var gateway=await Task.Run(gatewayFactory,cancellationToken).ConfigureAwait(false);
        // Remote/custom gateways cannot assert authentication for a locally registered native profile.
        if (gateway is not LlmGateway) throw new InvalidOperationException("Требуется локальный штатный gateway для discovery.");
        var report=await gateway.DiscoverModelOptionsAsync(nativeAccount,nativeModel,cancellationToken).ConfigureAwait(false);
        if (report.AccountId!=nativeAccount || report.NativeModel!=nativeModel
            || GatewayCatalogMapper.ProviderId(report.Provider)!=profile)
            throw new InvalidOperationException("Изменена native привязка discovery.");
        guard.EnsureSupervisorPermitted();
        var now=clock.GetUtcNow();
        if (report.ObservedAtUtc == default || report.ObservedAtUtc > now || report.ObservedAtUtc <= now.AddMinutes(-10))
            throw new InvalidOperationException("Native discovery report устарел или имеет неверное время.");
        // Inventory is not chat/tools/vision evidence. Only actual reported reasoning options are granted.
        await store.SaveAsync(new ModelCapabilityEvidence(modelId,accountId,CapabilityState.Supported,ModelProvenance.PluginReported,
            report.ObservedAtUtc,report.ObservedAtUtc.AddMinutes(10),report.ReasoningEfforts.Count>0?ModelCapabilityFlags.ReasoningVariants:ModelCapabilityFlags.None,
            report.ReasoningEfforts,[],[]) { DiscoverySource = report.Source },context,cancellationToken).ConfigureAwait(false);
    }
}
