using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Infrastructure.Mirasim;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class BackendPublicReviewContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void MirasimClient_RejectsUnboundedOrInvalidTimeoutBeforeRequests(int milliseconds)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new MirasimClient(http,
            Options.Create(new MirasimOptions { RequestTimeout = TimeSpan.FromMilliseconds(milliseconds) })));
        Assert.Equal(nameof(MirasimOptions.RequestTimeout), error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Once")]
    [InlineData("allow")]
    [InlineData("unknown")]
    public void PermissionSerializer_RejectsResponsesOutsideCapturedClosedSchema(string response)
    {
        Assert.Throws<ArgumentException>(() => OpenCodeSessionJson.SerializePermissionReply(
            new OpenCodePermissionReply { Response = response }));
    }

    [Fact]
    public void PermissionStandardResponses_CannotBeMutatedToAuthorizeAnotherNativeResponse()
    {
        const string unsupported = "synthetic-unsupported-response";
        var standards = OpenCodePermissionReply.StandardResponses;
        if (standards is ISet<string> mutable)
        {
            try
            {
                Assert.IsAssignableFrom<NotSupportedException>(Record.Exception(() => mutable.Add(unsupported)));
            }
            finally { if (mutable.Contains(unsupported)) mutable.Remove(unsupported); }
        }
        Assert.False(new OpenCodePermissionReply { Response = unsupported }.IsStandard);
        Assert.Equal(3, standards.Count);
    }

    [Fact]
    public void EmptyDefaultModelMap_CannotBeMutatedAcrossNewDiscoveryResponses()
    {
        const string provider = "synthetic-mutation";
        var defaults = OpenCodeConfiguredProvidersResponse.EmptyDefaultModels;
        if (defaults is IDictionary<string, string> mutable)
        {
            try
            {
                Assert.IsAssignableFrom<NotSupportedException>(Record.Exception(() => mutable.Add(provider, "model")));
            }
            finally { if (mutable.ContainsKey(provider)) mutable.Remove(provider); }
        }
        Assert.Empty(new OpenCodeConfiguredProvidersResponse().DefaultModels);
    }

    [Theory]
    [InlineData("{\"input\":-1}")]
    [InlineData("{\"output\":-1}")]
    [InlineData("{\"reasoning\":-1}")]
    [InlineData("{\"cache\":{\"read\":-1}}")]
    [InlineData("{\"cache\":{\"write\":-1}}")]
    [InlineData("{\"input\":\"malformed\"}")]
    [InlineData("{\"output\":true}")]
    [InlineData("{\"reasoning\":[]}")]
    [InlineData("{\"cache\":42}")]
    [InlineData("{\"cache\":{\"read\":{}}}")]
    public void TokenUsage_RejectsPresentInvalidCountersInsteadOfPublishingSuccess(string tokens)
    {
        using var document = JsonDocument.Parse("{\"tokens\":" + tokens + "}");
        Assert.False(OpenCodeTokenUsage.TryParse(document.RootElement, out var usage));
        Assert.Null(usage);
    }

    [Fact]
    public void TokenUsage_PreservesSparseZeroDefaultsAndNativeNonnegativeAccounting()
    {
        using var document = JsonDocument.Parse("""{"tokens":{"input":12,"cache":{"read":3}}}""");
        Assert.True(OpenCodeTokenUsage.TryParse(document.RootElement, out var usage));
        Assert.Equal(new OpenCodeTokenUsage(12, 0, 0, 3, 0), usage);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unsupported")]
    [InlineData("unknown")]
    public void DiscoveryCapabilities_UnknownStringCannotAdvertiseNativeSupport(string unknown)
    {
        var json = "{\"models\":[{\"id\":\"synthetic-model\",\"capabilities\":{\"vision\":\"" + unknown +
            "\",\"tools\":true,\"reasoning\":\"true\",\"audio\":false}}]}";
        var model = Assert.Single(OpenCodeDiscoveryJson.ParseModels(json));
        Assert.DoesNotContain("vision", model.Capabilities);
        Assert.Contains("tools", model.Capabilities);
        Assert.Contains("reasoning", model.Capabilities);
        Assert.DoesNotContain("audio", model.Capabilities);
    }
}
