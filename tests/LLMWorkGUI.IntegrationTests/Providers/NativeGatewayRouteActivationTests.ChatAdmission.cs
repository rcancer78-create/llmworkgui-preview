using System.Text.Json;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class NativeGatewayRouteActivationTests
{
    private async Task ActivateForChatAdmission()
    {
        await Ready();
        Directory.CreateDirectory(_db.GetWorkspacePath());
        await Sql("UPDATE Projects SET DataClassification='PublicSource'");
        var preview = await Service.PreviewAsync("route-1", Email);
        await Service.ActivateAsync(preview.ObservationId, true, true, DataClassification.PublicSource);
    }

    private Task<IReadOnlyList<NativeGatewayRouteOption>> ChatRoutes() =>
        new SqliteNativeGatewayRouteCatalog(_db.Factory, new SensitiveDataFilter(), _clock)
            .ListAsync("project-1", _db.GetWorkspacePath());

    private async Task RemoveChatAuthority(string mutation)
    {
        if (mutation == "expired") { _clock.Now += TimeSpan.FromHours(24); return; }
        if (mutation == "missing") { await Sql("DELETE FROM ModelCapabilities"); return; }
        var evidence = JsonSerializer.Deserialize<ModelCapabilityEvidence>((string)(await Sql("SELECT CapabilityValue FROM ModelCapabilities"))!)!;
        evidence = evidence with { Flags = ModelCapabilityFlags.None };
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ModelCapabilities SET CapabilityValue=$value";
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(evidence));
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("no-chat")]
    public async Task CursorUserDeclarationMustBeCurrentForCatalogAndJournalCreation(string mutation)
    {
        await ActivateForChatAdmission();
        Assert.Single(await ChatRoutes());
        await RemoveChatAuthority(mutation);
        Assert.Empty(await ChatRoutes());
        var journal = new SqliteNativeGatewayJournal(_db.Factory, _guard, _clock);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.BeginAsync(
            new("project-1", _db.GetWorkspacePath(), "route-1", Guid.NewGuid().ToString(), "synthetic prompt"), CancellationToken.None));
        Assert.Contains("Поддержка текстовых запросов", error.Message);
        Assert.Equal(0L, Convert.ToInt64(await Sql("SELECT COUNT(*) FROM Executions")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("no-chat")]
    public async Task CursorChatDeclarationIsRevalidatedBeforeDispatchAndMarkRunning(string mutation)
    {
        await ActivateForChatAdmission();
        var journal = new SqliteNativeGatewayJournal(_db.Factory, _guard, _clock);
        var entry = await journal.BeginAsync(new("project-1", _db.GetWorkspacePath(), "route-1", Guid.NewGuid().ToString(), "synthetic prompt"), CancellationToken.None);
        await RemoveChatAuthority(mutation);
        var dispatchError = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ValidateDispatchAsync(entry, CancellationToken.None));
        Assert.Contains("Подтверждение текстовых запросов", dispatchError.Message);
        var runningError = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MarkRunningAsync(entry, CancellationToken.None));
        Assert.Contains("Подтверждение текстовых запросов", runningError.Message);
        Assert.Equal("Starting", await Sql("SELECT State FROM Executions"));
    }

    [Fact]
    public async Task CursorLegacyPluginReportedTextRouteKeepsExistingAdmissionWithoutDeclaration()
    {
        await ActivateForChatAdmission();
        await Sql("DELETE FROM ModelCapabilities; UPDATE Models SET Provenance='PluginReported'");
        Assert.Single(await ChatRoutes());
        var journal = new SqliteNativeGatewayJournal(_db.Factory, _guard, _clock);
        var entry = await journal.BeginAsync(new("project-1", _db.GetWorkspacePath(), "route-1", Guid.NewGuid().ToString(), "synthetic prompt"), CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal("Starting", await Sql("SELECT State FROM Executions"));
    }
}
