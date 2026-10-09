using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace LLMWorkGUI.ActivityLoadDriver;

/// <summary>
/// Entry point of the Phase 11 normative event-load driver.
/// <para>
/// The driver composes the production host over a temporary application-data root and shows the shipped
/// <see cref="MainWindow"/>. It deliberately does not use <c>App.xaml</c>: the production
/// <c>App.OnStartup</c> would open the real <c>%LOCALAPPDATA%\LLMWorkGUI</c>, and a load run must never
/// write into a user's profile. It never contacts an external provider, never reads a credential and
/// never records a reviewer verdict.
/// </para>
/// </summary>
internal static class Program
{
    private static DriverTrace? _trace;

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

        ActivityLoadOptions options;

        try
        {
            options = ActivityLoadOptions.Parse(args);
        }
        catch (ActivityLoadUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        Directory.CreateDirectory(options.RunRoot);

        using var trace = new DriverTrace(Path.Combine(options.RunRoot, "driver-trace.log"));
        _trace = trace;

        // Both failure channels are recorded. A dispatcher-level fault in particular would otherwise
        // terminate the process with nothing on disk but an empty report.
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            trace.Step("UNHANDLED " + eventArgs.ExceptionObject);

        trace.Step($"driver start; mode={options.ModeDisplay}; runRoot={options.RunRoot}");
        trace.Step($"profile: {options.Profile.Describe()}");

        var report = new ActivityLoadReport(options, DateTimeOffset.UtcNow);
        var exitCode = 1;

        try
        {
            exitCode = Run(options, report, trace);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            trace.Step("RUN THREW " + exception);
            report.Add(new ActivityCriterionResult(
                "driver-completed",
                "The driver itself ran to completion.",
                ActivityCriterionOutcome.Fail,
                exception.ToString()));
        }

        try
        {
            trace.Step("writing report");
            WriteReport(options, report);
        }
        catch (Exception exception)
        {
            trace.Step("REPORT WRITE FAILED " + exception);
            throw;
        }

        trace.Step($"driver exit={exitCode}");
        return exitCode;
    }

    private static int Run(ActivityLoadOptions options, ActivityLoadReport report, DriverTrace trace)
    {
        var appData = Path.Combine(options.RunRoot, "appdata");
        Directory.CreateDirectory(appData);

        trace.Step("building host");
        using var host = HostBootstrapper
            .CreateHostBuilder(appDataDirectory: appData)
            .ConfigureServices((_, services) =>
            {
                services.AddAppUi();
                services.AddUnifiedWorkspaceShell();

                // The shipped registration and nothing else. An earlier version of this driver composed the
                // ingestion boundary with its own profile-sized bound (190 001) while the search index
                // stayed at the shipped 100 000, so 90 000 events were retained and pageable but not
                // searchable - and the run then reported "memory is bounded" for a window that had been
                // grown to hold everything. The product's own bound is now what is measured: all 190 000
                // events stay durably available through the journal, and the window stays at 100 000.
                services.AddSingleton(_ => new ActivityJournalOptions
                {
                    RetentionLimit = options.JournalRetentionLimit
                });
            })
            .Build();

        trace.Step("host built; starting");
        host.StartAsync().GetAwaiter().GetResult();
        trace.Step("host started");

        // The composed bound and the shipped default are both reported, and the report fails the memory
        // criterion unless they are the same number.
        var composedService = host.Services.GetRequiredService<IActivityCenterService>();
        report.ComposedWindowCapacity = composedService.Capacity;
        report.ShippedWindowCapacity = ActivityCenterService.DefaultCapacity;
        report.SetFact(
            "retention.composedWindowCapacity",
            composedService.Capacity.ToString("N0", CultureInfo.InvariantCulture));
        report.SetFact(
            "retention.shippedWindowCapacity",
            ActivityCenterService.DefaultCapacity.ToString("N0", CultureInfo.InvariantCulture));
        trace.Step(
            $"host: composed window capacity={composedService.Capacity} "
            + $"(shipped default {ActivityCenterService.DefaultCapacity})");

        var application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        // The shipped App.xaml merges exactly this dictionary; the driver does not use App.xaml because
        // App.OnStartup would open the real user profile.
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml",
                UriKind.Absolute)
        });

        application.Dispatcher.UnhandledException += (_, eventArgs) =>
        {
            trace.Step("DISPATCHER UNHANDLED " + eventArgs.Exception);
            report.Add(new ActivityCriterionResult(
                "driver-completed",
                "The driver itself ran to completion.",
                ActivityCriterionOutcome.Fail,
                eventArgs.Exception?.ToString() ?? "a dispatcher fault terminated the run"));
            eventArgs.Handled = true;
        };

        trace.Step("running HostBootstrapper.InitializeAsync (migrations)");
        HostBootstrapper.InitializeAsync(host).GetAwaiter().GetResult();
        trace.Step("migrations complete");

        trace.Step("initializing theme");
        host.Services.GetRequiredService<IThemeService>().InitializeAsync().GetAwaiter().GetResult();
        trace.Step("theme initialized");

        trace.Step("initializing MainWindowViewModel");
        host.Services.GetRequiredService<MainWindowViewModel>().InitializeAsync().GetAwaiter().GetResult();
        trace.Step("MainWindowViewModel initialized");

        trace.Step("resolving UnifiedWorkspaceShellViewModel");
        var shell = host.Services.GetRequiredService<UnifiedWorkspaceShellViewModel>();
        trace.Step("resolving ActivityCenterViewModel");
        var activity = host.Services.GetRequiredService<ActivityCenterViewModel>();
        trace.Step("view models resolved");

        trace.Step("creating MainWindow");
        var window = new MainWindow(host.Services.GetRequiredService<MainWindowViewModel>(), shell)
        {
            ShowActivated = true,
            Width = 1600,
            Height = 1000
        };

        trace.Step("showing MainWindow");
        window.Show();
        ActivityWindowCapture.UiDispatcher = window.Dispatcher;
        trace.Step("MainWindow shown; IsVisible=" + window.IsVisible);

        var walk = new ActivityLoadWalk(host.Services, window, shell, activity, options, report, trace);
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var cancellation = new CancellationTokenSource();

        dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(async () =>
            {
                trace.Step("dispatched walk starting");
                try
                {
                    await walk.RunAsync(cancellation.Token);
                    trace.Step("walk finished");
                }
                catch (OperationCanceledException)
                {
                    trace.Step("walk cancelled");
                    report.Add(new ActivityCriterionResult(
                        "driver-completed",
                        "The driver itself ran to completion.",
                        ActivityCriterionOutcome.Fail,
                        "the run was cancelled"));
                }
                catch (Exception exception)
                {
                    trace.Step("WALK THREW " + exception);
                    Console.Error.WriteLine(exception);
                    report.Add(new ActivityCriterionResult(
                        "driver-completed",
                        "The driver itself ran to completion.",
                        ActivityCriterionOutcome.Fail,
                        exception.ToString()));
                }
                finally
                {
                    try
                    {
                        trace.Step("harvesting latency");
                        HarvestLatency(activity, report);
                        ActivityLoadCriteria.Evaluate(report, Array.Empty<ActivityCriterionResult>());
                        report.Complete(DateTimeOffset.UtcNow);
                        trace.Step("closing window");
                    }
                    catch (Exception exception)
                    {
                        report.Add(new ActivityCriterionResult("driver-finalization", "Final measurements completed.",
                            ActivityCriterionOutcome.Fail, exception.GetType().Name));
                    }
                    finally
                    {
                        try { window.Close(); }
                        catch (Exception exception)
                        {
                            report.Add(new ActivityCriterionResult("driver-window-close", "The owned window closed.",
                                ActivityCriterionOutcome.Fail, exception.GetType().Name));
                        }
                        finally
                        {
                            // A measurement, trace or window-close failure cannot keep the handled
                            // async dispatcher callback alive indefinitely.
                            dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                        }
                    }
                }
            }));

        trace.Step("entering Dispatcher.Run");
        Dispatcher.Run();
        trace.Step("Dispatcher.Run returned");

        using (var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            host.StopAsync(shutdown.Token).GetAwaiter().GetResult();
        }

        trace.Step("host stopped");
        return report.ExitCode;
    }

    /// <summary>
    /// Harvests the latency samples after the stream has stopped. This is deliberately outside the
    /// generator's send loop: measuring inside the loop would charge the producer for the measurement.
    /// </summary>
    private static void HarvestLatency(ActivityCenterViewModel activity, ActivityLoadReport report)
    {
        var samples = activity.HarvestLatencySamples()
            .Select(sample => sample.Milliseconds)
            .ToArray();

        report.VisibilityLatency = ActivityLatencyStatistics.FromSamples(
            samples,
            observedCount: activity.Latency.ObservedCount,
            discardedSamples: activity.Latency.DiscardedSampleCount,
            droppedPending: activity.Latency.DroppedPendingCount,
            pendingCount: activity.Latency.PendingCount);

        // Dispatcher interaction latency is reported separately: it is the cost of one dispatched refresh
        // and of the operator's own actions, not the ingestion-to-visible latency of the stream. The
        // durable query is deliberately not part of it - that work happens on the query worker, so adding
        // it here would report time the dispatcher never spent.
        var dispatcherWork = activity.Latency.DrainDispatcherWorkSamples()
            .Concat(report.UiActionSamples)
            .ToArray();

        report.DispatcherLatency = ActivityLatencyStatistics.FromSamples(
            dispatcherWork,
            observedCount: activity.Latency.DispatcherObservedCount + report.UiActionSamples.Count,
            discardedSamples: activity.Latency.DispatcherDiscardedSampleCount);

        // The off-dispatcher query cost, as its own series. This is the number the search responsiveness
        // work moved off the UI thread, so hiding it inside the dispatcher figure would hide the change.
        var queryWork = activity.Latency.DrainQueryWorkSamples().ToArray();

        report.QueryLatency = ActivityLatencyStatistics.FromSamples(
            queryWork,
            observedCount: activity.Latency.QueryObservedCount,
            discardedSamples: activity.Latency.QueryDiscardedSampleCount);

        _trace?.Step(
            $"harvested {samples.Length} latency samples over {activity.Latency.ObservedCount} visible events; "
            + $"{dispatcherWork.Length} dispatcher-work samples; {queryWork.Length} off-dispatcher query samples");

        report.SetFact("latency.sampleCount", samples.Length.ToString("N0", CultureInfo.InvariantCulture));
        report.SetFact("latency.observed", activity.Latency.ObservedCount.ToString("N0", CultureInfo.InvariantCulture));
        report.SetFact("latency.pending", activity.Latency.PendingCount.ToString("N0", CultureInfo.InvariantCulture));
        report.SetFact("ui.coalescedAppends", activity.CoalescedAppendCount.ToString("N0", CultureInfo.InvariantCulture));
        report.SetFact("ui.refreshCount", activity.RefreshCount.ToString("N0", CultureInfo.InvariantCulture));
        report.SetFact("ui.staleRefreshes", activity.StaleRefreshCount.ToString("N0", CultureInfo.InvariantCulture));
        report.SetFact("ui.staleResults", activity.StaleResultCount.ToString("N0", CultureInfo.InvariantCulture));
        report.SetFact("ui.queryFailures", activity.QueryFailureCount.ToString("N0", CultureInfo.InvariantCulture));
        report.SetFact(
            "ui.dispatcherWork",
            string.Create(
                CultureInfo.InvariantCulture,
                $"last {activity.LastDispatcherWorkMilliseconds:N1} ms, max {activity.MaxDispatcherWorkMilliseconds:N1} ms"));
        report.SetFact(
            "ui.queryWork",
            string.Create(
                CultureInfo.InvariantCulture,
                $"last {activity.LastQueryWorkMilliseconds:N1} ms, max {activity.MaxQueryWorkMilliseconds:N1} ms on the query worker, outside the dispatcher"));
    }

    private static void WriteReport(ActivityLoadOptions options, ActivityLoadReport report)
    {
        Directory.CreateDirectory(options.RunRoot);
        var markdownPath = Path.Combine(options.RunRoot, "activity-load-report.md");
        File.WriteAllText(markdownPath, report.ToMarkdown(), Encoding.UTF8);

        Console.WriteLine(report.ToMarkdown());
        Console.WriteLine($"Report written to {markdownPath}");
        Console.WriteLine($"Screenshots written to {options.ScreenshotDirectory}");
    }
}
