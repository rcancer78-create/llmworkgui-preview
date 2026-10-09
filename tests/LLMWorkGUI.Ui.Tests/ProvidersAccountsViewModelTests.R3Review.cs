using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public partial class ProvidersAccountsViewModelTests
{
    [Fact]
    public async Task EditingSelectedProviderIdCannotCreateADuplicateOrReuseItsSecret()
    {
        var repository=new FakeProviderRepository();
        await repository.UpsertAsync(new ProviderProfile("owned-original","Owned",BackendType.CursorAcp,
            "https://owned.invalid/","owned-agent.exe",DataClassification.PrivateSource,true),"urn:llmworkgui:secret:owned-original");
        var model=CreateViewModel(provRepo:repository);
        await model.LoadProvidersAsync();
        model.SelectedProvider=Assert.Single(model.Providers);
        Assert.True(model.IsProviderIdReadOnly);
        model.EditingProviderId="owned-replacement";
        await model.ExecuteConfirmAndSaveAsync();
        var retained=Assert.Single(await repository.ListAsync());
        Assert.Equal("owned-original",retained.Id);
        Assert.Equal(BackendType.CursorAcp,retained.Backend);
        Assert.Equal("owned-agent.exe",retained.ExecutablePath);
        Assert.Equal("urn:llmworkgui:secret:owned-original",await repository.GetApiKeySecretReferenceAsync(retained.Id));
        Assert.Null(await repository.GetByIdAsync("owned-replacement"));
    }

    [Fact]
    public async Task DeleteCommandRequiresASelectionAndNewProviderRestoresCreationMode()
    {
        var repository=new FakeProviderRepository();
        await repository.UpsertAsync(new ProviderProfile("owned-original","Owned",BackendType.OpenCode,
            "https://owned.invalid/",null,DataClassification.PrivateSource,true));
        var model=CreateViewModel(provRepo:repository);
        Assert.False(model.DeleteProviderCommand.CanExecute(null));
        await model.LoadProvidersAsync();
        model.SelectedProvider=Assert.Single(model.Providers);
        Assert.True(model.DeleteProviderCommand.CanExecute(null));
        model.NewProviderCommand.Execute(null);
        Assert.False(model.IsProviderIdReadOnly);
        Assert.Null(model.SelectedProvider);
        Assert.False(model.DeleteProviderCommand.CanExecute(null));
        model.EditingProviderId="owned-new";
        model.EditingDisplayName="New owned profile";
        model.EditingBaseUrl="https://owned.invalid/";
        await model.ExecuteConfirmAndSaveAsync();
        Assert.NotNull(await repository.GetByIdAsync("owned-new"));
        Assert.NotNull(await repository.GetByIdAsync("owned-original"));
    }
}
