using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class DomainReviewContractTests
{
    [Theory]
    [InlineData("limit", double.NaN)]
    [InlineData("limit", double.PositiveInfinity)]
    [InlineData("used", double.NaN)]
    [InlineData("used", double.PositiveInfinity)]
    [InlineData("remaining", double.NaN)]
    [InlineData("remaining", double.PositiveInfinity)]
    [InlineData("reserve", double.NaN)]
    [InlineData("reserve", double.PositiveInfinity)]
    public void QuotaBucket_RejectsNonFiniteValuesBeforeTheyCanInfluenceRouting(string field, double invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuotaBucket("requests", QuotaLimitUnit.Requests,
            QuotaLimitWindow.PerDay, limitValue: field == "limit" ? invalid : 100,
            usedValue: field == "used" ? invalid : 25,
            remainingValue: field == "remaining" ? invalid : 75,
            hardReserve: field == "reserve" ? invalid : 10));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AccountReserve_RejectsNonFiniteValuesBeforeReservePolicyComparisons(double invalid)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Account("account", "provider", "Account",
            null, AuthState.Valid, 0, true, HealthState.Healthy, null, null, 1, invalid));
        Assert.Equal("reserveThreshold", error.ParamName);
    }

    [Fact]
    public void QuotaValues_PreserveUnknownAndFiniteZeroSemantics()
    {
        var unknown = new QuotaBucket("unknown", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay);
        Assert.Null(unknown.RemainingFraction);
        Assert.False(unknown.HasHardReserveViolation);
        var zero = new QuotaBucket("zero", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay,
            limitValue: 100, usedValue: 100, remainingValue: 0, hardReserve: 0);
        Assert.Equal(0, zero.RemainingFraction);
        Assert.Equal(1, zero.UsedFraction);
        Assert.True(zero.HasHardReserveViolation);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void WorkflowNodeKind_RejectsUndefinedValuesAtDefinitionBoundary(int kind)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowNodeDefinition(
            "node", (WorkflowNodeKind)kind, "Node", "Role"));
        Assert.Equal("kind", error.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void RequiredDocumentKinds_RejectUndefinedValuesBeforeTemplatePersistence(int kind)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Template(
            new[] { DocumentTemplateKind.ProblemStatement, (DocumentTemplateKind)kind }));
    }

    [Fact]
    public void RequiredDocumentKinds_PreserveDeclaredDeduplicationAndOrder()
    {
        var template = Template(new[] { DocumentTemplateKind.ProblemStatement, DocumentTemplateKind.Architecture,
            DocumentTemplateKind.ProblemStatement });
        Assert.Equal(new[] { DocumentTemplateKind.ProblemStatement, DocumentTemplateKind.Architecture },
            template.RequiredDocumentTemplates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublicExecutionStateLists_CannotChangeNormativeClassification(bool terminal)
    {
        var list = terminal ? ExecutionStateMachine.TerminalStates : ExecutionStateMachine.NonTerminalStates;
        if (list is IList<ExecutionState> mutable)
        {
            var original = mutable[0];
            try
            {
                Assert.IsAssignableFrom<NotSupportedException>(Record.Exception(() => mutable[0] = (ExecutionState)4242));
            }
            finally { if (mutable[0] != original) mutable[0] = original; }
        }
        Assert.DoesNotContain((ExecutionState)4242, list);
    }

    [Fact]
    public void PublicExecutionTransitions_CannotAuthorizeAnUndefinedTerminalState()
    {
        AssertImmutableSet(ExecutionStateMachine.AllowedTransitions, (ExecutionState.Queued, (ExecutionState)4242));
        Assert.False(new ExecutionStateMachine().CanTransitionTo((ExecutionState)4242));
    }

    [Fact]
    public void PublicSessionTransitions_CannotAuthorizeAnUndefinedSessionState()
    {
        AssertImmutableSet(SessionStateMachine.AllowedTransitions, (SessionState.Draft, (SessionState)4242));
        Assert.False(new SessionStateMachine().CanTransitionTo((SessionState)4242));
    }

    [Fact]
    public void PublicHealthTransitions_CannotAuthorizeAnUndefinedBreakerState()
    {
        AssertImmutableSet(HealthStateMachine.AllowedTransitions, (HealthState.Healthy, (HealthState)4242));
        Assert.False(new HealthStateMachine().CanTransitionTo((HealthState)4242));
    }

    private static void AssertImmutableSet<T>(IReadOnlySet<T> values, T unsupported)
    {
        if (values is ISet<T> mutable)
        {
            try { Assert.IsAssignableFrom<NotSupportedException>(Record.Exception(() => mutable.Add(unsupported))); }
            finally { if (mutable.Contains(unsupported)) mutable.Remove(unsupported); }
        }
        Assert.DoesNotContain(unsupported, values);
    }

    private static WorkflowTemplateDefinition Template(IReadOnlyList<DocumentTemplateKind> kinds) => new(
        "template", 1, "Template", "Declared document policy", new WorkflowGraph("terminal", new[]
            { new WorkflowNodeDefinition("terminal", WorkflowNodeKind.TerminalOutcome, "Terminal", "Role") }),
        new[] { new RoleBindingDefinition("Role", "route") }, kinds, false, DateTimeOffset.UnixEpoch);
}
