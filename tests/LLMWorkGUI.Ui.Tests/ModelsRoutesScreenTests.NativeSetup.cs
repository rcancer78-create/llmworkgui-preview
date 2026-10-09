using System.Windows;
using System.Windows.Controls;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class ModelsRoutesScreenTests
{
    [Fact]
    public void NativeSetupRequiresObservedIdentityAndBothExplicitConsents()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var service = new NativeSetupFixture();
            var refreshed = 0;
            var vm = new NativeRouteSetupViewModel(service, () => { refreshed++; return Task.CompletedTask; });
            vm.SetRoute(NativeSetupRoute("route-one"), NativeSetupProfile());
            Assert.False(vm.CanPreview);
            Assert.False(vm.CanActivate);
            vm.ExpectedEmail = "person@example.invalid";
            Assert.True(vm.CanPreview);
            Pump(vm.PreviewAsync());
            Assert.Equal("person@example.invalid", service.ExpectedEmail);
            Assert.Contains("user-42", vm.ObservedIdentity);
            Assert.Contains("grok-4.7-high", vm.ObservedIdentity);
            Assert.Equal(0, service.Activations);
            vm.MaxDataClass = DataClassification.PrivateSource;
            vm.ConfirmModelSupport = true;
            Assert.False(vm.CanActivate);
            vm.AllowUnverifiedFirstRequest = true;
            Assert.True(vm.CanActivate);
            Pump(vm.ActivateAsync());
            Assert.Equal(1, service.Activations);
            Assert.Equal(DataClassification.PrivateSource, service.Classification);
            Assert.True(service.SupportConfirmed && service.UnverifiedConfirmed);
            Assert.Equal(1, refreshed);
            Assert.False(vm.CanActivate);
            Assert.Contains("ForcedEnabled", vm.StatusMessage);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeSetupChangedEmailOrRouteInvalidatesObservationAndConsents(bool changeEmail)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var service = new NativeSetupFixture();
            var vm = new NativeRouteSetupViewModel(service, () => Task.CompletedTask);
            vm.SetRoute(NativeSetupRoute("route-one"), NativeSetupProfile());
            vm.ExpectedEmail = "person@example.invalid";
            Pump(vm.PreviewAsync());
            vm.ConfirmModelSupport = vm.AllowUnverifiedFirstRequest = true;
            Assert.True(vm.CanActivate);
            if (changeEmail) vm.ExpectedEmail = "other@example.invalid";
            else vm.SetRoute(NativeSetupRoute("route-two"), NativeSetupProfile());
            Assert.False(vm.CanActivate);
            Assert.False(vm.ConfirmModelSupport);
            Assert.False(vm.AllowUnverifiedFirstRequest);
            Pump(vm.ActivateAsync());
            Assert.Equal(0, service.Activations);
        });
    }

    [Fact]
    public void NativeSetupRejectedPreviewCannotActivateAndMissingServiceIsSafe()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var vm = new NativeRouteSetupViewModel(new NativeSetupFixture { Refuse = true }, () => Task.CompletedTask);
            vm.SetRoute(NativeSetupRoute("route-one"), NativeSetupProfile());
            vm.ExpectedEmail = "different@example.invalid";
            Pump(vm.PreviewAsync());
            vm.ConfirmModelSupport = vm.AllowUnverifiedFirstRequest = true;
            Assert.False(vm.CanActivate);
            Assert.Contains("Вход не совпадает", vm.StatusMessage);
            var unavailable = new NativeRouteSetupViewModel(null, () => Task.CompletedTask);
            unavailable.SetRoute(NativeSetupRoute("route-one"), NativeSetupProfile());
            unavailable.ExpectedEmail = "person@example.invalid";
            Assert.False(unavailable.CanPreview);
        });
    }

    [Fact]
    public void ShippedNativeSetupBindsEmailAndExplicitConsentsAtNarrowWidth()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var service = new NativeSetupFixture();
            var vm = new ModelsRoutesViewModel(new NativeSetupConfiguration(), nativeRouteActivation: service);
            Pump(vm.RefreshAsync());
            vm.Profile = vm.Profiles.Single();
            vm.SelectedTabIndex = 1;
            vm.SelectedRoute = vm.Routes.Single();
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            var host = new ContentControl { Content = vm };
            var window = new Window { Content = host, Width = 620, Height = 700, ShowInTaskbar = false };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
            try
            {
                window.Show(); Settle(window);
                var email = Find<TextBox>(host, "NativeCursorExpectedEmail");
                Assert.True(email.IsVisible);
                email.Text = "person@example.invalid";
                Settle(window);
                Assert.Equal(email.Text, vm.NativeRouteSetup.ExpectedEmail);
                Pump(vm.NativeRouteSetup.PreviewAsync());
                var activate = Find<Button>(host, "NativeCursorActivateButton");
                Assert.False(activate.Command.CanExecute(null));
                Find<CheckBox>(host, "NativeCursorModelConsent").IsChecked = true;
                Find<CheckBox>(host, "NativeCursorUnverifiedConsent").IsChecked = true;
                Settle(window);
                Assert.True(activate.Command.CanExecute(null));
                Assert.True(activate.ActualWidth > 0 && activate.ActualWidth < host.ActualWidth);
                Assert.Equal(0, service.Activations);
            }
            finally { window.Close(); }
        });
    }

    private sealed class NativeSetupConfiguration : IModelRouteConfigurationService
    {
        public Task<ModelRouteConfiguration> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ModelRouteConfiguration(
            [NativeSetupProfile()], [new("account", "profile", "Cursor", AuthState.Unknown, false)],
            [new("model", "profile", BackendType.NativeGateway, "grok-4.7-high", "Grok", CapabilityState.Unknown, ModelProvenance.PluginReported, false)],
            [NativeSetupRoute("route-one")]));
        public Task<string> SaveModelAsync(SaveModelConfiguration request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SaveRouteAsync(SaveRouteConfiguration request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public void NativeSetupIgnoresPreviewForSelectionChangedDuringNativeStatusCall()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var pending = new PendingNativeSetupFixture();
            var vm = new NativeRouteSetupViewModel(pending, () => Task.CompletedTask);
            vm.SetRoute(NativeSetupRoute("route-one"), NativeSetupProfile());
            vm.ExpectedEmail = "person@example.invalid";
            var call = vm.PreviewAsync();
            Assert.True(vm.IsBusy);
            vm.SetRoute(NativeSetupRoute("route-two"), NativeSetupProfile());
            pending.Completion.SetResult(new("old-observation", "route-one", "profile", "account", "model",
                "person@example.invalid", "user-42", "grok-4.7-high", DateTimeOffset.UtcNow.AddMinutes(2)));
            Pump(call);
            vm.ConfirmModelSupport = vm.AllowUnverifiedFirstRequest = true;
            Assert.False(vm.CanActivate);
            Assert.DoesNotContain("user-42", vm.ObservedIdentity);
            Assert.Empty(vm.StatusMessage);
        });
    }

    [Fact]
    public void NativeSetupDataClassChangeRevokesBothProfileWideConsents()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var vm = new NativeRouteSetupViewModel(new NativeSetupFixture(), () => Task.CompletedTask);
            vm.SetRoute(NativeSetupRoute("route-one"), NativeSetupProfile());
            vm.ExpectedEmail = "person@example.invalid";
            Pump(vm.PreviewAsync());
            vm.ConfirmModelSupport = vm.AllowUnverifiedFirstRequest = true;
            Assert.True(vm.CanActivate);
            vm.MaxDataClass = DataClassification.PrivateSource;
            Assert.False(vm.CanActivate);
            Assert.False(vm.ConfirmModelSupport);
            Assert.False(vm.AllowUnverifiedFirstRequest);
        });
    }

    private sealed class PendingNativeSetupFixture : INativeGatewayRouteActivationService
    {
        public TaskCompletionSource<NativeGatewayRouteActivationPreview> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NativeGatewayRouteActivationPreview> PreviewAsync(string routeId, string expectedEmail, CancellationToken cancellationToken = default) => Completion.Task;
        public Task<NativeGatewayRouteActivationResult> ActivateAsync(string observationId, bool confirmUserDeclaredModelSupport,
            bool allowUnverifiedFirstRequest, DataClassification maxDataClass, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Not activated");
    }

    private static ModelProfileOption NativeSetupProfile() => new("profile", "Cursor", BackendType.NativeGateway, DataClassification.PublicSource, false);
    private static ConfiguredRoute NativeSetupRoute(string id) => new(id, "profile", "account", "model", null,
        DataClassification.PublicSource, false, 0, null, null, BackendType.NativeGateway);
    private sealed class NativeSetupFixture : INativeGatewayRouteActivationService
    {
        public string? ExpectedEmail { get; private set; }
        public bool Refuse { get; init; }
        public int Activations { get; private set; }
        public bool SupportConfirmed { get; private set; }
        public bool UnverifiedConfirmed { get; private set; }
        public DataClassification Classification { get; private set; }
        public Task<NativeGatewayRouteActivationPreview> PreviewAsync(string routeId, string expectedEmail, CancellationToken cancellationToken = default)
        {
            if (Refuse) throw new InvalidOperationException("Вход не совпадает с ожидаемым email.");
            ExpectedEmail = expectedEmail;
            return Task.FromResult(new NativeGatewayRouteActivationPreview("observation", routeId, "profile", "account", "model",
                expectedEmail, "user-42", "grok-4.7-high", DateTimeOffset.UtcNow.AddMinutes(2)));
        }
        public Task<NativeGatewayRouteActivationResult> ActivateAsync(string observationId, bool confirmUserDeclaredModelSupport,
            bool allowUnverifiedFirstRequest, DataClassification maxDataClass, CancellationToken cancellationToken = default)
        {
            Assert.Equal("observation", observationId);
            Activations++;
            SupportConfirmed = confirmUserDeclaredModelSupport;
            UnverifiedConfirmed = allowUnverifiedFirstRequest;
            Classification = maxDataClass;
            return Task.FromResult(new NativeGatewayRouteActivationResult("route-one", "person@example.invalid", "grok-4.7-high", DateTimeOffset.UtcNow));
        }
    }
}
