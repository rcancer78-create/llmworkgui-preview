using System.Reflection;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class SessionBindingTests
{
    [Fact]
    public void Constructor_StoresAllComponents()
    {
        var binding = new SessionBinding(
            BackendType.CursorAcp,
            "profile-7",
            "account-3",
            "model-9",
            "medium",
            "slow",
            "plan");

        Assert.Equal(BackendType.CursorAcp, binding.Backend);
        Assert.Equal("profile-7", binding.ProviderProfileId);
        Assert.Equal("account-3", binding.AccountId);
        Assert.Equal("model-9", binding.ModelId);
        Assert.Equal("medium", binding.ReasoningEffort);
        Assert.Equal("slow", binding.SpeedMode);
        Assert.Equal("plan", binding.ExecutionMode);
    }

    [Fact]
    public void Constructor_AllowsNullOptionalComponents()
    {
        var binding = new SessionBinding(BackendType.OpenCode, "profile-1", "account-1", "model-1", null, null, null);

        Assert.Null(binding.ReasoningEffort);
        Assert.Null(binding.SpeedMode);
        Assert.Null(binding.ExecutionMode);
    }

    [Fact]
    public void Equality_IsStrictAcrossAllComponents()
    {
        var original = CreateBinding();
        var identical = CreateBinding();

        Assert.True(original.Equals(identical));
        Assert.True(original == identical);
        Assert.False(original != identical);
        Assert.Equal(original.GetHashCode(), identical.GetHashCode());
    }

    [Fact]
    public void Equality_DistinguishesBackend()
    {
        var original = CreateBinding();

        Assert.NotEqual(
            original,
            new SessionBinding(
                BackendType.CursorAcp,
                original.ProviderProfileId,
                original.AccountId,
                original.ModelId,
                original.ReasoningEffort,
                original.SpeedMode,
                original.ExecutionMode));
    }

    [Fact]
    public void Equality_DistinguishesProviderProfileId()
    {
        var original = CreateBinding();

        Assert.NotEqual(
            original,
            new SessionBinding(
                original.Backend,
                "profile-2",
                original.AccountId,
                original.ModelId,
                original.ReasoningEffort,
                original.SpeedMode,
                original.ExecutionMode));
    }

    [Fact]
    public void Equality_DistinguishesAccountId()
    {
        var original = CreateBinding();

        Assert.NotEqual(
            original,
            new SessionBinding(
                original.Backend,
                original.ProviderProfileId,
                "account-2",
                original.ModelId,
                original.ReasoningEffort,
                original.SpeedMode,
                original.ExecutionMode));
    }

    [Fact]
    public void Equality_DistinguishesModelId()
    {
        var original = CreateBinding();

        Assert.NotEqual(
            original,
            new SessionBinding(
                original.Backend,
                original.ProviderProfileId,
                original.AccountId,
                "model-2",
                original.ReasoningEffort,
                original.SpeedMode,
                original.ExecutionMode));
    }

    [Fact]
    public void Equality_DistinguishesReasoningEffort()
    {
        var original = CreateBinding();

        Assert.NotEqual(
            original,
            new SessionBinding(
                original.Backend,
                original.ProviderProfileId,
                original.AccountId,
                original.ModelId,
                null,
                original.SpeedMode,
                original.ExecutionMode));
    }

    [Fact]
    public void Equality_DistinguishesSpeedMode()
    {
        var original = CreateBinding();

        Assert.NotEqual(
            original,
            new SessionBinding(
                original.Backend,
                original.ProviderProfileId,
                original.AccountId,
                original.ModelId,
                original.ReasoningEffort,
                null,
                original.ExecutionMode));
    }

    [Fact]
    public void Equality_DistinguishesExecutionMode()
    {
        var original = CreateBinding();

        Assert.NotEqual(
            original,
            new SessionBinding(
                original.Backend,
                original.ProviderProfileId,
                original.AccountId,
                original.ModelId,
                original.ReasoningEffort,
                original.SpeedMode,
                null));
    }

    [Fact]
    public void Equality_TreatsNullAndValueOptionalComponentsAsDifferent()
    {
        var withNull = new SessionBinding(BackendType.OpenCode, "profile-1", "account-1", "model-1", null, "fast", null);
        var withValues = new SessionBinding(BackendType.OpenCode, "profile-1", "account-1", "model-1", "high", "fast", "agent");

        Assert.NotEqual(withNull, withValues);
        Assert.False(withNull == withValues);
    }

    [Fact]
    public void Dictionary_LookupUsesStructuralEquality()
    {
        var map = new Dictionary<SessionBinding, string> { [CreateBinding()] = "bound" };

        Assert.Equal("bound", map[CreateBinding()]);
    }

    [Fact]
    public void Properties_HaveNoPublicSetters()
    {
        foreach (var property in typeof(SessionBinding).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.Null(property.SetMethod);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Constructor_RejectsBlankProviderProfileId(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => new SessionBinding(BackendType.OpenCode, value!, "account-1", "model-1", null, null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Constructor_RejectsBlankAccountId(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => new SessionBinding(BackendType.OpenCode, "profile-1", value!, "model-1", null, null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Constructor_RejectsBlankModelId(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => new SessionBinding(BackendType.OpenCode, "profile-1", "account-1", value!, null, null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_RejectsBlankReasoningEffort(string value)
    {
        Assert.Throws<ArgumentException>(
            () => new SessionBinding(BackendType.OpenCode, "profile-1", "account-1", "model-1", value, null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_RejectsBlankSpeedMode(string value)
    {
        Assert.Throws<ArgumentException>(
            () => new SessionBinding(BackendType.OpenCode, "profile-1", "account-1", "model-1", null, value, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_RejectsBlankExecutionMode(string value)
    {
        Assert.Throws<ArgumentException>(
            () => new SessionBinding(BackendType.OpenCode, "profile-1", "account-1", "model-1", null, null, value));
    }

    private static SessionBinding CreateBinding() =>
        new(BackendType.OpenCode, "profile-1", "account-1", "model-1", "high", "fast", "agent");
}
