using LLMWorkGUI.Domain.Entities;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class WorkflowBindingTests
{
    private static readonly string BindingId = "8f14e45fceea167a5a36dedd4bea2543";
    private static readonly DateTimeOffset CreatedAt = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var updatedAt = CreatedAt.AddMinutes(15);

        var binding = new WorkflowBinding(
            BindingId,
            "project-1",
            "pkg-1",
            "ver-2",
            "route-policy-1",
            CreatedAt,
            updatedAt);

        Assert.Equal(BindingId, binding.Id);
        Assert.Equal("project-1", binding.ProjectId);
        Assert.Equal("pkg-1", binding.WorkflowPackageId);
        Assert.Equal("ver-2", binding.ActiveVersionId);
        Assert.Equal("route-policy-1", binding.RoutePolicyId);
        Assert.Equal(CreatedAt, binding.CreatedAtUtc);
        Assert.Equal(updatedAt, binding.UpdatedAtUtc);
    }

    [Fact]
    public void Constructor_AllowsNullRoutePolicy()
    {
        var binding = CreateBinding(routePolicyId: null);

        Assert.Null(binding.RoutePolicyId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankId(string invalidId)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowBinding(
            invalidId,
            "project-1",
            "pkg-1",
            "ver-1",
            null,
            CreatedAt,
            CreatedAt));
    }

    [Theory]
    [InlineData("binding-1")]
    [InlineData("not-a-guid")]
    [InlineData("8f14e45fceea167a5a36dedd4bea254")]
    public void Constructor_RejectsIdThatIsNotAGuid(string invalidId)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowBinding(
            invalidId,
            "project-1",
            "pkg-1",
            "ver-1",
            null,
            CreatedAt,
            CreatedAt));
    }

    [Fact]
    public void Constructor_AcceptsGuidInDashedFormat()
    {
        var dashedId = Guid.Parse(BindingId).ToString("D");

        var binding = new WorkflowBinding(
            dashedId,
            "project-1",
            "pkg-1",
            "ver-1",
            null,
            CreatedAt,
            CreatedAt);

        Assert.Equal(dashedId, binding.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankProjectId(string invalidProjectId)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowBinding(
            BindingId,
            invalidProjectId,
            "pkg-1",
            "ver-1",
            null,
            CreatedAt,
            CreatedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankWorkflowPackageId(string invalidPackageId)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowBinding(
            BindingId,
            "project-1",
            invalidPackageId,
            "ver-1",
            null,
            CreatedAt,
            CreatedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankActiveVersionId(string invalidVersionId)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowBinding(
            BindingId,
            "project-1",
            "pkg-1",
            invalidVersionId,
            null,
            CreatedAt,
            CreatedAt));
    }

    [Fact]
    public void Constructor_RejectsBlankRoutePolicyId()
    {
        Assert.Throws<ArgumentException>(() => CreateBinding(routePolicyId: "   "));
    }

    private static WorkflowBinding CreateBinding(string? routePolicyId) =>
        new(
            BindingId,
            "project-1",
            "pkg-1",
            "ver-1",
            routePolicyId,
            CreatedAt,
            CreatedAt);
}
