using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>One real WPF Application per isolated child process. The parent testhost's Application,
/// dispatcher and personal installation are untouched; only the host dependency is synthetic.</summary>
public sealed class AppShutdownClosingTests
{
    private const string ChildVariable = "LLMWORKGUI_APP_CLOSING_TEST_CHILD";

    [Fact]
    public Task ClosingDrainsAsynchronouslyAndClearsMarkerOnlyAfterSuccess() =>
        RunScenarioAsync(nameof(ClosingDrainsAsynchronouslyAndClearsMarkerOnlyAfterSuccess), false);

    [Fact]
    public Task ClosingFailureKeepsRecoveryMarkerThroughActualApplicationExit() =>
        RunScenarioAsync(nameof(ClosingFailureKeepsRecoveryMarkerThroughActualApplicationExit), true);

    [Fact]
    public Task ExitDisposalFailureStillRaisesExitAndSettlesLifetime() =>
        RunScenarioAsync(nameof(ExitDisposalFailureStillRaisesExitAndSettlesLifetime), false, failDispose: true);

    [Fact]
    public async Task PostStartBootstrapFailureStopsTheOwnedHostBeforeDisposalAndPreservesMarker()
    {
        var testName = nameof(PostStartBootstrapFailureStopsTheOwnedHostBeforeDisposalAndPreservesMarker);
        if (!string.Equals(Environment.GetEnvironmentVariable(ChildVariable), testName, StringComparison.Ordinal))
        {
            await RunChildAsync(testName);
            return;
        }
        StaTestRunner.Run(() =>
        {
            Assert.Null(System.Windows.Application.Current);
            var root = Directory.CreateTempSubdirectory("app-startup-owned-");
            var factory = new SqliteConnectionFactory(Path.Combine(root.FullName, "synthetic.db"));
            var host = new DeferredHost(new StartupServices(factory, new Guard()));
            host.Release.TrySetResult();
            var app = new OwnedStartupApp(host) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Exit += (_, _) => exited.TrySetResult();
            try
            {
                // Real inherited OnStartup is scheduled by Application construction. The owned host
                // starts, creates the real marker, then intentionally lacks the bootstrap migrator.
                Pump(exited.Task);
                Assert.True(app.StartupFailureObserved);
                Assert.Equal(1, host.StartCalls);
                Assert.Equal(1, host.StopCalls);
                Assert.True(host.StopToken.CanBeCanceled);
                Assert.True(host.Disposed);
                Assert.True(host.DisposedAfterStop);
                Assert.True(File.Exists(factory.DatabasePath + ".running"));
                Assert.Null(GetField(app, "_host"));
            }
            finally
            {
                host.Release.TrySetResult();
                if (!exited.Task.IsCompleted) { app.Shutdown(); Pump(exited.Task); }
                var owned = Path.GetFullPath(root.FullName);
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), owned, StringComparison.OrdinalIgnoreCase);
                Directory.Delete(owned, true);
            }
        });
    }

    private static async Task RunScenarioAsync(string testName, bool fail, bool failDispose = false)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(ChildVariable), testName, StringComparison.Ordinal))
        {
            await RunChildAsync(testName);
            return;
        }

        StaTestRunner.Run(() =>
        {
            Assert.Null(System.Windows.Application.Current);
            var root = Directory.CreateTempSubdirectory("app-closing-");
            var host = new DeferredHost(failDispose: failDispose);
            var app = new QuietClosingApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? exitFailure = null;
            app.DispatcherUnhandledException += (_, args) =>
            {
                exitFailure = args.Exception;
                args.Handled = true;
                exited.TrySetResult(); // Do not hang the child if the real OnExit faults before base.
            };
            app.Exit += (_, _) => exited.TrySetResult();
            var factory = new SqliteConnectionFactory(Path.Combine(root.FullName, "synthetic.db"));
            var markerPath = factory.DatabasePath + ".running";
            var marker = new ApplicationRunMarker(factory, new Guard());
            marker.Begin();
            SetField(app, "_host", host);
            SetField(app, "_runMarker", marker);
            var method = typeof(LLMWorkGUI.App.App).GetMethod("OnMainWindowClosing", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var closing = (CancelEventHandler)method.CreateDelegate(typeof(CancelEventHandler), app);
            var window = new Window { Width = 200, Height = 100, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            app.MainWindow = window;
            var cancelledClosures = new List<bool>();
            window.Closing += closing;
            window.Closing += (_, args) => cancelledClosures.Add(args.Cancel);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            try
            {
                window.Show();
                window.Close();
                Assert.Equal(1, host.StopCalls);
                Assert.True(host.StopToken.CanBeCanceled);
                Assert.True(window.IsVisible); Assert.False(closed.Task.IsCompleted);
                Assert.True(File.Exists(markerPath));
                window.Close();
                Assert.Equal(1, host.StopCalls); Assert.Equal(new[] { true, true }, cancelledClosures);

                // Actual dispatcher work executes while the application's async closing handler awaits
                // StopAsync; a synchronous GetResult on the dispatcher would prevent this observation.
                var dispatcherProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => dispatcherProgress.TrySetResult()));
                Pump(dispatcherProgress.Task);
                Assert.True(app.StartupSuppressed);
                Assert.Same(host, GetField(app, "_host"));
                Assert.Same(marker, GetField(app, "_runMarker"));
                Assert.True(window.IsVisible); Assert.False(closed.Task.IsCompleted);
                Assert.True(File.Exists(markerPath));

                if (fail) host.Release.TrySetException(new IOException("Synthetic owned-host cleanup failure"));
                else host.Release.TrySetResult();
                Pump(closed.Task);
                Assert.Equal(1, host.StopCalls);
                Assert.Equal(new[] { true, true, false }, cancelledClosures);
                Assert.Equal(fail, File.Exists(markerPath));
                app.Shutdown(); Pump(exited.Task);
                Assert.Null(exitFailure);
                Assert.Equal(1, host.StopCalls); Assert.True(host.Disposed);
                Assert.Null(GetField(app, "_host"));
                Assert.Equal(fail, File.Exists(markerPath));
            }
            finally
            {
                host.Release.TrySetResult();
                if (!closed.Task.IsCompleted && window.IsVisible) { window.Close(); Pump(closed.Task); }
                if (!exited.Task.IsCompleted) { app.Shutdown(); Pump(exited.Task); }
                var owned = Path.GetFullPath(root.FullName);
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), owned, StringComparison.OrdinalIgnoreCase);
                Directory.Delete(owned, true);
            }
        });
    }

    private static void SetField(object target, string name, object value)
    {
        var field = typeof(LLMWorkGUI.App.App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field); field.SetValue(target, value);
    }

    private static object? GetField(object target, string name)
    {
        var field = typeof(LLMWorkGUI.App.App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field); return field.GetValue(target);
    }

    private static void Pump(Task task)
    {
        var deadline = Stopwatch.StartNew();
        while (!task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(10))
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(task.IsCompleted, "Isolated WPF shutdown did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static async Task RunChildAsync(string testName)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "LLMWorkGUI.sln"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var results = Path.Combine(repository.FullName, "artifacts", "project-closure-20261004", "runtime-shutdown-tests", "app-closing-child");
        Directory.CreateDirectory(results);
        var trxName = testName + "-" + Guid.NewGuid().ToString("N") + ".trx";
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repository.FullName, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "test", Path.Combine(repository.FullName, "tests", "LLMWorkGUI.Ui.Tests", "LLMWorkGUI.Ui.Tests.csproj"),
            "-c", "Release", "--no-build", "--no-restore", "--nologo", "--verbosity", "quiet", "--filter",
            "FullyQualifiedName=LLMWorkGUI.Ui.Tests.AppShutdownClosingTests." + testName,
            "--logger", "trx;LogFileName=" + trxName, "--results-directory", results }) start.ArgumentList.Add(argument);
        start.Environment[ChildVariable] = testName;
        // Defence in depth if a future fixture accidentally enables the normal bootstrap again:
        // HostBootstrapper binds the Storage section, so its store remains in this owned test area.
        start.Environment["Storage__AppDataDirectory"] = Path.Combine(results, trxName + ".bootstrap");
        start.Environment["Storage__DatabaseFileName"] = "synthetic-fallback.db";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The isolated shutdown testhost could not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The isolated shutdown testhost exceeded two minutes.");
        }
        Assert.True(process.ExitCode == 0, await output + Environment.NewLine + await errors);
        var trx = XDocument.Load(Path.Combine(results, trxName));
        var result = Assert.Single(trx.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"
            && element.Attribute("testName")?.Value == "LLMWorkGUI.Ui.Tests.AppShutdownClosingTests." + testName));
        Assert.Equal("Passed", result.Attribute("outcome")?.Value);
    }

    private sealed class DeferredHost(IServiceProvider? services = null, bool failDispose = false) : IHost
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCalls; public int StopCalls; public CancellationToken StopToken;
        public bool Disposed; public bool DisposedAfterStop;
        public IServiceProvider Services { get; } = services ?? new EmptyServices();
        public Task StartAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref StartCalls); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref StopCalls); StopToken = cancellationToken; return Release.Task.WaitAsync(cancellationToken); }
        public void Dispose()
        {
            Disposed = true;
            DisposedAfterStop = StopCalls > 0;
            if (failDispose) throw new IOException("Synthetic host disposal failure");
        }
    }
    // Application construction schedules OnStartup on the dispatcher even without Run(). Suppress
    // production bootstrap only: real inherited Closing handler and OnExit remain under test, and
    // cannot replace the injected host/marker or open the user's default database while pumping.
    private sealed class QuietClosingApp : LLMWorkGUI.App.App
    {
        public bool StartupSuppressed { get; private set; }
        protected override void OnStartup(StartupEventArgs e) => StartupSuppressed = true;
    }
    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type serviceType) => null; }
    private sealed class StartupServices(ISqliteConnectionFactory factory, IApplicationInstanceGuard guard) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(ISqliteConnectionFactory) ? factory
            : type == typeof(IApplicationInstanceGuard) ? guard : null;
    }
    private sealed class OwnedStartupApp(IHost host) : LLMWorkGUI.App.App
    {
        public bool StartupFailureObserved;
        protected override IHost BuildHost(string[] args) => host;
        protected override void ShowStartupFailure(Exception exception) => StartupFailureObserved = true;
    }
    private sealed class Guard : IApplicationInstanceGuard
    {
        public string InstanceId => "synthetic-closing-owner";
        public bool IsPrimarySupervisor => true; public bool IsViewOnly => false;
        public void EnsureSupervisorPermitted() { }
        public void Dispose() { }
    }
}
