using System;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class HealthScopeIdentityDisplayTests
{
    [Fact]
    public void PendingAuthenticationNeverDisplaysPersistedHealthyAsAvailable()
    {
        var viewModel = new HealthScopeViewModel(new HealthSnapshot
        { Scope = HealthScope.ForAccount("account"), State = HealthState.Healthy, AuthenticationFanoutPending = true });
        Assert.False(viewModel.IsRoutable);
        Assert.Contains("Заблокировано", viewModel.StateDisplay);
        Assert.Contains("Обновить", viewModel.StateDisplay);
    }

    [Fact]
    public void EncodedModelScopeDisplaysExactAccountAndModelButRetainsCanonicalSelectionKey()
    {
        var scope = HealthScope.ForModelRoute("аккаунт:a", "model:b");
        var viewModel = new HealthScopeViewModel(new HealthSnapshot
        { Scope = scope, State = HealthState.ProbeRequired, UpdatedAt = DateTimeOffset.UtcNow });
        Assert.Equal("аккаунт:a / model:b", viewModel.ScopeIdDisplay);
        Assert.Equal("Модель аккаунта", viewModel.ScopeTypeDisplay);
        Assert.Equal("model-route:" + scope.ScopeId, viewModel.Key);
        Assert.False(viewModel.IsRoutable);
    }
}
