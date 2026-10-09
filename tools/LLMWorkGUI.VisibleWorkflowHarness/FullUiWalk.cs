using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using LLMWorkGUI.App.Help;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace LLMWorkGUI.VisibleWorkflowHarness;

/// <summary>Opt-in acceptance of the production shell using WPF automation providers.
/// All mutations stay in the harness temporary profile. No remote model requests are issued.</summary>
internal sealed class FullUiWalk(Window window, UnifiedWorkspaceShellViewModel shell,
    HarnessOptions options, AcceptanceReport report, IServiceProvider services)
{
    private int sequence;
    private readonly List<object> inventory = new();
    private readonly HashSet<string> invoked = new();
    private readonly List<string> assertions = new();
    private readonly List<string> interactions = new();
    private IEnumerable<T> Visible<T>() where T : FrameworkElement =>
        UiProbe.Descendants<T>(window).Where(x => x.IsVisible);
    private static string Binding(DependencyObject item, DependencyProperty property) =>
        BindingOperations.GetBinding(item, property)?.Path?.Path ?? "";
    private Button Find(string id) => Visible<Button>().First(x => x.Name == id ||
        Binding(x, Button.CommandProperty) == id || x.Content as string == id);
    private async Task Click(string id)
    {
        CommandManager.InvalidateRequerySuggested();
        await WindowCapture.SettleAsync();
        var button = Find(id);
        if (!button.IsEnabled) throw new InvalidOperationException($"Disabled: {id}");
        button.BringIntoView();
        await WindowCapture.SettleAsync();
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        invoked.Add(id);
        interactions.Add($"Control {id}: enabled and WPF invoke provider executed.");
        await WindowCapture.SettleAsync();
        await Task.Delay(200);
    }
    private void Input(string path, string value)
    {
        var box = Visible<TextBox>().First(x => x.Name == path || Binding(x, TextBox.TextProperty) == path);
        box.BringIntoView();
        ((IValueProvider)new TextBoxAutomationPeer(box).GetPattern(PatternInterface.Value)).SetValue(value);
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
    private async Task Step(string title, Func<Task> action)
    {
        assertions.Clear();
        interactions.Clear();
        var id = $"full-{++sequence:000}";
        var step = new AcceptanceStep(id, title, "Interact with shipped controls through WPF automation providers.",
            "Automation actions complete; any explicit predicates must evaluate true.");
        try
        {
            await action();
            await WindowCapture.SettleAsync();
            var observed = "Interactions: " + (interactions.Count == 0 ? "none recorded." : string.Join(" ", interactions))
                + " Assertions: " + (assertions.Count == 0
                    ? "none; this step proves action completion and captures a screenshot only."
                    : string.Join(" ", assertions));
            report.Add(step.Complete(AcceptanceOutcome.Pass, observed,
                WindowCapture.Capture(window, options.ScreenshotDirectory, id)));
        }
        catch (Exception ex)
        {
            report.Add(step.Complete(AcceptanceOutcome.Fail, ex.Message,
                WindowCapture.Capture(window, options.ScreenshotDirectory, id), ex.ToString()));
        }
    }
    private void Check(bool result, string message, [CallerArgumentExpression("result")] string predicate = "")
    {
        if (!result) throw new InvalidOperationException(message);
        assertions.Add($"Predicate ({predicate}) evaluated true.");
    }
    private async Task Navigate(ScreenId screen)
    {
        var list = Visible<ListBox>().Single(x => Binding(x, ItemsControl.ItemsSourceProperty) == "NavigationItems");
        var item = list.Items.Cast<NavigationItemViewModel>().Single(x => x.Id == screen);
        list.ScrollIntoView(item);
        await WindowCapture.SettleAsync();
        ((ISelectionItemProvider)new ListBoxItemAutomationPeer(item, new ListBoxAutomationPeer(list)).GetPattern(PatternInterface.SelectionItem)).Select();
        await WindowCapture.SettleAsync();
        await Task.Delay(250);
        Check(shell.ActiveScreenId == screen, $"Navigation failed: {screen}");
    }
    private void Record(string state)
    {
        WindowCapture.Capture(window, options.ScreenshotDirectory, "state-" + state);
        inventory.Add(new { state, controls = Visible<Control>()
            .Where(x => x is ButtonBase or TextBox or ComboBox or ListBox or TabItem or Expander)
            .Select(x => new { type = x.GetType().Name, x.Name, x.IsEnabled,
                label = (x as ContentControl)?.Content as string,
                command = Binding(x, ButtonBase.CommandProperty),
                textBinding = Binding(x, TextBox.TextProperty),
                width = x.ActualWidth, height = x.ActualHeight }).ToArray() });
    }
    public async Task RunAsync()
    {
        report.Facts["fullUi.interaction"] = "WPF IInvokeProvider/IValueProvider/IToggleProvider on the shown production window; not OS mouse simulation.";
        if (shell.IsWorkflowConsoleOpen) await Click("CloseWorkflowConsoleButton");
        foreach (var theme in new[] { "Dark", "Light" })
        {
            await Click(theme + "ThemeButton");
            foreach (var size in new[] { (1280d, 800d), (900d, 560d) })
            {
                window.Width = size.Item1; window.Height = size.Item2;
                foreach (var screen in Enum.GetValues<ScreenId>())
                    await Step($"{theme} {size.Item1}x{size.Item2}: {screen}", async () =>
                    {
                        await Navigate(screen);
                        Record($"{theme}-{size.Item1}-{screen}");
                    });
            }
        }
        window.Width = 1280; window.Height = 800;
        await Click("DarkThemeButton");
        await Step("Offline guide: open, search, navigate and close", async () =>
        {
            await Click("OpenHelpGuideButton");
            var guide = window.OwnedWindows.OfType<HelpGuideWindow>().Single();
            try
            {
                var search = UiProbe.Require<TextBox>(guide, "SearchBox");
                ((IValueProvider)new TextBoxAutomationPeer(search).GetPattern(PatternInterface.Value))
                    .SetValue("резервн");
                await WindowCapture.SettleAsync();
                var reader = UiProbe.Require<RichTextBox>(guide, "GuideReader");
                Check(reader.Selection.Text.Contains("резервн", StringComparison.OrdinalIgnoreCase), "Guide search selected no result");
                var next = UiProbe.Require<Button>(guide, "NextMatchButton");
                ((IInvokeProvider)new ButtonAutomationPeer(next).GetPattern(PatternInterface.Invoke)).Invoke();
                await WindowCapture.SettleAsync();
                WindowCapture.Capture(guide, options.ScreenshotDirectory, "guide-search");
                var contents = UiProbe.Require<ListBox>(guide, "ContentsList");
                contents.SelectedIndex = contents.Items.Count - 1;
                await WindowCapture.SettleAsync();
                var headingBounds = ((HelpHeading)contents.SelectedItem).Paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                Check(!headingBounds.IsEmpty && headingBounds.Top >= 0 && headingBounds.Top < reader.ViewportHeight,
                    "The last guide heading is outside the reader viewport");
                WindowCapture.Capture(guide, options.ScreenshotDirectory, "guide-last-section");
                var close = UiProbe.Require<Button>(guide, "CloseGuideButton");
                ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)).Invoke();
                await WindowCapture.SettleAsync();
                Check(!guide.IsVisible, "Guide close button did not close the window");
                interactions.Add("Guide search, Next and Close executed through WPF automation providers; last contents item selected.");
            }
            finally { if (guide.IsVisible) guide.Close(); }
        });
        await Step("Navigation pane hides and returns", async () =>
        { await Click("ToggleLeftPaneButton"); await Click("ToggleLeftPaneButton"); });
        await Step("System theme selection and dark restoration", async () =>
        { await Click("SystemThemeButton"); await Click("DarkThemeButton"); });
        await Step("Command palette empty search and Escape", async () =>
        {
            await Click("OpenCommandPaletteButton"); Input("SearchBox", "zz-no-command-123");
            await WindowCapture.SettleAsync();
            Check(!shell.CommandPalette.HasResults, "Search should have no results");
            Record("palette-empty");
            UiProbe.InvokeShortcut(window, Key.Escape, ModifierKeys.None);
        });
        await Step("Command palette keyboard selection and execution", async () =>
        {
            await Click("OpenCommandPaletteButton"); Input("SearchBox", "");
            UiProbe.InvokeShortcut(Visible<LLMWorkGUI.App.Views.CommandPaletteView>().Single(), Key.Down, ModifierKeys.None);
            UiProbe.InvokeShortcut(Visible<LLMWorkGUI.App.Views.CommandPaletteView>().Single(), Key.Enter, ModifierKeys.None);
            await WindowCapture.SettleAsync(); Check(!shell.CommandPalette.IsOpen, "Palette remains open");
        });
        foreach (var screen in new[] { ScreenId.Projects, ScreenId.Models, ScreenId.Sessions, ScreenId.Runs })
            await Step($"{screen}: refresh, search, clear", async () =>
            {
                await Navigate(screen); await Click("RefreshCommand");
                Input("CatalogSearchInput", "zz-no-result-123"); await WindowCapture.SettleAsync();
                Check(shell.ActiveScreen is ModelsRoutesViewModel modelRoutes ? modelRoutes.IsEmpty
                    : ((CatalogScreenViewModel)shell.ActiveScreen).Entries.Count == 0, "Search did not empty the list");
                Input("CatalogSearchInput", "");
            });
        await Step("Projects: reject missing directory", async () =>
        { await Navigate(ScreenId.Projects); Input("ProjectPathInput", Path.Combine(options.RunRoot, "missing")); await Click("AddProjectCommand"); });
        await Step("Projects: add directory and refuse duplicate", async () =>
        {
            Input("CatalogSearchInput", "");
            var path = Path.Combine(options.RunRoot, "added-project"); Directory.CreateDirectory(path);
            Input("ProjectPathInput", path); await Click("AddProjectCommand");
            Input("ProjectPathInput", path); await Click("AddProjectCommand");
            Check(((CatalogScreenViewModel)shell.ActiveScreen).Entries.Count == 2, "Expected seeded plus added project");
        });
        await Providers();
        await Step("Workspace: refresh native model choices and inspect backend panels", async () =>
        {
            await Navigate(ScreenId.Workspace); await Click("RefreshOpenCodeRoutesCommand");
            var panels = Visible<ContentControl>().Where(x => x.Content is CursorWorkspaceViewModel or MirasimWorkspaceViewModel).ToArray();
            Check(panels.Length == 2, "Expected both composed native backend panels");
            foreach (var panel in panels)
            {
                panel.BringIntoView(new Rect(0, 0, panel.ActualWidth, Math.Min(panel.ActualHeight, 400)));
                await WindowCapture.SettleAsync();
                var buttons = UiProbe.Descendants<Button>(panel).Where(x => x.IsVisible).ToArray();
                Check(buttons.Length >= 3, "Native panel action buttons are missing");
                foreach (var button in buttons)
                {
                    var bounds = button.TransformToAncestor(panel).TransformBounds(new Rect(button.RenderSize));
                    Check(bounds.Left >= -1 && bounds.Right <= panel.ActualWidth + 1,
                        $"Native button '{button.Content}' is horizontally clipped: {bounds}, panel width {panel.ActualWidth}");
                }
                Record(panel.Content is CursorWorkspaceViewModel ? "workspace-cursor" : "workspace-mirasim");
            }
        });
        await Step("Quotas: refresh and local routing simulation", async () =>
        {
            await Navigate(ScreenId.Quotas); await Click("RefreshAllCommand");
            foreach (var check in Visible<CheckBox>().ToArray())
                ((IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle)).Toggle();
            await Click("SimulateRoutingCommand"); Record("quotas-simulated");
        });
        await Step("Health: refresh and select scopes", async () =>
        {
            await Navigate(ScreenId.HealthCenter); await Click("RefreshCommand");
            foreach (var list in Visible<ListBox>().Where(x => x.Items.Count > 0 && Binding(x, ItemsControl.ItemsSourceProperty) == "Scopes")) list.SelectedIndex = 0;
            Record("health-selected");
        });
        await Step("Settings: local CLI rediscovery", async () =>
        { await Navigate(ScreenId.SettingsDiagnostics); await Click("CliStatus.RefreshCommand"); await Task.Delay(1800); });
        await Onboarding();
        await ConsoleWalk();
        await Step("Activity center: open and refresh", async () =>
        { await Click("OpenActivityCenterButton"); await Click("RefreshButton"); Record("activity"); });
        await Step("Activity: synthetic fixture paging and diff viewer", async () =>
        {
            var activity = services.GetRequiredService<IActivityCenterService>();
            for (var i = 0; i < 420; i++)
                activity.Append(new ActivityEvent("ui-fixture-" + i, DateTimeOffset.UtcNow.AddSeconds(i),
                    ActivityEventKind.System, ActivityRoleNames.System, ActivityEventState.Completed,
                    ActivityEventSource.Synthetic, "Synthetic UI acceptance " + i, "Local test fixture; no model execution.",
                    diffText: "--- a/example.txt\n+++ b/example.txt\n@@ -1 +1 @@\n-before\n+after\n", artifactName: "example.txt"));
            await Task.Delay(1000); await Click("RefreshButton");
            await Click("NextPageButton"); await Click("FirstPageButton");
            await Click("NextPageButton"); await Click("PreviousPageButton");
            var list = Visible<ListBox>().Single(x => x.Name == "EventsList");
            Check(list.Items.Count > 0, "Fixture events missing"); list.SelectedIndex = 0;
            await WindowCapture.SettleAsync(); await Click("OpenDiffButton"); Record("activity-diff");
            Check(services.GetRequiredService<ActivityCenterViewModel>().IsDiffViewerOpen, "Diff pane did not open");
            await Click("CloseDiffViewerButton");
        });
        await Step("Activity center: all filter toggles and ranges", async () =>
        {
            var root = UiProbe.Require<FrameworkElement>(window, "ActivityCenterOverlay");
            foreach (var check in UiProbe.Descendants<CheckBox>(root).Where(x => x.IsVisible && x.IsEnabled).ToArray())
            {
                var peer = (IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle);
                peer.Toggle(); await WindowCapture.SettleAsync(); peer.Toggle();
            }
            foreach (var combo in UiProbe.Descendants<ComboBox>(root).Where(x => x.IsVisible).ToArray())
            { for (var i = 0; i < combo.Items.Count; i++) { combo.SelectedIndex = i; await WindowCapture.SettleAsync(); } }
            Input("SearchBox", "zz-no-events-123"); await WindowCapture.SettleAsync();
            await Click("ResetFiltersButton"); Record("activity-reset");
        });
        await Step("Activity center: close returns to workspace", async () =>
        { await Click("CloseActivityCenterButton"); Check(!shell.IsActivityCenterOpen, "Activity center remains open"); });
        foreach (var theme in new[] { "Light", "Dark" })
        {
            await Click(theme + "ThemeButton"); window.Width = 900; window.Height = 560;
            await Step(theme + " narrow: palette opens and closes", async () =>
            { await Click("OpenCommandPaletteButton"); Record(theme + "-narrow-palette"); UiProbe.InvokeShortcut(window, Key.Escape, ModifierKeys.None); });
            await Step(theme + " narrow: activity scroll and close", async () =>
            { await Click("OpenActivityCenterButton"); Record(theme + "-narrow-activity"); await Click("CloseActivityCenterButton"); });
            await Click("OpenWorkflowConsoleButton");
            foreach (var mode in new[] { "Templates", "Roles", "Documents", "Schema", "Activity", "Inspector" })
                await Step(theme + " narrow console " + mode, async () =>
                {
                    await Click("Console" + mode + "Button");
                    var panel = Visible<FrameworkElement>().First(x => x.Name ==
                        (mode is "Templates" or "Roles" or "Documents" ? "ConsolidatedStudioView" : "ConsolidatedMonitorView"));
                    Check(panel.ActualHeight >= 300, "Console editor collapsed below usable height");
                    panel.BringIntoView(new Rect(0, 0, 200, 250)); await WindowCapture.SettleAsync();
                    Record(theme + "-narrow-console-" + mode);
                });
            await Click("CloseWorkflowConsoleButton");
            await Step(theme + " narrow onboarding", async () =>
            {
                await Click("OpenOnboardingButton");
                window.UpdateLayout();
                var card = UiProbe.Require<Border>(window, "OnboardingCardHost");
                var client = (FrameworkElement)window.Content;
                var bounds = card.TransformToAncestor(client).TransformBounds(new Rect(card.RenderSize));
                Check(card.ActualWidth > 0 && card.ActualHeight > 0, "Onboarding card has not been laid out");
                Check(bounds.Left >= 0 && bounds.Top >= 0
                    && bounds.Right <= client.ActualWidth && bounds.Bottom <= client.ActualHeight,
                    "Onboarding card extends outside the narrow window");
                Record(theme + "-narrow-onboarding");
                await Click("DismissOnboardingButton");
            });
        }
        File.WriteAllText(Path.Combine(options.RunRoot, "full-ui-controls.json"), JsonSerializer.Serialize(new { invoked, inventory }, new JsonSerializerOptions { WriteIndented = true }));
        report.Facts["fullUi.states"] = inventory.Count.ToString();
        report.Facts["fullUi.distinctInvoked"] = invoked.Count.ToString();
    }
    private async Task Providers()
    {
        await Navigate(ScreenId.ProvidersAccounts);
        await Step("Provider editor: invalid URL blocks save", async () =>
        {
            await Click("NewProviderCommand"); Input("EditingProviderId", "ui-acceptance");
            Input("EditingDisplayName", "Visual acceptance local provider"); Input("EditingBaseUrl", "invalid-url");
            await Click("ConfirmAndSaveCommand");
            Check(!((ProvidersAccountsViewModel)Find("NewProviderCommand").DataContext).IsUrlValid, "Invalid URL accepted");
        });
        await Step("Provider: preview diff and cancel", async () =>
        { Input("EditingBaseUrl", "http://127.0.0.1:1/v1"); await Click("PreviewConfigCommand"); Record("provider-diff"); await Click("PreviewDialog.CancelCommand"); });
        await Step("Provider: confirm save and export diagnostics", async () =>
        {
            await Click("PreviewConfigCommand"); await Click("PreviewDialog.ConfirmCommand");
            var vm = (ProvidersAccountsViewModel)Find("NewProviderCommand").DataContext;
            await WaitForProviderIdle(vm);
            Check(vm.Providers.Any(x => x.Id == "ui-acceptance"), "Provider was not persisted");
            Check(vm.CanConfigureProvider, "Provider editor remained disabled after save");
            await Click("ExportDiagnosticsCommand");
            await WaitForProviderIdle(vm);
            Check(!string.IsNullOrWhiteSpace(vm.LastExportedDiagnosticJson), "Diagnostics were not exported");
        });
        await Step("Provider: add and remove local approval rule", async () =>
        {
            var projects = Visible<ComboBox>().Single(x => Binding(x, ItemsControl.ItemsSourceProperty) == "RuleProjects");
            projects.SelectedIndex = 0;
            await Click("AddApprovalRuleCommand");
            var list = Visible<ListBox>().First(x => Binding(x, ItemsControl.ItemsSourceProperty) == "ApprovalRules");
            Check(list.Items.Count > 0, "Rule not created"); list.SelectedIndex = list.Items.Count - 1;
            await Click("RemoveApprovalRuleCommand");
        });
        await Step("Provider: delete temporary profile", async () =>
        {
            var list = Visible<ListBox>().First(x => Binding(x, ItemsControl.ItemsSourceProperty) == "Providers");
            list.SelectedItem = list.Items.Cast<ProviderItemViewModel>().Single(x => x.Id == "ui-acceptance");
            await Click("DeleteProviderCommand");
            await WaitForProviderIdle((ProvidersAccountsViewModel)Find("NewProviderCommand").DataContext);
            Check(!list.Items.Cast<ProviderItemViewModel>().Any(x => x.Id == "ui-acceptance"), "Provider still exists");
        });
    }
    private static async Task WaitForProviderIdle(ProvidersAccountsViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (vm.IsBusy) throw new TimeoutException("Provider operation did not finish within ten seconds");
        await WindowCapture.SettleAsync();
    }
    private async Task Onboarding()
    {
        await Click("OpenOnboardingButton");
        await Step("Native folder picker opens and cancels without changing path", async () =>
        {
            var box = Visible<TextBox>().Single(x => x.Name == "WorkspacePathBox");
            var before = box.Text;
            var native = Task.Run(() => NativeDialogProbe.CaptureAndCancelAsync(Path.Combine(options.ScreenshotDirectory, "native-folder-picker.png")));
            await Click("BrowseWorkspaceButton"); await native; await WindowCapture.SettleAsync();
            Check(box.Text == before, "Cancel changed the workspace path");
        });
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            await Step($"Onboarding page {index + 1}", async () =>
            {
                Record($"onboarding-{index + 1}");
                if (index == 0)
                { Input("WorkspacePathBox", Path.Combine(options.RunRoot, "workspace")); await Click("ConfirmWorkspaceButton"); }
                if (index == 1) await Click("RefreshCliOnboardingButton");
                if (index == 2) await Click("RefreshCatalogButton");
            });
            if (i < 3) await Step($"Onboarding next from {i + 1}", () => Click("NextStepButton"));
        }
        await Step("Onboarding safe simulation start/stop", async () =>
        { await Click("StartSafeSimulationButton"); await Task.Delay(450); await Click("StopSafeSimulationButton"); });
        await Step("Onboarding back and next", async () =>
        { await Click("BackStepButton"); await Click("NextStepButton"); });
        await Step("Onboarding complete", () => Click("CompleteOnboardingButton"));
        if (shell.IsOnboardingOpen) await Click("DismissOnboardingButton");
    }
    private async Task ConsoleWalk()
    {
        await Click("OpenWorkflowConsoleButton");
        foreach (var mode in new[] { "Templates", "Roles", "Documents", "Schema", "Activity", "Inspector" })
            await Step("Console " + mode, async () => { await Click("Console" + mode + "Button"); Record("console-" + mode); });
        await Step("Studio: clone template, add/remove node, validate and save version", async () =>
        {
            await Click("ConsoleTemplatesButton");
            Input("NewTemplateName", "Visual acceptance clone"); await Click("CloneTemplateCommand");
            await Click("AddNodeCommand"); await Click("RemoveNodeCommand");
            await Click("ValidateTemplateCommand"); await Click("SaveTemplateCommand");
            await Click("CreateTemplateVersionCommand"); Record("studio-edited");
        });
        await Step("Documents: generate local draft, edit and preview", async () =>
        {
            await Click("ConsoleDocumentsButton"); await Click("GenerateDraftCommand");
            Input("DraftTitle", "Visual acceptance draft"); await Click("UpdateDraftCommand");
            await Click("PreviewDraftCommand"); await Click("RefreshGateCommand"); Record("document-preview");
        });
        await Step("Console export selected workflow", async () =>
        {
            var directory = Path.Combine(options.RunRoot, "export");
            Input("Library.QuickExportDirectory", directory); await Click("ConsoleQuickExportButton");
            Check(Directory.Exists(directory) && Directory.GetFiles(directory, "*.zip", SearchOption.AllDirectories).Length > 0, "Export file missing");
        });
        await Step("Console invalid import reports error", async () =>
        { Input("Library.QuickImportPath", Path.Combine(options.RunRoot, "missing.json")); await Click("ConsoleQuickImportButton"); });
        await Step("Console reimport exported archive", async () =>
        { Input("Library.QuickImportPath", Directory.GetFiles(Path.Combine(options.RunRoot, "export"), "*.zip", SearchOption.AllDirectories).First()); await Click("ConsoleQuickImportButton"); });
        await Step("Console close", () => Click("CloseWorkflowConsoleButton"));
        await Step("Workflow adaptation dialog opens and cancels", async () =>
        { await Navigate(ScreenId.Workflows); await Click("LibraryAdaptButton"); Record("adaptation"); await Click("AdaptationDialog.CancelCommand"); });
    }
}
