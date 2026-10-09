using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ProviderSecretEditorLifetimeReviewTests
{
    [Fact]
    public void ProviderKey_RealUnloadClearsUiAndOwner_AndReloadDoesNotRestoreInput()
    {
        OnSta(() =>
        {
            var vm = Provider();
            var editor = new ProviderApiKeyBox { DataContext = vm };
            using var host = new EditorHost(editor);
            var password = Assert.IsType<PasswordBox>(editor.Content);
            password.Password = "synthetic-provider-input";
            Assert.Equal(password.Password, vm.EditingApiKey);
            host.Unload(editor);
            Assert.Empty(password.Password);
            Assert.Empty(vm.EditingApiKey);
            host.Load(editor);
            Assert.Empty(password.Password);
            Assert.Empty(vm.EditingApiKey);
        });
    }

    [Fact]
    public void ProviderKey_ContextSwapClearsOldOwner_AndPreservesNewOwnerInput()
    {
        OnSta(() =>
        {
            var old = Provider();
            var editor = new ProviderApiKeyBox { DataContext = old };
            using var host = new EditorHost(editor);
            var password = Assert.IsType<PasswordBox>(editor.Content);
            password.Password = "synthetic-old-input";
            var next = Provider();
            next.EditingApiKey = "synthetic-new-input";
            editor.DataContext = next;
            Pump();
            Assert.Empty(old.EditingApiKey);
            Assert.Equal("synthetic-new-input", next.EditingApiKey);
            Assert.Equal(next.EditingApiKey, password.Password);
        });
    }

    [Theory]
    [InlineData("sensitive-name")]
    [InlineData("explicit-secret")]
    [InlineData("saved-reference")]
    [InlineData("public")]
    public void Header_RealUnloadClearsSensitiveInput_AndPreservesPublicValue(string mode)
    {
        OnSta(() =>
        {
            var model = Header(mode);
            var sensitive = model.IsSensitive;
            var editor = new ProviderHeaderValueBox { DataContext = model };
            using var host = new EditorHost(editor);
            Enter(editor, "synthetic-header-input");
            var oldUi = editor.Content;
            Assert.Equal("synthetic-header-input", model.Value);
            host.Unload(editor);
            Assert.Equal(sensitive ? "" : "synthetic-header-input", model.Value);
            Assert.Equal(sensitive ? "" : "synthetic-header-input", Value(oldUi));
            host.Load(editor);
            Assert.Equal(sensitive ? "" : "synthetic-header-input", Value(editor.Content));
        });
    }

    [Theory]
    [InlineData("sensitive-name")]
    [InlineData("explicit-secret")]
    [InlineData("saved-reference")]
    [InlineData("public")]
    public void Header_ContextSwapClearsOldSensitiveOwner_AndPreservesNewOwnerInput(string mode)
    {
        OnSta(() =>
        {
            var old = Header(mode);
            var sensitive = old.IsSensitive;
            var editor = new ProviderHeaderValueBox { DataContext = old };
            using var host = new EditorHost(editor);
            Enter(editor, "synthetic-old-header-input");
            var oldUi = editor.Content;
            var next = new CustomHeaderItemViewModel("X-Region", "public-new-region");
            editor.DataContext = next;
            Pump();
            Assert.Equal(sensitive ? "" : "synthetic-old-header-input", old.Value);
            if (sensitive) Assert.Empty(Assert.IsType<PasswordBox>(oldUi).Password);
            Assert.Equal("public-new-region", next.Value);
            Assert.Equal(next.Value, Assert.IsType<TextBox>(editor.Content).Text);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameOwner_RepeatedLoadedPreservesActiveUnsavedInput(bool header)
    {
        OnSta(() =>
        {
            var vm = Provider();
            var model = Header("sensitive-name");
            UserControl editor = header ? new ProviderHeaderValueBox { DataContext = model }
                : new ProviderApiKeyBox { DataContext = vm };
            using var host = new EditorHost(editor);
            Enter(editor, "synthetic-active-input");
            editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Pump();
            Assert.Equal("synthetic-active-input", Value(editor.Content));
            Assert.Equal("synthetic-active-input", header ? model.Value : vm.EditingApiKey);
        });
    }

    [Fact]
    public async Task FailedSave_WhileEditorsRemainLoadedPreservesKeyAndSensitiveHeaderInput()
    {
        StaTestRunner.EnsureApplication();
        await StaTestRunner.Run(async () =>
        {
            var repository = new FailingReadRepository();
            var vm = Provider(repository);
            vm.EditingProviderId = "synthetic-provider";
            vm.EditingDisplayName = "Synthetic provider";
            vm.EditingBaseUrl = "https://synthetic-provider.test";
            var model = Header("sensitive-name");
            vm.EditingHeaders.Add(model);
            var key = new ProviderApiKeyBox { DataContext = vm };
            var header = new ProviderHeaderValueBox { DataContext = model };
            var panel = new StackPanel();
            panel.Children.Add(key);
            panel.Children.Add(header);
            using var host = new EditorHost(panel);
            Enter(key, "synthetic-unsaved-key");
            Enter(header, "synthetic-unsaved-header");
            await vm.ExecuteConfirmAndSaveAsync();
            Assert.Equal(1, repository.ReadAttempts);
            Assert.Contains("Сбой", vm.StatusMessage);
            Assert.True(key.IsLoaded);
            Assert.True(header.IsLoaded);
            Assert.Equal("synthetic-unsaved-key", vm.EditingApiKey);
            Assert.Equal(vm.EditingApiKey, Value(key.Content));
            Assert.Equal("synthetic-unsaved-header", model.Value);
            Assert.Equal(model.Value, Value(header.Content));
        });
    }

    [Fact]
    public void AccountKey_ExistingUnloadBehaviorStillClearsOwnerAndUi()
    {
        OnSta(() =>
        {
            var vm = new AccountManagementViewModel();
            var editor = new AccountApiKeyBox { DataContext = vm };
            using var host = new EditorHost(editor);
            Enter(editor, "synthetic-account-input");
            Assert.NotEmpty(vm.EnteredApiKey);
            host.Unload(editor);
            Assert.Empty(vm.EnteredApiKey);
            Assert.Empty(Value(editor.Content));
        });
    }

    private static ProvidersAccountsViewModel Provider(IProviderProfileRepository? repository = null) =>
        new(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System), profileRepository: repository);

    private static CustomHeaderItemViewModel Header(string mode) =>
        new(mode == "sensitive-name" ? "X-Token" : "X-Region", "", mode == "explicit-secret",
            mode == "saved-reference" ? "urn:llmworkgui:secret:synthetic-reference" : null);

    private static void Enter(UserControl editor, string value)
    {
        if (editor.Content is PasswordBox password) password.Password = value;
        else Assert.IsType<TextBox>(editor.Content).Text = value;
        Pump();
    }

    private static string Value(object content) => content is PasswordBox password ? password.Password
        : Assert.IsType<TextBox>(content).Text;

    private static void OnSta(Action action)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(action);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class EditorHost : IDisposable
    {
        private readonly Window _window = new()
        {
            Width = 400, Height = 160, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, Title = "Synthetic editor lifetime test"
        };

        public EditorHost(FrameworkElement editor)
        {
            _window.Content = editor;
            _window.Show();
            Pump();
            Assert.True(editor.IsLoaded);
        }

        public void Unload(FrameworkElement editor)
        {
            _window.Content = null;
            Pump();
            Assert.False(editor.IsLoaded);
        }

        public void Load(FrameworkElement editor)
        {
            _window.Content = editor;
            Pump();
            Assert.True(editor.IsLoaded);
        }

        public void Dispose() { _window.Close(); Pump(); }
    }

    private sealed class FailingReadRepository : IProviderProfileRepository
    {
        public int ReadAttempts { get; private set; }
        public Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            ReadAttempts++;
            return Task.FromException<ProviderProfile?>(new InvalidOperationException("Synthetic save read failure"));
        }
        public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertAsync(ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
