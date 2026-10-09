using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpModelSelectorTests
{
    private static readonly CursorAcpModelCatalog Catalog =
        new CursorAcpModelCatalogService().Deserialize(CursorAcpTestData.ReadModelCatalogText());

    private readonly CursorAcpModelSelector _selector = new();

    [Fact]
    public void Format_WithoutOverrides_ReturnsPlainBaseModelId()
    {
        var formatted = _selector.Format("claude-4.6-sonnet", Array.Empty<CursorAcpOverrideValue>(), Catalog);

        Assert.Equal("claude-4.6-sonnet", formatted);
    }

    [Fact]
    public void Format_SingleOverride_AppendsBracketPair()
    {
        var formatted = _selector.Format(
            "cursor-grok-4.6-high",
            new[] { new CursorAcpOverrideValue { Name = "effort", Value = "high" } },
            Catalog);

        Assert.Equal("cursor-grok-4.6-high[effort=high]", formatted);
    }

    [Fact]
    public void Format_MultipleOverrides_SortsParametersAlphabetically()
    {
        var formatted = _selector.Format(
            "claude-4.6-sonnet",
            new[]
            {
                new CursorAcpOverrideValue { Name = "effort", Value = "medium" },
                new CursorAcpOverrideValue { Name = "context", Value = "1m" }
            },
            Catalog);

        Assert.Equal("claude-4.6-sonnet[context=1m,effort=medium]", formatted);
    }

    [Fact]
    public void Format_OrdersParametersWithOrdinalComparison()
    {
        var catalog = CreateCatalogWithParameters("Zulu", "alpha");

        var formatted = _selector.Format(
            "test-model",
            new[]
            {
                new CursorAcpOverrideValue { Name = "alpha", Value = "on" },
                new CursorAcpOverrideValue { Name = "Zulu", Value = "on" }
            },
            catalog);

        Assert.Equal("test-model[Zulu=on,alpha=on]", formatted);
    }

    [Fact]
    public void Format_SelectionRecord_FormatsLikeExplicitOverrides()
    {
        var selection = new CursorAcpModelSelection
        {
            BaseModelId = "gpt-5.3-codex",
            Overrides = new[]
            {
                new CursorAcpOverrideValue { Name = "effort", Value = "high" },
                new CursorAcpOverrideValue { Name = "context", Value = "272k" }
            }
        };

        Assert.Equal(
            "gpt-5.3-codex[context=272k,effort=high]",
            _selector.Format(selection, Catalog));
    }

    [Fact]
    public void Format_UnknownModel_IsRejected()
    {
        var exception = Assert.Throws<CursorAcpModelSelectionException>(
            () => _selector.Format("unknown-model", Array.Empty<CursorAcpOverrideValue>(), Catalog));

        Assert.Equal(CursorAcpSelectionFailureKind.UnknownModel, exception.Kind);
    }

    [Fact]
    public void Format_UnsupportedParameterState_IsRejected()
    {
        var exception = Assert.Throws<CursorAcpModelSelectionException>(() => _selector.Format(
            "gpt-5.3-codex",
            new[] { new CursorAcpOverrideValue { Name = "fast", Value = "on" } },
            Catalog));

        Assert.Equal(CursorAcpSelectionFailureKind.UnsupportedParameter, exception.Kind);
        Assert.Contains("Unknown", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fast", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_InvalidValue_IsRejected()
    {
        var exception = Assert.Throws<CursorAcpModelSelectionException>(() => _selector.Format(
            "cursor-grok-4.6-high",
            new[] { new CursorAcpOverrideValue { Name = "effort", Value = "extreme" } },
            Catalog));

        Assert.Equal(CursorAcpSelectionFailureKind.InvalidValue, exception.Kind);
        Assert.Contains("extreme", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_DuplicateParameter_IsRejected()
    {
        var exception = Assert.Throws<CursorAcpModelSelectionException>(() => _selector.Format(
            "gpt-5.3-codex",
            new[]
            {
                new CursorAcpOverrideValue { Name = "effort", Value = "high" },
                new CursorAcpOverrideValue { Name = "effort", Value = "low" }
            },
            Catalog));

        Assert.Equal(CursorAcpSelectionFailureKind.DuplicateParameter, exception.Kind);
    }

    [Fact]
    public void Parse_PlainBaseModelId_ReturnsSelectionWithoutOverrides()
    {
        var selection = _selector.Parse("claude-4.6-sonnet", Catalog);

        Assert.Equal("claude-4.6-sonnet", selection.BaseModelId);
        Assert.Empty(selection.Overrides);
    }

    [Fact]
    public void Parse_MultipleOverrides_ReturnsValidatedSelection()
    {
        var selection = _selector.Parse("claude-4.6-sonnet[context=1m,effort=medium]", Catalog);

        Assert.Equal("claude-4.6-sonnet", selection.BaseModelId);
        Assert.Equal(2, selection.Overrides.Count);
        Assert.Contains(
            selection.Overrides,
            value => value.Name == "context" && value.Value == "1m");
        Assert.Contains(
            selection.Overrides,
            value => value.Name == "effort" && value.Value == "medium");
    }

    [Fact]
    public void Parse_UnsortedParameters_AreAcceptedAndNormalizedByFormat()
    {
        var selection = _selector.Parse("claude-4.6-sonnet[effort=high,context=1m]", Catalog);

        Assert.Equal("claude-4.6-sonnet[context=1m,effort=high]", _selector.Format(selection, Catalog));
    }

    [Fact]
    public void Parse_OutputOfFormat_RoundTrips()
    {
        var wire = _selector.Format(
            "gpt-5.3-codex",
            new[]
            {
                new CursorAcpOverrideValue { Name = "effort", Value = "low" },
                new CursorAcpOverrideValue { Name = "context", Value = "272k" }
            },
            Catalog);

        var parsed = _selector.Parse(wire, Catalog);

        Assert.Equal(wire, _selector.Format(parsed, Catalog));
    }

    [Fact]
    public void Parse_DuplicateParameter_IsRejected()
    {
        var exception = Assert.Throws<CursorAcpModelSelectionException>(
            () => _selector.Parse("gpt-5.3-codex[effort=high,effort=low]", Catalog));

        Assert.Equal(CursorAcpSelectionFailureKind.DuplicateParameter, exception.Kind);
    }

    [Fact]
    public void Parse_UnknownModel_IsRejected()
    {
        var exception = Assert.Throws<CursorAcpModelSelectionException>(
            () => _selector.Parse("unknown-model[effort=high]", Catalog));

        Assert.Equal(CursorAcpSelectionFailureKind.UnknownModel, exception.Kind);
    }

    [Fact]
    public void Parse_UnsupportedFastParameter_IsRejected()
    {
        var exception = Assert.Throws<CursorAcpModelSelectionException>(
            () => _selector.Parse("gpt-5.3-codex[fast=on]", Catalog));

        Assert.Equal(CursorAcpSelectionFailureKind.UnsupportedParameter, exception.Kind);
    }

    [Fact]
    public void Parse_InvalidValue_IsRejected()
    {
        var exception = Assert.Throws<CursorAcpModelSelectionException>(
            () => _selector.Parse("cursor-grok-4.6-high[effort=extreme]", Catalog));

        Assert.Equal(CursorAcpSelectionFailureKind.InvalidValue, exception.Kind);
    }

    [Fact]
    public void Validate_ContextOnGrok_IsRejectedEvenThoughRootParameterStatesListIt()
    {
        var exception = Assert.Throws<CursorAcpModelSelectionException>(() => _selector.Validate(
            "cursor-grok-4.6-high",
            new[] { new CursorAcpOverrideValue { Name = "context", Value = "200k" } },
            Catalog));

        Assert.Equal(CursorAcpSelectionFailureKind.UnknownParameter, exception.Kind);
        Assert.Contains("parameterStates", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("claude-4.6-sonnet[]")]
    [InlineData("claude-4.6-sonnet[effort]")]
    [InlineData("claude-4.6-sonnet[=medium]")]
    [InlineData("claude-4.6-sonnet[effort=]")]
    [InlineData("claude-4.6-sonnet[effort=medium, ]")]
    [InlineData(" claude-4.6-sonnet[effort=medium]")]
    [InlineData("claude-4.6-sonnet[ effort=medium]")]
    [InlineData("claude-4.6-sonnet[effort =medium]")]
    [InlineData("claude-4.6-sonnet[effort= medium]")]
    [InlineData("claude-4.6-sonnet[effort=medium]x")]
    [InlineData("claude-4.6-sonnet[effort=medium")]
    [InlineData("claude-4.6-sonnet]effort=medium[")]
    [InlineData("claude-4.6-sonnet[effort=medium][context=1m]")]
    [InlineData("claude-4.6-sonnet[effort=medium=high]")]
    public void Parse_MalformedSyntax_IsRejected(string selection)
    {
        var exception = Assert.Throws<CursorAcpModelSelectionException>(
            () => _selector.Parse(selection, Catalog));

        Assert.Equal(CursorAcpSelectionFailureKind.MalformedSyntax, exception.Kind);
    }

    private static CursorAcpModelCatalog CreateCatalogWithParameters(params string[] parameterNames) =>
        new()
        {
            SchemaVersion = 1,
            Protocol = "cursor-agent-acp",
            AgentVersion = "test",
            Models = new[]
            {
                new CursorAcpModelInfo
                {
                    ModelId = "test-model",
                    Family = "test",
                    DisplayName = "Test Model",
                    DefaultContext = "1k",
                    MaxContext = "1k",
                    Overrides = parameterNames
                        .Select(name => new CursorAcpOverrideDefinition
                        {
                            Name = name,
                            Values = new[] { "on" },
                            Default = "on",
                            CapabilityState = CapabilityState.Supported
                        })
                        .ToArray()
                }
            }
        };
}
