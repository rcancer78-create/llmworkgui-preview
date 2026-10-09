using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class AdaptationDomainTests
{
    [Fact]
    public void AdaptationGoal_DefinesCanonicalValues()
    {
        var values = Enum.GetValues<AdaptationGoal>();

        Assert.Equal(
            new[]
            {
                AdaptationGoal.CostSaving,
                AdaptationGoal.Quality,
                AdaptationGoal.Speed,
                AdaptationGoal.Balanced
            },
            values);

        Assert.Equal(1, (int)AdaptationGoal.CostSaving);
        Assert.Equal(2, (int)AdaptationGoal.Quality);
        Assert.Equal(3, (int)AdaptationGoal.Speed);
        Assert.Equal(4, (int)AdaptationGoal.Balanced);
    }

    [Theory]
    [InlineData(AdaptationGoal.CostSaving, "CostSaving")]
    [InlineData(AdaptationGoal.Quality, "Quality")]
    [InlineData(AdaptationGoal.Speed, "Speed")]
    [InlineData(AdaptationGoal.Balanced, "Balanced")]
    public void AdaptationGoal_ToWireName_ReturnsStableName(AdaptationGoal goal, string expected)
    {
        Assert.Equal(expected, goal.ToWireName());
    }

    [Theory]
    [InlineData(AdaptationGoal.CostSaving, "Экономия токенов")]
    [InlineData(AdaptationGoal.Quality, "Качество")]
    [InlineData(AdaptationGoal.Speed, "Скорость")]
    [InlineData(AdaptationGoal.Balanced, "Баланс")]
    public void AdaptationGoal_ToDisplayName_ReturnsLocalizedLabel(AdaptationGoal goal, string expected)
    {
        Assert.Equal(expected, goal.ToDisplayName());
    }

    [Fact]
    public void AdaptationGoal_Extensions_ThrowForUndefinedValue()
    {
        var undefined = (AdaptationGoal)42;

        Assert.Throws<ArgumentOutOfRangeException>(() => undefined.ToWireName());
        Assert.Throws<ArgumentOutOfRangeException>(() => undefined.ToDisplayName());
    }

    [Fact]
    public void AdaptationBlockerKind_DefinesCanonicalValues()
    {
        var values = Enum.GetValues<AdaptationBlockerKind>();

        Assert.Equal(
            new[]
            {
                AdaptationBlockerKind.MissingModel,
                AdaptationBlockerKind.MissingCapability,
                AdaptationBlockerKind.DisallowedSemanticChange,
                AdaptationBlockerKind.DetectedSecret,
                AdaptationBlockerKind.InvalidSchema,
                AdaptationBlockerKind.Other
            },
            values);
    }

    [Fact]
    public void SemanticRoleMapping_Constructor_SetsAllProperties()
    {
        var mapping = new SemanticRoleMapping(
            WorkflowRole.Reviewer,
            "account-1:model-old",
            "account-2:model-new",
            "model-new",
            "Reviewer requires stronger reasoning.",
            isSemanticChange: false,
            blockerKind: null);

        Assert.Equal(WorkflowRole.Reviewer, mapping.Role);
        Assert.Equal("account-1:model-old", mapping.OriginalRoute);
        Assert.Equal("account-2:model-new", mapping.TargetRoute);
        Assert.Equal("model-new", mapping.TargetModelId);
        Assert.Equal("Reviewer requires stronger reasoning.", mapping.Rationale);
        Assert.False(mapping.IsSemanticChange);
        Assert.Null(mapping.BlockerKind);
    }

    [Fact]
    public void SemanticRoleMapping_Constructor_CarriesSemanticChangeBlocker()
    {
        var mapping = new SemanticRoleMapping(
            WorkflowRole.Escalation,
            "route-old",
            "route-new",
            "model-new",
            "Escalation semantics would change.",
            isSemanticChange: true,
            blockerKind: AdaptationBlockerKind.DisallowedSemanticChange);

        Assert.True(mapping.IsSemanticChange);
        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, mapping.BlockerKind);
    }

    [Theory]
    [InlineData(WorkflowRole.Unknown)]
    [InlineData((WorkflowRole)99)]
    public void SemanticRoleMapping_Constructor_RejectsUnknownRole(WorkflowRole role)
    {
        Assert.Throws<ArgumentException>(() => new SemanticRoleMapping(
            role,
            "route-old",
            "route-new",
            "model-new",
            "rationale",
            isSemanticChange: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SemanticRoleMapping_Constructor_RejectsBlankOriginalRoute(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => new SemanticRoleMapping(
            WorkflowRole.Executor,
            invalidValue,
            "route-new",
            "model-new",
            "rationale",
            isSemanticChange: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SemanticRoleMapping_Constructor_RejectsBlankTargetRoute(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => new SemanticRoleMapping(
            WorkflowRole.Executor,
            "route-old",
            invalidValue,
            "model-new",
            "rationale",
            isSemanticChange: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SemanticRoleMapping_Constructor_RejectsBlankTargetModelId(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => new SemanticRoleMapping(
            WorkflowRole.Executor,
            "route-old",
            "route-new",
            invalidValue,
            "rationale",
            isSemanticChange: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SemanticRoleMapping_Constructor_RejectsBlankRationale(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => new SemanticRoleMapping(
            WorkflowRole.Executor,
            "route-old",
            "route-new",
            "model-new",
            invalidValue,
            isSemanticChange: false));
    }

    [Fact]
    public void SemanticRoleMapping_Constructor_AcceptsCoordinatorRole()
    {
        var mapping = new SemanticRoleMapping(
            WorkflowRole.Coordinator,
            "account-1:model-old",
            "account-2:model-new",
            "model-new",
            "Coordinator is a supported adaptation role.",
            isSemanticChange: false);

        Assert.Equal(WorkflowRole.Coordinator, mapping.Role);
    }

    [Fact]
    public void SemanticRoleMapping_Constructor_RejectsUndefinedBlockerKind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticRoleMapping(
            WorkflowRole.Executor,
            "route-old",
            "route-new",
            "model-new",
            "rationale",
            isSemanticChange: true,
            blockerKind: (AdaptationBlockerKind)99));
    }

    [Fact]
    public void SemanticRoleMapping_Equality_UsesAllProperties()
    {
        var first = new SemanticRoleMapping(
            WorkflowRole.Executor,
            "route-old",
            "route-new",
            "model-new",
            "rationale",
            isSemanticChange: false);

        var second = new SemanticRoleMapping(
            WorkflowRole.Executor,
            "route-old",
            "route-new",
            "model-new",
            "rationale",
            isSemanticChange: false);

        var different = new SemanticRoleMapping(
            WorkflowRole.Executor,
            "route-old",
            "route-new",
            "model-other",
            "rationale",
            isSemanticChange: false);

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }
}
