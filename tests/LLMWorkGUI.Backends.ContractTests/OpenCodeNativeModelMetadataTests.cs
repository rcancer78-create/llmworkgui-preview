using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class OpenCodeNativeModelMetadataTests
{
    [Theory]
    [InlineData("\"modelID\":\"m\",\"providerID\":\"p\"", "p", false)]
    [InlineData("\"model\":{\"modelID\":\"m\",\"providerID\":\"p\"}", "p", false)]
    [InlineData("\"modelID\":\"m\",\"providerID\":\"p\",\"model\":{\"providerID\":\"other\"}", null, true)]
    [InlineData("\"modelID\":\"m\",\"providerID\":\"p\",\"providerID\":\"p\"", null, true)]
    [InlineData("\"modelID\":\"m\",\"modelID\":\"m\"", null, true)]
    [InlineData("\"modelID\":\"m\",\"providerID\":3", null, true)]
    [InlineData("\"modelID\":\"m\",\"requestedProvider\":\"p\"", null, false)]
    public void ProviderObservationRejectsMalformedOrContradictoryIdentity(string fields, string? provider, bool invalid)
    {
        var json = "{\"info\":{\"id\":\"msg_native\",\"sessionID\":\"ses_native\",\"role\":\"assistant\"," + fields + "}}";
        using var document = JsonDocument.Parse(json);
        Assert.True(MessageUpdatedEvent.TryParse(new OpenCodeEventEnvelope("message.updated", document.RootElement.Clone(), json, DateTime.UtcNow), out var parsed));
        Assert.Equal(provider, parsed!.ProviderId);
        Assert.Equal(invalid, parsed.ModelMetadataInvalid);
        Assert.Equal(invalid ? null : "m", parsed.ModelId);
    }

    [Theory]
    // Flat modelID/providerID shape observed in the real 1.18.31 assistant
    // message: completion-20261008-r1/live-product-r2, isolated native database.
    [InlineData("\"modelID\":\"space-bunny-free\",\"providerID\":\"opencode\"", "space-bunny-free")]
    [InlineData("\"model\":{\"modelID\":\"nested-model\"}", "nested-model")]
    [InlineData("\"modelID\":\"same\",\"model\":{\"modelID\":\"same\"}", "same")]
    [InlineData("\"modelID\":\"flat\",\"model\":{\"modelID\":\"different\"}", null)]
    [InlineData("\"requestedModel\":\"requested-only\"", null)]
    [InlineData("\"modelID\":\"   \"", null)]
    public void ReportedModelUsesNativeMetadataAndRefusesContradiction(string fields, string? expected)
    {
        var json = "{\"info\":{\"id\":\"msg_native\",\"sessionID\":\"ses_native\",\"role\":\"assistant\"," + fields + "}}";
        using var document = JsonDocument.Parse(json);
        var envelope = new OpenCodeEventEnvelope("message.updated", document.RootElement.Clone(), json, DateTime.UtcNow);
        Assert.True(MessageUpdatedEvent.TryParse(envelope, out var parsed));
        Assert.Equal(expected, parsed!.ModelId);
    }
}
