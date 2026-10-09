using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

/// <summary>
/// Evidence for the adaptation route identity: the four identity parts stay separate, the composite
/// route id round-trips, and a reserved literal is never mistaken for a route id.
/// </summary>
public sealed class AdaptationRouteIdentityTests
{
    [Theory]
    [InlineData("C:/private/model")]
    [InlineData("/etc/models")]
    [InlineData("urn:llmworkgui:secret:model")]
    [InlineData("model with spaces")]
    public void RouteIdentity_RejectsValuesForbiddenForBackendModels(string invalidModel)
    {
        Assert.Throws<ArgumentException>(() => new AdaptationRouteIdentity(
            "acct", "profile", BackendType.OpenCode, invalidModel));
        var encoded = Uri.EscapeDataString(invalidModel);
        Assert.False(AdaptationRouteIdentity.TryParse($"route:acct|profile|OpenCode|{encoded}", out _));
    }

    [Fact]
    public void RouteId_RoundTripsUnicodeWithoutChangingIdentity()
    {
        var identity = new AdaptationRouteIdentity("аккаунт-😀", "профиль-é", BackendType.OpenCode, "模型/модель");

        Assert.True(AdaptationRouteIdentity.TryParse(identity.RouteId, out var parsed));
        Assert.Equal(identity, parsed);
    }

    [Theory]
    [InlineData("route:acct|profile|99|model")]
    [InlineData("route:|profile|OpenCode|model")]
    [InlineData("route:acct||OpenCode|model")]
    [InlineData("route:%20|profile|OpenCode|model")]
    [InlineData("route:acct|%20|OpenCode|model")]
    [InlineData("route:%FF|profile|OpenCode|model")]
    [InlineData("route:%C3%28|profile|OpenCode|model")]
    public void TryParse_MalformedIdentity_ReturnsFalseWithoutThrowing(string routeId)
    {
        Assert.False(AdaptationRouteIdentity.TryParse(routeId, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void RouteId_RoundTripsEveryIdentityPart()
    {
        var identity = new AdaptationRouteIdentity(
            "acct:with:colons",
            "prov|with|pipes",
            BackendType.OpenCode,
            "opencode/space-bunny-free");

        Assert.True(AdaptationRouteIdentity.TryParse(identity.RouteId, out var parsed));

        Assert.Equal(identity.AccountId, parsed.AccountId);
        Assert.Equal(identity.ProviderProfileId, parsed.ProviderProfileId);
        Assert.Equal(identity.Backend, parsed.Backend);
        Assert.Equal(identity.BackendModelId, parsed.BackendModelId);
    }

    [Fact]
    public void RouteId_IsNeverConfusableWithABareAccountId()
    {
        var identity = new AdaptationRouteIdentity(
            "cursor",
            "prov-1",
            BackendType.OpenCode,
            "opencode/space-bunny-free");

        Assert.NotEqual("cursor", identity.RouteId);
        Assert.False(AdaptationRouteIdentity.TryParse("cursor", out _));
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("MIRASIM")]
    [InlineData("default")]
    [InlineData("opencode")]
    [InlineData("acct-1:model-2")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsAnythingThatIsNotACompositeRouteId(string? routeId)
    {
        Assert.False(AdaptationRouteIdentity.TryParse(routeId, out var identity));
        Assert.Null(identity);
    }

    [Fact]
    public void TryParse_MissingBackendModelId_IsRepresentedAsAbsent()
    {
        var identity = new AdaptationRouteIdentity("acct-1", "prov-1", BackendType.OpenCode);

        Assert.False(identity.HasBackendModelId);
        Assert.True(AdaptationRouteIdentity.TryParse(identity.RouteId, out var parsed));
        Assert.Null(parsed.BackendModelId);
        Assert.False(parsed.HasBackendModelId);
    }

    [Fact]
    public void TryParse_RejectsATamperedRouteId()
    {
        var identity = new AdaptationRouteIdentity("acct-1", "prov-1", BackendType.OpenCode, "model-1");

        // A component separator smuggled into a component cannot be re-parsed into a different identity.
        Assert.False(AdaptationRouteIdentity.TryParse("route:acct-1|evil|prov-1|OpenCode|model-1", out _));
    }

    [Theory]
    [InlineData(BackendType.OpenCode, true)]
    [InlineData(BackendType.CursorAcp, false)]
    [InlineData(BackendType.Mirasim, false)]
    [InlineData(BackendType.StarCliProxy, false)]
    [InlineData(BackendType.Agy, false)]
    public void IsAdaptationCapable_IsAClosedSetOverBackends(BackendType backend, bool expected)
    {
        var identity = new AdaptationRouteIdentity("acct-1", "prov-1", backend, "model-1");

        Assert.Equal(expected, identity.IsAdaptationCapable);
        Assert.Equal(expected, identity.IsSelectableForAdaptation);
    }

    [Fact]
    public void TryCreate_RefusesACatalogRowWithoutACompleteIdentity()
    {
        var incomplete = new SanitizedModelInfo(
            "model-1",
            "Primary",
            ModelCapabilityFlags.Chat,
            Array.Empty<string>(),
            Array.Empty<string>(),
            ContextWindow: null,
            HealthState.Healthy,
            IsRoutable: true);

        Assert.False(AdaptationRouteIdentity.TryCreate(incomplete, out var identity));
        Assert.Null(identity);

        var complete = incomplete with
        {
            AccountId = "acct-1",
            ProviderProfileId = "prov-1",
            Backend = BackendType.OpenCode,
            BackendModelId = "opencode/space-bunny-free"
        };

        Assert.True(AdaptationRouteIdentity.TryCreate(complete, out identity));
        Assert.Equal("acct-1", identity.AccountId);
        Assert.Equal("prov-1", identity.ProviderProfileId);
        Assert.Equal(BackendType.OpenCode, identity.Backend);
        Assert.Equal("opencode/space-bunny-free", identity.BackendModelId);
    }

    [Fact]
    public void EnumerateCandidateAccountIds_PrefersTheRouteAccountThenTheLegacyPrefix()
    {
        var routeId = new AdaptationRouteIdentity("acct-1", "prov-1", BackendType.OpenCode, "model-1").RouteId;

        Assert.Equal(new[] { "acct-1" }, AdaptationRouteIdentity.EnumerateCandidateAccountIds(routeId));
        Assert.Equal(new[] { "acct-1" }, AdaptationRouteIdentity.EnumerateCandidateAccountIds("acct-1"));
        Assert.Equal(new[] { "acct-1:model-2", "acct-1" }, AdaptationRouteIdentity.EnumerateCandidateAccountIds("acct-1:model-2"));
        Assert.Equal(new[] { "a:b:" }, AdaptationRouteIdentity.EnumerateCandidateAccountIds("a:b:"));
        Assert.Equal(new[] { "a:b", "a" }, AdaptationRouteIdentity.EnumerateCandidateAccountIds("a:b"));
    }

    [Fact]
    public void EnumerateModelRoutes_YieldsOneDistinctRoutePerConfiguredModel()
    {
        var identity = new AdaptationRouteIdentity("acct-1", "prov-1", BackendType.OpenCode);

        var routes = identity.EnumerateModelRoutes(new[]
        {
            "opencode/space-bunny-free",
            "opencode/space-bunny-pro",
            "opencode/space-bunny-free",
            "   ",
            @"C:\Users\tester\.codex\home"
        });

        // One route per real model, in configuration order, each with its own unambiguous route id.
        Assert.Equal(2, routes.Count);
        Assert.Equal(
            new[] { "opencode/space-bunny-free", "opencode/space-bunny-pro" },
            routes.Select(route => route.BackendModelId).ToArray());
        Assert.Equal(2, routes.Select(route => route.RouteId).Distinct(StringComparer.Ordinal).Count());

        Assert.All(routes, route =>
        {
            Assert.Equal("acct-1", route.AccountId);
            Assert.Equal("prov-1", route.ProviderProfileId);
            Assert.Equal(BackendType.OpenCode, route.Backend);
            Assert.True(route.IsSelectableForAdaptation);
        });
    }

    [Theory]
    [InlineData(BackendType.CursorAcp)]
    [InlineData(BackendType.Mirasim)]
    [InlineData(BackendType.StarCliProxy)]
    [InlineData(BackendType.Agy)]
    public void EnumerateModelRoutes_OnANonAdaptationBackend_YieldsNothing(BackendType backend)
    {
        var identity = new AdaptationRouteIdentity("acct-1", "prov-1", backend);

        Assert.Empty(identity.EnumerateModelRoutes(new[] { "any/model" }));
    }

    [Fact]
    public void EnumerateModelRoutes_WithoutAnyConfiguredModel_YieldsNothing()
    {
        var identity = new AdaptationRouteIdentity("acct-1", "prov-1", BackendType.OpenCode);

        Assert.Empty(identity.EnumerateModelRoutes(Array.Empty<string>()));
        Assert.False(identity.IsSelectableForAdaptation);
    }
}

/// <summary>
/// Evidence for the single closed required-capability mask. Chat is the only required flag, and it is
/// the same mask for every mappable role: nothing else is requested by this seam, so nothing else is
/// asserted.
/// </summary>
public sealed class WorkflowRoleRequiredCapabilitiesTests
{
    [Theory]
    [InlineData(WorkflowRole.Coordinator)]
    [InlineData(WorkflowRole.Executor)]
    [InlineData(WorkflowRole.Reviewer)]
    [InlineData(WorkflowRole.Escalation)]
    public void RequiredFor_EveryKnownRoleIsTheSameChatOnlyMask(WorkflowRole role)
    {
        Assert.Equal(ModelCapabilityFlags.Chat, WorkflowRoleRequiredCapabilities.RequiredFor(role));
        Assert.Equal(
            WorkflowRoleRequiredCapabilities.RequiredMask,
            WorkflowRoleRequiredCapabilities.RequiredFor(role));
    }

    [Fact]
    public void RequiredFor_UnknownRoleIsRefusedRatherThanDefaulted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorkflowRoleRequiredCapabilities.RequiredFor(WorkflowRole.Unknown));
    }

    [Theory]
    [InlineData(WorkflowRole.Coordinator)]
    [InlineData(WorkflowRole.Executor)]
    [InlineData(WorkflowRole.Reviewer)]
    [InlineData(WorkflowRole.Escalation)]
    public void IsSatisfiedBy_ChatIsRequiredAndOtherFlagsAreNot(WorkflowRole role)
    {
        Assert.True(WorkflowRoleRequiredCapabilities.IsSatisfiedBy(role, ModelCapabilityFlags.Chat));
        Assert.False(WorkflowRoleRequiredCapabilities.IsSatisfiedBy(role, ModelCapabilityFlags.None));
        Assert.False(WorkflowRoleRequiredCapabilities.IsSatisfiedBy(
            role,
            ModelCapabilityFlags.ToolCalling | ModelCapabilityFlags.Vision | ModelCapabilityFlags.ReasoningVariants));
    }
}
