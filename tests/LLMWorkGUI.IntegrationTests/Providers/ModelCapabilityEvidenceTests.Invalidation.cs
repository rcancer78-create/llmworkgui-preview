using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class ModelCapabilityEvidenceTests
{
    [Theory]
    [InlineData("UPDATE Accounts SET ProviderNativeId='other-native' WHERE Id='account-1'")]
    [InlineData("UPDATE Accounts SET GatewayNativeId='other-gateway' WHERE Id='account-1'")]
    [InlineData("UPDATE Accounts SET SecretReference='urn:llmworkgui:secret:new' WHERE Id='account-1'")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown' WHERE Id='account-1'")]
    [InlineData("UPDATE ProviderProfiles SET BaseUrl='https://other.example/v1' WHERE Id='provider-1'")]
    [InlineData("UPDATE ProviderProfiles SET ExecutablePath='D:\\other\\client.exe' WHERE Id='provider-1'")]
    [InlineData("UPDATE ProviderProfiles SET ApiKeySecretReference='urn:llmworkgui:secret:new' WHERE Id='provider-1'")]
    [InlineData("UPDATE ProviderProfiles SET GatewayNativeId='other-provider' WHERE Id='provider-1'")]
    [InlineData("UPDATE ProviderProfiles SET CustomHeadersJson='[]' WHERE Id='provider-1'")]
    [InlineData("UPDATE Models SET ProviderModelId='other-native' WHERE Id='model-1'")]
    [InlineData("UPDATE Models SET GatewayNativeId='other-model' WHERE Id='model-1'")]
    public async Task ContextChangeMakesEvidenceStaleAndCannotBeUndoneByOldEditor(string sql)
    {
        await Ready(); await SaveFresh(Evidence());
        var expected = Assert.Single((await Configuration.ReadAsync()).Capabilities);
        await Execute(sql);
        var stale = Assert.Single((await Configuration.ReadAsync()).Capabilities);
        Assert.Equal(CapabilityState.Stale, stale.State);
        Assert.Equal(expected.ReasoningEfforts, stale.ReasoningEfforts);
        Assert.NotNull(await Reject());
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveUserFresh(Evidence(), expected));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveFresh(Evidence()));
    }

    [Fact]
    public async Task AccountInvalidationDoesNotInvalidateOtherAccountsOnTheProvider()
    {
        await Ready();
        await Execute("""
            INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,AuthState,Health,CreatedAtUtc,UpdatedAtUtc)
            SELECT 'other-account',ProviderProfileId,'Other',AuthState,Health,CreatedAtUtc,UpdatedAtUtc FROM Accounts WHERE Id='account-1'
            """);
        await SaveFresh(Evidence());
        await SaveFresh(Evidence() with { AccountId = "other-account" });
        await Execute("UPDATE Accounts SET AuthState='Unknown' WHERE Id='account-1'");
        var rows = (await Configuration.ReadAsync()).Capabilities;
        Assert.Equal(CapabilityState.Stale, rows.Single(item => item.AccountId == "account-1").State);
        Assert.Equal(CapabilityState.Supported, rows.Single(item => item.AccountId == "other-account").State);
    }

    [Theory]
    [InlineData("UPDATE Accounts SET DisplayName='Renamed',ManualPriority=7,UpdatedAtUtc='2026-10-07' WHERE Id='account-1'")]
    [InlineData("UPDATE Accounts SET AuthState=AuthState,SecretReference=SecretReference WHERE Id='account-1'")]
    [InlineData("UPDATE ProviderProfiles SET DisplayName='Renamed',UpdatedAtUtc='2026-10-07' WHERE Id='provider-1'")]
    [InlineData("UPDATE Models SET DisplayName='Renamed' WHERE Id='model-1'")]
    public async Task DisplayAndNoOpChangesPreserveEvidence(string sql)
    {
        await Ready(); await SaveFresh(Evidence()); await Execute(sql);
        Assert.Null(await Reject());
    }

    [Fact]
    public async Task FailedContextMutationRollsBackItsCapabilityInvalidation()
    {
        await Ready(); await SaveFresh(Evidence());
        await using var connection = await _db.Factory.OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE Accounts SET AuthState='Unknown' WHERE Id='account-1'";
        await command.ExecuteNonQueryAsync();
        transaction.Rollback();
        Assert.Null(await Reject());
    }

    [Fact]
    public async Task MalformedPayloadCannotBreakCredentialChangeAndDeletedAccountCannotReviveEvidence()
    {
        await Ready(); await SaveFresh(Evidence());
        await Execute("UPDATE ModelCapabilities SET CapabilityValue='bad-json'; UPDATE Accounts SET AuthState='Unknown'");
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
        await Execute("DELETE FROM ModelCapabilities");
        await SaveFresh(Evidence());
        await Execute("DELETE FROM Accounts WHERE Id='account-1'");
        await Execute("""
            INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,AuthState,Health,CreatedAtUtc,UpdatedAtUtc)
            VALUES ('account-1','provider-1','Recreated','Valid','Healthy','2026-10-07T12:00:00.0000000+00:00','2026-10-07T12:00:00.0000000+00:00')
            """);
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
    }

    [Theory]
    [InlineData("UPDATE SecretReferences SET LastRotatedAtUtc='2026-10-07T12:00:00.0000000+00:00'")]
    [InlineData("UPDATE SecretReferences SET LastRotatedAtUtc=LastRotatedAtUtc")]
    [InlineData("UPDATE SecretReferences SET State='Revoked',RevokedAtUtc='2026-10-07T12:00:00.0000000+00:00'")]
    [InlineData("DELETE FROM SecretReferences")]
    [InlineData("DELETE FROM SecretReferenceOwners")]
    [InlineData("UPDATE SecretReferenceOwners SET OwnerId='other-provider'")]
    public async Task HeaderCredentialChangesInvalidateAllModelsOfItsOwningProvider(string sql)
    {
        await Ready();
        await Execute("""
            INSERT INTO SecretReferences (Reference,Kind,State,CreatedAtUtc)
            VALUES ('urn:llmworkgui:secret:header','ProviderApiKey','Active','2026-10-07T12:00:00.0000000+00:00');
            INSERT INTO SecretReferenceOwners (Reference,OwnerKind,OwnerId)
            VALUES ('urn:llmworkgui:secret:header','ProviderProfile','provider-1');
            """);
        await SaveFresh(Evidence()); await Execute(sql);
        Assert.Equal(CapabilityState.Stale, Assert.Single((await Configuration.ReadAsync()).Capabilities).State);
        Assert.NotNull(await Reject());
    }

    [Fact]
    public async Task CredentialMetadataCreationInvalidatesPreviouslyDeclaredOptions()
    {
        await Ready();
        await Execute("UPDATE Accounts SET SecretReference='urn:llmworkgui:secret:restored' WHERE Id='account-1'");
        await SaveFresh(Evidence());
        await Execute("""
            INSERT INTO SecretReferences (Reference,Kind,State,CreatedAtUtc)
            VALUES ('urn:llmworkgui:secret:restored','ProviderApiKey','Active','2026-10-07T12:00:00.0000000+00:00')
            """);
        Assert.Equal(CapabilityState.Stale, Assert.Single((await Configuration.ReadAsync()).Capabilities).State);
    }

    [Fact]
    public async Task UpgradeFrom28PreservesDeclarationsButRequiresReconfirmation()
    {
        var migrations = DatabaseMigrator.LoadEmbeddedMigrations();
        await new DatabaseMigrator(_db.Factory, migrations.Where(item => item.Version <= 28).ToArray()).MigrateAsync();
        await _db.SeedRouteChainAsync();
        _guard = new LLMWorkGUI.Infrastructure.Concurrency.ApplicationInstanceGuard(_db.Root);
        // Use the old schema's payload directly; the current writer requires schema30 context.
        await using (var connection = await _db.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO ModelCapabilities (Id,ModelId,CapabilityKey,CapabilityValue,State,Provenance,UpdatedAtUtc)
                VALUES ('legacy-evidence','model-1',$key,$payload,'Supported','ProviderReported',$time)
                """;
            command.Parameters.AddWithValue("$key", "llmworkgui.evidence.v1/" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("account-1")));
            command.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(Evidence()));
            command.Parameters.AddWithValue("$time", Evidence().ObservedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }
        await new DatabaseMigrator(_db.Factory).MigrateAsync();
        var stale = Assert.Single((await Configuration.ReadAsync()).Capabilities);
        Assert.Equal(CapabilityState.Stale, stale.State); Assert.Equal(Evidence().ReasoningEfforts, stale.ReasoningEfforts);
        await SaveFresh(stale); // Replaying the normalized stale row remains stale.
        await SaveUserFresh(stale with { State = CapabilityState.Supported }, stale);
        Assert.Equal(ModelProvenance.UserDefined, Assert.Single((await Configuration.ReadAsync()).Capabilities).Provenance);
    }
}
