using LLMWorkGUI.Application.ReviewerIdentity;
using Xunit;

namespace LLMWorkGUI.Application.Tests.ReviewerIdentity;

public sealed class ObservationChunkRetentionRegressionTests
{
    [Fact]
    public void SuccessfulMergeRetainsImmutableExactObservedChunks()
    {
        var provider = new GatewayRouteObservationChunk(nativeProviderId: "provider");
        var model = new GatewayRouteObservationChunk(nativeModelId: "model");
        var source = new List<GatewayRouteObservationChunk> { provider, model };
        var merged = GatewayRouteObservation.Merge(source);
        Assert.True(merged.Agrees);
        Assert.Equal(source, merged.Chunks);
        source.Clear();
        Assert.Equal(2, merged.Chunks.Count);
        Assert.Same(provider, merged.Chunks[0]);
        Assert.Same(model, merged.Chunks[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<GatewayRouteObservationChunk>)merged.Chunks).Clear());
        var conflict = GatewayRouteObservation.Merge([provider,
            new GatewayRouteObservationChunk(nativeProviderId: "other")]);
        Assert.False(conflict.Agrees);
        Assert.Empty(conflict.Chunks);
    }
}
