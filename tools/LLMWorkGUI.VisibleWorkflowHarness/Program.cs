using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LLMWorkGUI.VisibleWorkflowHarness;

/// <summary>
/// Entry point of the visible workflow acceptance harness.
/// <para>
/// It composes the production host exactly the way <c>App.OnStartup</c> does, with one difference: the
/// application-data root is a temporary directory. The shell's layout memory follows that root on its own,
/// so the harness registers nothing extra for it. The unpackaged production <c>App</c> is never started, so
/// the real <c>%LOCALAPPDATA%\LLMWorkGUI</c> is not opened, and no credential, API call, external model
/// process or Mirasim session is created.
/// </para>
/// <para>
/// <c>--mode visible</c> keeps the shown window on the desktop until a human closes it or the hold timeout
/// elapses, which is what a supervisor interacts with. <c>--mode ci</c> runs the same walk and closes the
/// window itself.
/// </para>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // A windowed process may have no console attached; the report file is the real output.
        }

        HarnessOptions options;

        try
        {
            if (ReadReferenceScreenshotOptOut() is { } message)
            {
                Console.Error.WriteLine(message);
                return 2;
            }

            options = HarnessOptions.Parse(args);
        }
        catch (HarnessUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var report = new AcceptanceReport(options, DateTimeOffset.UtcNow);
        var exitCode = 1;

        try
        {
            exitCode = Run(options, report);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            report.Add(new AcceptanceStep(
                "harness",
                "The harness itself completed",
                "Compose the production host, show the window and run the walk.",
                "No harness-level fault.").Complete(AcceptanceOutcome.Fail, exception.Message, null, exception.ToString()));
        }

        WriteReport(options, report);
        return exitCode;
    }

    private static int Run(HarnessOptions options, AcceptanceReport report)
    {
        var consoleWindowsBefore = ConsoleWindowProbe.CaptureVisibleConsoleWindows();
        var attachedConsoleAtStart = ConsoleWindowProbe.HasAttachedConsole;
        var host = BuildHost(options.RunRoot);

        try
        {
            host.StartAsync().GetAwaiter().GetResult();

            var application = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };

            // The shipped App.xaml merges exactly this dictionary into Application.Resources; the harness
            // does not use App.xaml because App.OnStartup would open the real user profile.
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml",
                    UriKind.Absolute)
            });

            HostBootstrapper.InitializeAsync(host).GetAwaiter().GetResult();
            host.Services.GetRequiredService<IThemeService>().InitializeAsync().GetAwaiter().GetResult();
            host.Services.GetRequiredService<MainWindowViewModel>().InitializeAsync().GetAwaiter().GetResult();

            AcceptanceSeed
                .SeedAsync(
                    host.Services,
                    Path.Combine(options.RunRoot, "workspace"),
                    System.Threading.CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var library = host.Services.GetRequiredService<WorkflowLibraryViewModel>();
            var shell = host.Services.GetRequiredService<UnifiedWorkspaceShellViewModel>();

            var window = new MainWindow(
                host.Services.GetRequiredService<MainWindowViewModel>(),
                shell)
            {
                ShowActivated = true
            };

            report.Facts["window.title"] = window.Title;
            report.Facts["window.startupLocation"] = window.WindowStartupLocation.ToString();
            report.Facts["window.windowStyle"] = window.WindowStyle.ToString();
            report.Facts["window.showActivated"] = window.ShowActivated.ToString();
            report.Facts["run.root"] = options.RunRoot;
            report.Facts["run.mode"] = options.Mode;
            report.Facts["host.appDataDirectory"] = host.Services
                .GetRequiredService<StorageOptions>()
                .AppDataDirectory ?? "(none)";
            report.Facts["shell.layoutFile"] = ((LayoutPersistenceService)host.Services
                .GetRequiredService<ILayoutPersistenceService>()).FilePath;
            report.Facts["shell.layoutFileResolvedBy"] =
                "production registration over the configured app-data root; the harness registers nothing";
            report.Facts["reviewEvidence.repository"] = host.Services
                .GetRequiredService<IWorkflowReviewEvidenceRepository>()
                .GetType()
                .Name;

            window.Show();

            var walk = new AcceptanceWalk(window, shell, library, host.Services, options, report);
            var dispatcher = Dispatcher.CurrentDispatcher;

            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(async () =>
                {
                    try
                    {
                        await walk.RunAsync();
                        if (options.AllUi)
                            await new FullUiWalk(window, shell, options, report, host.Services).RunAsync();
                        var consoleWindowsAfter = ConsoleWindowProbe.CaptureVisibleConsoleWindows();
                        consoleWindowsAfter.ExceptWith(consoleWindowsBefore);
                        var attachedConsoleAtEnd = ConsoleWindowProbe.HasAttachedConsole;
                        report.Facts["console.attachedAtStart"] = attachedConsoleAtStart.ToString();
                        report.Facts["console.attachedAtEnd"] = attachedConsoleAtEnd.ToString();
                        report.Facts["console.additionalVisibleAtEnd"] = consoleWindowsAfter.Count.ToString();
                        report.Facts["console.scope"] = "Attached console is process-specific. Desktop ConsoleWindowClass before/after count is informational and may include unrelated apps; no titles/content read. No transient-window, Windows Terminal or external-native-backend guarantee.";
                        var noConsole = !attachedConsoleAtStart && !attachedConsoleAtEnd;
                        report.Add(new AcceptanceStep("console-snapshot", "GUI walk needs no attached console",
                            "Sample attached console and additional visible Win32 console windows at run boundaries.",
                            "The harness process has no attached console at either boundary; desktop count is informational.")
                            .Complete(noConsole ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
                                $"Attached start={attachedConsoleAtStart}; attached end={attachedConsoleAtEnd}; additional visible={consoleWindowsAfter.Count}.", null));
                    }
                    catch (Exception exception)
                    {
                        report.Add(new AcceptanceStep(
                            "walk",
                            "The scripted walk completed",
                            "Drive every visible step on the shown window.",
                            "No harness-level fault.").Complete(
                                AcceptanceOutcome.Fail,
                                exception.Message,
                                null,
                                exception.ToString()));
                    }
                    finally
                    {
                        report.Complete(DateTimeOffset.UtcNow);
                        HoldForInteraction(window, options, dispatcher);
                    }
                }));

            Dispatcher.Run();

            return report.ExitCode;
        }
        finally
        {
            try
            {
                host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // A shutdown fault must not replace the acceptance outcome.
            }

            host.Dispose();
        }
    }

    private static IHost BuildHost(string runRoot)
    {
        var appData = Path.Combine(runRoot, "appdata");
        Directory.CreateDirectory(appData);

        // The shell's layout memory is bound to the configured application-data root by production code,
        // so nothing here has to redirect it: an acceptance run cannot reach the real user profile.
        return HostBootstrapper
            .CreateHostBuilder(appDataDirectory: appData)
            .ConfigureServices((_, services) =>
            {
                services.AddAppUi();
                services.AddUnifiedWorkspaceShell();
            })
            .Build();
    }

    private static void HoldForInteraction(Window window, HarnessOptions options, Dispatcher dispatcher)
    {
        if (options.IsVisibleMode)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            var deadline = DateTime.UtcNow + options.HoldTimeout;
            timer.Tick += (_, _) =>
            {
                if (!window.IsVisible || DateTime.UtcNow >= deadline)
                {
                    timer.Stop();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }
            };

            window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            timer.Start();

            return;
        }

        window.Close();
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
    }

    /// <summary>
    /// The harness never writes reference screenshot fixtures. If the caller exported the variable that
    /// redirects the suite's screenshots at the fixture folder, the run is refused rather than silently
    /// producing a different kind of evidence.
    /// </summary>
    private static string? ReadReferenceScreenshotOptOut()
    {
        var value = Environment.GetEnvironmentVariable("LLMWORKGUI_UPDATE_REFERENCE_SCREENSHOTS");

        return string.IsNullOrWhiteSpace(value)
            ? null
            : "LLMWORKGUI_UPDATE_REFERENCE_SCREENSHOTS is set. This harness never updates the reference "
            + "screenshot fixtures; unset the variable and run again.";
    }

    private static void WriteReport(HarnessOptions options, AcceptanceReport report)
    {
        var markdownPath = Path.Combine(options.RunRoot, "acceptance-report.md");
        File.WriteAllText(markdownPath, report.ToMarkdown(), Encoding.UTF8);

        Console.WriteLine(report.ToMarkdown());
        Console.WriteLine($"Report written to {markdownPath}");
        Console.WriteLine($"Screenshots written to {options.ScreenshotDirectory}");
    }
}
