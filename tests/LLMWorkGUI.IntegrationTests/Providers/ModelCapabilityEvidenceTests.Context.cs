using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class ModelCapabilityEvidenceTests
{
    [Theory]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'; UPDATE Accounts SET AuthState='Valid'")]
    [InlineData("UPDATE Accounts SET ProviderNativeId='different'; UPDATE Accounts SET ProviderNativeId=NULL")]
    [InlineData("UPDATE Accounts SET GatewayNativeId='different'")]
    [InlineData("UPDATE Accounts SET SecretReference='urn:llmworkgui:secret:replacement'")]
    [InlineData("UPDATE ProviderProfiles SET BaseUrl='https://other.example'; UPDATE ProviderProfiles SET BaseUrl=NULL")]
    [InlineData("UPDATE ProviderProfiles SET ExecutablePath='D:\\other.exe'")]
    [InlineData("UPDATE ProviderProfiles SET GatewayNativeId='different'")]
    [InlineData("UPDATE ProviderProfiles SET CustomHeadersJson='[]'")]
    [InlineData("UPDATE ProviderProfiles SET ApiKeySecretReference='urn:llmworkgui:secret:replacement'")]
    [InlineData("UPDATE Models SET ProviderModelId='changed'; UPDATE Models SET ProviderModelId='model-1'")]
    [InlineData("UPDATE Models SET GatewayNativeId='different'")]
    [InlineData("UPDATE ProviderProfiles SET Backend='CursorAcp'; UPDATE Models SET Backend='CursorAcp'; UPDATE ProviderProfiles SET Backend='OpenCode'; UPDATE Models SET Backend='OpenCode'")]
    public async Task LateFirstObservationCannotCrossContextChanges(string sql)
    {
        await Ready();
        var captured = await Store.CaptureContextAsync("model-1", "account-1");
        await Execute(sql);
        Assert.NotEqual(captured, await Store.CaptureContextAsync("model-1", "account-1"));
        // There has never been an evidence row to mark Stale. Even a newer report is rejected.
        var report = Evidence() with { ObservedAtUtc = Now };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(report, captured));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveUserDeclarationAsync(report, null, captured));
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
        await SaveFresh(report);
        Assert.Equal(CapabilityState.Supported, Assert.Single((await Configuration.ReadAsync()).Capabilities).State);
    }

    [Theory]
    [InlineData("Account")]
    [InlineData("ProviderProfile")]
    public async Task EveryCredentialEventInvalidatesAnObservationWithoutAnEvidenceRow(string ownerKind)
    {
        await Ready();
        var ownerId = ownerKind == "Account" ? "account-1" : "provider-1";
        await Execute("""
            INSERT INTO SecretReferences (Reference,Kind,State,CreatedAtUtc)
            VALUES ('urn:llmworkgui:secret:context','ProviderApiKey','Active','2026-10-07T12:00:00.0000000+00:00');
            """);
        foreach (var sql in new[]
        {
            $"INSERT INTO SecretReferenceOwners (Reference,OwnerKind,OwnerId) VALUES ('urn:llmworkgui:secret:context','{ownerKind}','{ownerId}')",
            "UPDATE SecretReferences SET LastRotatedAtUtc=LastRotatedAtUtc",
            "UPDATE SecretReferences SET LastRotatedAtUtc='2026-10-07T12:01:00.0000000+00:00'",
            "UPDATE SecretReferences SET State='Revoked',RevokedAtUtc='2026-10-07T12:02:00.0000000+00:00'",
            "DELETE FROM SecretReferences"
        })
        {
            var before = await Store.CaptureContextAsync("model-1", "account-1");
            await Execute(sql);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(Evidence(), before));
            Assert.Empty((await Configuration.ReadAsync()).Capabilities);
        }
    }

    [Theory]
    [InlineData("UPDATE Accounts SET SecretReference='urn:llmworkgui:secret:restored'")]
    [InlineData("UPDATE ProviderProfiles SET ApiKeySecretReference='urn:llmworkgui:secret:restored'")]
    public async Task CredentialCreationInvalidatesPendingReports(string referenceSetup)
    {
        await Ready(); await Execute(referenceSetup);
        var captured = await Store.CaptureContextAsync("model-1", "account-1");
        await Execute("""
            INSERT INTO SecretReferences (Reference,Kind,State,CreatedAtUtc)
            VALUES ('urn:llmworkgui:secret:restored','ProviderApiKey','Active','2026-10-07T12:00:00.0000000+00:00')
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(Evidence(), captured));
    }

    [Theory]
    [InlineData("Accounts")]
    [InlineData("Models")]
    public async Task RecreatedIdentityCannotReuseCapturedContext(string table)
    {
        await Ready();
        var captured = await Store.CaptureContextAsync("model-1", "account-1");
        // Deliberately copy all fields, including the old revision, to exercise the insert trigger.
        await Execute($"CREATE TEMP TABLE old_identity AS SELECT * FROM {table}; DELETE FROM {table}; INSERT INTO {table} SELECT * FROM old_identity");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(Evidence(), captured));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveUserDeclarationAsync(Evidence(), null, captured));
        await SaveFresh(Evidence());
        Assert.Single((await Configuration.ReadAsync()).Capabilities);
    }

    [Fact]
    public async Task OtherAccountAndCosmeticChangesDoNotCancelTheObservation()
    {
        await Ready();
        await Execute("""
            INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,AuthState,Health,CreatedAtUtc,UpdatedAtUtc)
            SELECT 'other-account',ProviderProfileId,'Other',AuthState,Health,CreatedAtUtc,UpdatedAtUtc FROM Accounts
            """);
        var captured = await Store.CaptureContextAsync("model-1", "account-1");
        await Execute("""
            UPDATE Accounts SET AuthState='Unknown' WHERE Id='other-account';
            UPDATE Accounts SET DisplayName='Renamed',AuthState=AuthState,SecretReference=SecretReference;
            UPDATE Models SET DisplayName='Renamed';
            UPDATE ProviderProfiles SET DisplayName='Renamed',BaseUrl=BaseUrl;
            """);
        Assert.Equal(captured, await Store.CaptureContextAsync("model-1", "account-1"));
        await Store.SaveAsync(Evidence(), captured);
        Assert.Null(await Reject());
    }

    [Fact]
    public async Task ContextAndEvidenceInvalidationRollbackTogether()
    {
        await Ready(); await SaveFresh(Evidence());
        var captured = await Store.CaptureContextAsync("model-1", "account-1");
        await using (var connection = await _db.Factory.OpenConnectionAsync())
        using (var transaction = connection.BeginTransaction())
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE Accounts SET AuthState='Unknown'; UPDATE Models SET ProviderModelId='changed'";
            await command.ExecuteNonQueryAsync(); transaction.Rollback();
        }
        Assert.Equal(captured, await Store.CaptureContextAsync("model-1", "account-1"));
        await Store.SaveAsync(Evidence(), captured);
        Assert.Null(await Reject());
    }

    [Fact]
    public async Task EmptyOrMismatchedTokensNeverAuthorizeWrites()
    {
        await Ready();
        var captured = await Store.CaptureContextAsync("model-1", "account-1");
        foreach (var context in new[]
        {
            captured with { AccountRevision = "" }, captured with { ModelRevision = "" },
            captured with { AccountId = "other" }, captured with { ModelId = "other" }
        }) await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(Evidence(), context));
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
    }

    [Fact]
    public async Task LateReportCannotReviveStaleEvidenceEvenWithANewerTimestamp()
    {
        await Ready();
        var captured = await Store.CaptureContextAsync("model-1", "account-1");
        await Store.SaveAsync(Evidence(), captured);
        await Execute("UPDATE Accounts SET AuthState='Unknown'; UPDATE Accounts SET AuthState='Valid'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(Evidence() with { ObservedAtUtc = Now }, captured));
        Assert.Equal(CapabilityState.Stale, Assert.Single((await Configuration.ReadAsync()).Capabilities).State);
        await SaveFresh(Evidence() with { ObservedAtUtc = Now });
        Assert.Null(await Reject());
    }

    [Fact]
    public async Task MovingIdentityAwayAndBackInvalidatesBothModelAndAccountCaptures()
    {
        await Ready();
        await Execute("""
            INSERT INTO ProviderProfiles (Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
            SELECT 'other-provider','Other',Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc FROM ProviderProfiles;
            """);
        foreach (var table in new[] { "Accounts", "Models" })
        {
            var captured = await Store.CaptureContextAsync("model-1", "account-1");
            await Execute($"UPDATE {table} SET ProviderProfileId='other-provider'; UPDATE {table} SET ProviderProfileId='provider-1'");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(Evidence(), captured));
        }
        Assert.Empty((await Configuration.ReadAsync()).Capabilities);
    }

    [Fact]
    public async Task MovingHeaderCredentialOwnershipInvalidatesBothAffectedProviders()
    {
        await Ready();
        await _db.SeedRouteChainAsync(projectId: "project-2", projectRootPath: Path.Combine(_db.Root, "other-workspace"),
            providerProfileId: "provider-2", accountId: "account-2", modelId: "model-2", routeId: "route-2");
        await Execute("""
            INSERT INTO SecretReferences (Reference,Kind,State,CreatedAtUtc)
            VALUES ('urn:llmworkgui:secret:header','ProviderApiKey','Active','2026-10-07T12:00:00.0000000+00:00');
            INSERT INTO SecretReferenceOwners (Reference,OwnerKind,OwnerId)
            VALUES ('urn:llmworkgui:secret:header','ProviderProfile','provider-1');
            """);
        var first = await Store.CaptureContextAsync("model-1", "account-1");
        var second = await Store.CaptureContextAsync("model-2", "account-2");
        await Execute("UPDATE SecretReferenceOwners SET OwnerId='provider-2'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(Evidence(), first));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(Evidence() with { ModelId="model-2",AccountId="account-2" }, second));
        var fresh = await Store.CaptureContextAsync("model-2", "account-2");
        await Execute("DELETE FROM SecretReferenceOwners");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveAsync(Evidence() with { ModelId="model-2",AccountId="account-2" }, fresh));
    }

    [Fact]
    public async Task UpgradeFrom29SeedsRevisionsAndSnapshotMatchesCapturedContext()
    {
        var migrations = DatabaseMigrator.LoadEmbeddedMigrations();
        await new DatabaseMigrator(_db.Factory, migrations.Where(item => item.Version <= 29).ToArray()).MigrateAsync();
        await _db.SeedRouteChainAsync();
        _guard = new LLMWorkGUI.Infrastructure.Concurrency.ApplicationInstanceGuard(_db.Root);
        await new DatabaseMigrator(_db.Factory).MigrateAsync();
        var captured = await Store.CaptureContextAsync("model-1", "account-1");
        var snapshot = await Configuration.ReadAsync();
        Assert.True(Guid.TryParseExact(captured.AccountRevision, "N", out _));
        Assert.True(Guid.TryParseExact(captured.ModelRevision, "N", out _));
        Assert.Equal(captured.AccountRevision, Assert.Single(snapshot.Accounts).CapabilityRevision);
        Assert.Equal(captured.ModelRevision, Assert.Single(snapshot.Models).CapabilityRevision);
        await new DatabaseMigrator(_db.Factory).MigrateAsync();
        Assert.Equal(captured, await Store.CaptureContextAsync("model-1", "account-1"));
        await Store.SaveAsync(Evidence(), captured);
    }
}
