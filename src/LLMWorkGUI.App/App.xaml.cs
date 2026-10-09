using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LLMWorkGUI.App;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private IHost? _host;
    private ActivityCenterEventBridge? _activityEventBridge;
    private ApplicationRunMarker? _runMarker;
    private bool _shutdownAttempted;
    private bool _shutdownCompleted;
    private readonly CancellationTokenSource _startupCancellation = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var hostStarted = false;

        try
        {
            _host = BuildHost(e.Args);

            await _host.StartAsync();
            hostStarted = true;
            _runMarker = new ApplicationRunMarker(
                _host.Services.GetRequiredService<ISqliteConnectionFactory>(),
                _host.Services.GetRequiredService<IApplicationInstanceGuard>());
            var previousLifetimeInterrupted = _runMarker.Begin();
            await HostBootstrapper.InitializeAsync(_host, recoverInterruptedWorkflows: previousLifetimeInterrupted);

            var themeService = _host.Services.GetRequiredService<IThemeService>();
            await themeService.InitializeAsync();

            var viewModel = _host.Services.GetRequiredService<MainWindowViewModel>();

            // Connect the product's own event sources to the Activity Center before the window is shown.
            // Resolving the bridge is what subscribes it: without this the journal of a running application
            // would only ever hold events a load driver produced.
            var activityBridge = _host.Services.GetService<ActivityCenterEventBridge>();

            var shellViewModel = _host.Services.GetService<UnifiedWorkspaceShellViewModel>();

            var window = new MainWindow(viewModel, shellViewModel);
            MainWindow = window;
            window.Closing += OnMainWindowClosing;
            window.Show();

            _activityEventBridge = activityBridge;

            // The shell is usable while optional PATH discovery is pending. Yield below render/input
            // before starting it, and cancel its result publication when this GUI lifetime ends.
            var startupToken = _startupCancellation.Token;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            await viewModel.InitializeAsync(startupToken);
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _startupCancellation.Cancel();
            _activityEventBridge?.Dispose();
            _activityEventBridge = null;
            _shutdownAttempted = true;
            try
            {
                if (hostStarted && _host is not null) await StopHostAsync(_host);
            }
            catch { /* Failed startup never clears the recovery marker, including stop failures. */ }
            try { _host?.Dispose(); }
            catch { /* Disposal failure must not abandon the WPF exit protocol. */ }
            finally { _host = null; _runMarker = null; _shutdownCompleted = true; }
            ShowStartupFailure(exception);
            Shutdown(1);
        }
    }

    // These boundaries let an isolated application exercise the real startup/exit protocol with an
    // owned host and without a modal message box. Production still uses the same composition and UI.
    protected virtual IHost BuildHost(string[] args)
    {
        var builder = HostBootstrapper.CreateHostBuilder(args);
        builder.ConfigureServices((_, services) =>
        {
            services.AddAppUi();
            services.AddUnifiedWorkspaceShell();
        });
        return builder.Build();
    }

    protected virtual void ShowStartupFailure(Exception exception) => MessageBox.Show(
        $"Не удалось запустить LLM Work GUI: {UiErrorMessage.Describe(exception)}",
        "LLM Work GUI", MessageBoxButton.OK, MessageBoxImage.Error);

    private static async Task StopHostAsync(IHost host)
    {
        using var timeout = new CancellationTokenSource(ShutdownTimeout);
        // The wait is bounded even when a hosted service does not honor its cancellation token.
        // Timing out is a failed stop, never evidence permitting marker removal.
        await host.StopAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownAttempted) { e.Cancel = !_shutdownCompleted; return; }
        e.Cancel = true;
        _shutdownAttempted = true;
        _startupCancellation.Cancel();
        _activityEventBridge?.Dispose();
        _activityEventBridge = null;
        // Keep the dispatcher alive while composer cancellation and native ownership cleanup drain.
        // Blocking OnExit on the UI thread can deadlock a pending consent dialog continuation.
        try
        {
            if (_host is not null) await StopHostAsync(_host);
            _runMarker?.CompleteGracefulShutdown();
        }
        catch
        {
            // The persisted run marker and uncertain native reservations are recovery evidence.
            // A timeout or cleanup failure must never be recorded as a graceful exit.
        }
        finally
        {
            _shutdownCompleted = true;
            if (sender is Window window) window.Close();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupCancellation.Cancel();
        // Unsubscribed before the host stops, so a source that raises during shutdown cannot append into a
        // journal whose write queue is already being torn down.
        _activityEventBridge?.Dispose();
        _activityEventBridge = null;

        if (_host is not null)
        {
            try
            {
                if (!_shutdownAttempted)
                {
                    _shutdownAttempted = true;
                    StopHostAsync(_host).GetAwaiter().GetResult();
                    if (e.ApplicationExitCode == 0) _runMarker?.CompleteGracefulShutdown();
                }
            }
            catch { /* Preserve the run marker on an unconfirmed shutdown. */ }
            finally
            {
                try { _host.Dispose(); }
                catch { /* Final application cleanup still runs; no disposal exception escapes OnExit. */ }
                _host = null;
                _runMarker = null;
            }
        }

        _startupCancellation.Dispose();
        base.OnExit(e);
    }
}
