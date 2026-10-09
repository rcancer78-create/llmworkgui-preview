using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

public sealed class HealthScopeIdentityTests
{
    [Theory]
    [InlineData("a:b", "c:d")]
    [InlineData("акк:🧪", "модель/%:α")]
    [InlineData(" a ", " model ")]
    public void ModelIdentityRoundTripsExactBytes(string account, string model)
    {
        var scope = HealthScope.ForModelRoute(account, model);
        Assert.True(scope.TryGetModelRoute(out var parsedAccount, out var parsedModel));
        Assert.Equal(account, parsedAccount);
        Assert.Equal(model, parsedModel);
    }

    [Theory]
    [InlineData("a:b:c")]
    [InlineData("v1:61:FF")]
    [InlineData("v1:6a:62")]
    [InlineData("v1::62")]
    [InlineData("v1:61:62:63")]
    public void MalformedOrNonCanonicalEncodingIsRefused(string id)
        => Assert.False(new HealthScope(HealthScope.ModelRouteScopeType, id).TryGetModelRoute(out _, out _));

    [Fact]
    public void ColonInAccountCannotAliasColonInModelOrOpaqueRoute()
    {
        var first = HealthScope.ForModelRoute("a:b", "c");
        var second = HealthScope.ForModelRoute("a", "b:c");
        Assert.NotEqual(first, second);
        Assert.NotEqual(first, HealthScope.ForRoute(first.ScopeId));
    }

    [Fact]
    public async Task AuthenticationCascadeDoesNotBlockAccountWhoseNameSharesPrefix()
    {
        var service = new HealthCenterService(new InMemoryHealthStateRepository(), new InMemoryHealthEventRepository(),
            new HealthTestTimeProvider(), new HealthPolicy { FailureThreshold = 1 });
        var own = HealthScope.ForModelRoute("account", "model");
        var neighbour = HealthScope.ForModelRoute("account:other", "model");
        await service.ReportSuccessAsync(own);
        await service.ReportSuccessAsync(neighbour);
        await service.ReportFailureAsync(HealthScope.ForAccount("account"), HealthErrorClass.AuthenticationOrRefresh);
        Assert.False((await service.GetSnapshotAsync(own)).IsRoutable);
        Assert.True((await service.GetSnapshotAsync(neighbour)).IsRoutable);
    }
}
