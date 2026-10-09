using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class AdaptationReferenceValidatorTests
{
    private readonly AdaptationReferenceValidator _validator = new();

    [Fact]
    public void Validate_KnownRoutableModel_ReturnsValidResult()
    {
        var catalog = CreateCatalog(CreateModel("model-1", isRoutable: true));

        var result = _validator.Validate(
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            catalog);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.Empty(result.BlockerKinds);
    }

    [Theory]
    [InlineData(HealthState.Healthy)]
    [InlineData(HealthState.Degraded)]
    [InlineData(HealthState.ForcedEnabled)]
    public void Validate_RoutableModel_IsAcceptedRegardlessOfHealthMetadata(HealthState health)
    {
        var catalog = CreateCatalog(CreateModel("model-1", isRoutable: true, health));

        var result = _validator.Validate(
            new[] { CreateMapping(WorkflowRole.Reviewer, "model-1") },
            catalog);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ModelAbsentFromCatalog_AddsMissingModel()
    {
        var catalog = CreateCatalog(CreateModel("model-1", isRoutable: true));

        var result = _validator.Validate(
            new[] { CreateMapping(WorkflowRole.Reviewer, "model-unknown") },
            catalog);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.MissingModel, issue.Kind);
        Assert.Equal("Reviewer", issue.Role);
        Assert.Contains("model-unknown", issue.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { AdaptationBlockerKind.MissingModel }, result.BlockerKinds);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_NonRoutableModel_AddsMissingCapability()
    {
        var catalog = CreateCatalog(CreateModel("model-locked", isRoutable: false));

        var result = _validator.Validate(
            new[] { CreateMapping(WorkflowRole.Escalation, "model-locked") },
            catalog);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.MissingCapability, issue.Kind);
        Assert.Equal("Escalation", issue.Role);
        Assert.Contains("not routable", issue.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { AdaptationBlockerKind.MissingCapability }, result.BlockerKinds);
    }

    [Fact]
    public void Validate_RoutableModelWithoutRequiredChatCapability_AddsMissingCapability()
    {
        var catalog = CreateCatalog(CreateModel(
            "model-no-chat",
            isRoutable: true,
            capabilities: ModelCapabilityFlags.ToolCalling));

        var result = _validator.Validate(
            new[] { CreateMapping(WorkflowRole.Executor, "model-no-chat") },
            catalog);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.MissingCapability, issue.Kind);
        Assert.Equal("Executor", issue.Role);
        Assert.Contains("does not provide the capability", issue.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { AdaptationBlockerKind.MissingCapability }, result.BlockerKinds);
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(WorkflowRole.Coordinator)]
    [InlineData(WorkflowRole.Executor)]
    [InlineData(WorkflowRole.Reviewer)]
    [InlineData(WorkflowRole.Escalation)]
    public void Validate_ChatRouteIsEligibleForEveryRole(WorkflowRole role)
    {
        var catalog = CreateCatalog(CreateModel("model-chat", isRoutable: true));

        var result = _validator.Validate(
            new[] { CreateMapping(role, "model-chat") },
            catalog);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RoutableModelIsNotBlockedForExtraCapabilities()
    {
        var catalog = CreateCatalog(CreateModel(
            "model-rich",
            isRoutable: true,
            capabilities: ModelCapabilityFlags.Chat
                | ModelCapabilityFlags.Vision
                | ModelCapabilityFlags.ToolCalling
                | ModelCapabilityFlags.ReasoningVariants));

        var result = _validator.Validate(
            new[] { CreateMapping(WorkflowRole.Reviewer, "model-rich") },
            catalog);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MultipleMappings_ReportsEveryIssue()
    {
        var catalog = CreateCatalog(
            CreateModel("model-1", isRoutable: true),
            CreateModel("model-locked", isRoutable: false),
            CreateModel("model-no-chat", isRoutable: true, capabilities: ModelCapabilityFlags.None));

        var result = _validator.Validate(
            new[]
            {
                CreateMapping(WorkflowRole.Executor, "model-1"),
                CreateMapping(WorkflowRole.Reviewer, "model-missing"),
                CreateMapping(WorkflowRole.Escalation, "model-locked"),
                CreateMapping(WorkflowRole.Coordinator, "model-no-chat")
            },
            catalog);

        Assert.Equal(3, result.Issues.Count);
        Assert.Contains(result.Issues, issue => issue.Kind == AdaptationBlockerKind.MissingModel);
        Assert.Equal(
            2,
            result.Issues.Count(issue => issue.Kind == AdaptationBlockerKind.MissingCapability));
    }

    [Fact]
    public void Validate_EmptyMappings_ReturnsEmptyResult()
    {
        var result = _validator.Validate(Array.Empty<SemanticRoleMapping>(), CreateCatalog());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => _validator.Validate(null!, CreateCatalog()));
        Assert.Throws<ArgumentNullException>(
            () => _validator.Validate(Array.Empty<SemanticRoleMapping>(), null!));
    }

    private static SemanticRoleMapping CreateMapping(WorkflowRole role, string targetModelId)
    {
        return new SemanticRoleMapping(
            role,
            "account-1:model-old",
            "account-1:" + targetModelId,
            targetModelId,
            "Reference validation test mapping.",
            isSemanticChange: false);
    }

    private static SanitizedCapabilityCatalog CreateCatalog(params SanitizedModelInfo[] models)
    {
        return new SanitizedCapabilityCatalog(
            Array.Empty<SanitizedProviderInfo>(),
            models,
            new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
    }

    private static SanitizedModelInfo CreateModel(
        string modelId,
        bool isRoutable,
        HealthState health = HealthState.Healthy,
        ModelCapabilityFlags capabilities = ModelCapabilityFlags.Chat)
    {
        return new SanitizedModelInfo(
            modelId,
            modelId,
            capabilities,
            Array.Empty<string>(),
            Array.Empty<string>(),
            ContextWindow: null,
            health,
            isRoutable);
    }
}
