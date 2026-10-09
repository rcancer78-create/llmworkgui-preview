using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class AdaptationResponseParserTests
{
    private readonly AdaptationResponseParser _parser = new();

    [Fact]
    public void Parse_RawJson_ParsesMappingsRationaleWarningsAndBlockers()
    {
        var parsed = _parser.Parse("""
            {
              "mappings": [
                {
                  "role": "Reviewer",
                  "originalRoute": "account-1:model-old",
                  "targetRoute": "account-2:model-new",
                  "targetModelId": "model-new",
                  "rationale": "Reviewer requires stronger reasoning.",
                  "isSemanticChange": false,
                  "blockerKind": null
                }
              ],
              "rationale": "Overall adaptation plan.",
              "warnings": ["warning-1"],
              "blockers": ["cannot map escalation"]
            }
            """);

        Assert.True(parsed.IsValidJson);
        Assert.Null(parsed.ParseError);
        Assert.Equal("Overall adaptation plan.", parsed.Rationale);
        Assert.Equal(new[] { "warning-1" }, parsed.Warnings);
        Assert.Equal(new[] { "cannot map escalation" }, parsed.Blockers);
        Assert.Empty(parsed.BlockerKinds);
        Assert.Empty(parsed.FileModifications);

        var mapping = Assert.Single(parsed.Mappings);

        Assert.Equal(WorkflowRole.Reviewer, mapping.Role);
        Assert.Equal("account-1:model-old", mapping.OriginalRoute);
        Assert.Equal("account-2:model-new", mapping.TargetRoute);
        Assert.Equal("model-new", mapping.TargetModelId);
        Assert.Equal("Reviewer requires stronger reasoning.", mapping.Rationale);
        Assert.False(mapping.IsSemanticChange);
        Assert.Null(mapping.BlockerKind);
    }

    [Fact]
    public void Parse_FencedJson_ExtractsJsonObject()
    {
        var parsed = _parser.Parse("""
            ```json
            {
              "mappings": [],
              "rationale": "Nothing to change.",
              "warnings": [],
              "blockers": []
            }
            ```
            """);

        Assert.True(parsed.IsValidJson);
        Assert.Empty(parsed.Mappings);
        Assert.Equal("Nothing to change.", parsed.Rationale);
    }

    [Fact]
    public void Parse_FencedJson_WithoutLanguageTag_IsAccepted()
    {
        var parsed = _parser.Parse("```\n{\"mappings\":[]}\n```");

        Assert.True(parsed.IsValidJson);
        Assert.Empty(parsed.Mappings);
    }

    [Theory]
    [InlineData("Coordinator")]
    [InlineData("coordinator")]
    [InlineData("COORDINATOR")]
    [InlineData("Executor")]
    [InlineData("executor")]
    [InlineData("Reviewer")]
    [InlineData("Escalation")]
    public void Parse_RoleNames_AreCaseInsensitive(string role)
    {
        var parsed = _parser.Parse($$"""
            {
              "mappings": [
                {
                  "role": "{{role}}",
                  "originalRoute": "route-old",
                  "targetRoute": "route-new",
                  "targetModelId": "model-1",
                  "rationale": "rationale"
                }
              ]
            }
            """);

        Assert.True(parsed.IsValidJson);
        Assert.Single(parsed.Mappings);
    }

    [Fact]
    public void Parse_ReadsFileModifications()
    {
        var parsed = _parser.Parse("""
            {
              "mappings": [],
              "fileModifications": {
                "prompts/executor.md": "You are the executor.",
                "README.md": ""
              }
            }
            """);

        Assert.True(parsed.IsValidJson);
        Assert.Equal(2, parsed.FileModifications.Count);
        Assert.Equal("You are the executor.", parsed.FileModifications["prompts/executor.md"]);
        Assert.Equal(string.Empty, parsed.FileModifications["README.md"]);
    }

    [Fact]
    public void Parse_MissingOptionalSections_ReturnsEmptyCollections()
    {
        var parsed = _parser.Parse("""{"mappings":[]}""");

        Assert.True(parsed.IsValidJson);
        Assert.Equal(string.Empty, parsed.Rationale);
        Assert.Empty(parsed.Warnings);
        Assert.Empty(parsed.Blockers);
        Assert.Empty(parsed.FileModifications);
    }

    [Fact]
    public void Parse_MissingIsSemanticChange_DefaultsToFalse()
    {
        var parsed = _parser.Parse("""
            {
              "mappings": [
                {
                  "role": "Executor",
                  "originalRoute": "route-old",
                  "targetRoute": "route-new",
                  "targetModelId": "model-1",
                  "rationale": "rationale"
                }
              ]
            }
            """);

        Assert.True(parsed.IsValidJson);
        Assert.False(Assert.Single(parsed.Mappings).IsSemanticChange);
    }

    [Theory]
    [InlineData("MissingModel")]
    [InlineData("missingmodel")]
    [InlineData("MissingCapability")]
    [InlineData("DisallowedSemanticChange")]
    [InlineData("DetectedSecret")]
    [InlineData("InvalidSchema")]
    [InlineData("Other")]
    public void Parse_ParsesBlockerKindIntoBlockerKinds(string blockerKind)
    {
        var parsed = _parser.Parse($$"""
            {
              "mappings": [
                {
                  "role": "Executor",
                  "originalRoute": "route-old",
                  "targetRoute": "route-new",
                  "targetModelId": "model-1",
                  "rationale": "rationale",
                  "blockerKind": "{{blockerKind}}"
                }
              ]
            }
            """);

        Assert.True(parsed.IsValidJson);
        var kind = Assert.Single(parsed.BlockerKinds);
        Assert.Equal(Enum.Parse<AdaptationBlockerKind>(blockerKind, ignoreCase: true), kind);
        Assert.Equal(kind, Assert.Single(parsed.Mappings).BlockerKind);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"mappings\": [ }")]
    [InlineData("```json\n{ broken\n```")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_MalformedOrBlankText_ReturnsInvalidSchema(string rawText)
    {
        var parsed = _parser.Parse(rawText);

        Assert.False(parsed.IsValidJson);
        Assert.NotNull(parsed.ParseError);
        Assert.Empty(parsed.Mappings);
        Assert.Empty(parsed.FileModifications);
        Assert.Equal(new[] { AdaptationBlockerKind.InvalidSchema }, parsed.BlockerKinds);
    }

    [Fact]
    public void Parse_NullText_ReturnsInvalidSchema()
    {
        var parsed = _parser.Parse(null!);

        Assert.False(parsed.IsValidJson);
        Assert.Equal(new[] { AdaptationBlockerKind.InvalidSchema }, parsed.BlockerKinds);
    }

    [Fact]
    public void Parse_MissingMappingsArray_ReturnsInvalidSchema()
    {
        var parsed = _parser.Parse("""{"rationale":"no mappings"}""");

        Assert.False(parsed.IsValidJson);
        Assert.Contains(AdaptationBlockerKind.InvalidSchema, parsed.BlockerKinds);
        Assert.Contains("mappings", parsed.ParseError!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RootArray_ReturnsInvalidSchema()
    {
        var parsed = _parser.Parse("""[{"role":"Executor"}]""");

        Assert.False(parsed.IsValidJson);
        Assert.Contains(AdaptationBlockerKind.InvalidSchema, parsed.BlockerKinds);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("originalRoute")]
    [InlineData("targetRoute")]
    [InlineData("targetModelId")]
    [InlineData("rationale")]
    public void Parse_BlankRequiredMappingField_ReturnsInvalidSchema(string blankField)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["role"] = "\"Executor\"",
            ["originalRoute"] = "\"route-old\"",
            ["targetRoute"] = "\"route-new\"",
            ["targetModelId"] = "\"model-1\"",
            ["rationale"] = "\"rationale\""
        };

        fields[blankField] = "\"   \"";

        var json = $$"""
            {
              "mappings": [
                {
                  "role": {{fields["role"]}},
                  "originalRoute": {{fields["originalRoute"]}},
                  "targetRoute": {{fields["targetRoute"]}},
                  "targetModelId": {{fields["targetModelId"]}},
                  "rationale": {{fields["rationale"]}}
                }
              ]
            }
            """;

        var parsed = _parser.Parse(json);

        Assert.False(parsed.IsValidJson);
        Assert.Contains(AdaptationBlockerKind.InvalidSchema, parsed.BlockerKinds);
        Assert.Contains(blankField, parsed.ParseError!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownRole_ReturnsInvalidSchema()
    {
        var parsed = _parser.Parse("""
            {
              "mappings": [
                {
                  "role": "Wizard",
                  "originalRoute": "route-old",
                  "targetRoute": "route-new",
                  "targetModelId": "model-1",
                  "rationale": "rationale"
                }
              ]
            }
            """);

        Assert.False(parsed.IsValidJson);
        Assert.Contains(AdaptationBlockerKind.InvalidSchema, parsed.BlockerKinds);
    }

    [Fact]
    public void Parse_UnknownBlockerKind_ReturnsInvalidSchema()
    {
        var parsed = _parser.Parse("""
            {
              "mappings": [
                {
                  "role": "Executor",
                  "originalRoute": "route-old",
                  "targetRoute": "route-new",
                  "targetModelId": "model-1",
                  "rationale": "rationale",
                  "blockerKind": "Exploded"
                }
              ]
            }
            """);

        Assert.False(parsed.IsValidJson);
        Assert.Contains(AdaptationBlockerKind.InvalidSchema, parsed.BlockerKinds);
    }

    [Fact]
    public void Parse_NonBooleanIsSemanticChange_ReturnsInvalidSchema()
    {
        var parsed = _parser.Parse("""
            {
              "mappings": [
                {
                  "role": "Executor",
                  "originalRoute": "route-old",
                  "targetRoute": "route-new",
                  "targetModelId": "model-1",
                  "rationale": "rationale",
                  "isSemanticChange": "yes"
                }
              ]
            }
            """);

        Assert.False(parsed.IsValidJson);
        Assert.Contains(AdaptationBlockerKind.InvalidSchema, parsed.BlockerKinds);
    }

    [Fact]
    public void Parse_NonStringFileModification_ReturnsInvalidSchema()
    {
        var parsed = _parser.Parse("""
            {
              "mappings": [],
              "fileModifications": {
                "README.md": 42
              }
            }
            """);

        Assert.False(parsed.IsValidJson);
        Assert.Contains(AdaptationBlockerKind.InvalidSchema, parsed.BlockerKinds);
        Assert.Empty(parsed.FileModifications);
    }

    [Fact]
    public void Parse_NonStringWarnings_ReturnsInvalidSchema()
    {
        var parsed = _parser.Parse("""{"mappings":[],"warnings":[42]}""");

        Assert.False(parsed.IsValidJson);
        Assert.Contains(AdaptationBlockerKind.InvalidSchema, parsed.BlockerKinds);
    }
}
