using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Trait("Category", "VisualUi")]
public sealed class ProvidersAccountsUiVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    [Fact]
    public void HeaderEditor_ExpandsWithWorkingRemoveCommandAndMaskedSecret()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            new LLMWorkGUI.App.Services.ThemeResourceApplier().ApplyTheme(LLMWorkGUI.App.Services.AppTheme.Dark);
            using var provider = UiTestHost.CreateProvider();
            var main = provider.GetRequiredService<MainWindowViewModel>();
            var vm = provider.GetRequiredService<ProvidersAccountsViewModel>();
            main.NavigateCommand.Execute(ScreenId.ProvidersAccounts);
            PopulateSampleData(vm);
            vm.EditingHeaders.Add(new("X-Region", "east"));
            vm.EditingHeaders.Add(new("X-Token", "synthetic-header-canary", true));
            var window = new MainWindow(main);
            try
            {
                Layout(window);
                var root = (DependencyObject)window.Content;
                var expander = Assert.Single(FindVisualDescendants<Expander>(root).Where(e => Equals(e.Header, "HTTP-заголовки")));
                expander.IsExpanded = true;
                Layout(window);
                Assert.Equal(2, FindVisualDescendants<ProviderHeaderValueBox>(expander).Count());
                Assert.DoesNotContain(FindVisualDescendants<TextBox>(expander), b => b.Text.Contains("synthetic-header-canary"));
                Assert.Equal("synthetic-header-canary", Assert.Single(FindVisualDescendants<PasswordBox>(expander)).Password);
                Directory.CreateDirectory(ScreenshotOutputDir);
                CaptureScreenshot(window, Path.Combine(ScreenshotOutputDir, "provider_headers_expanded.png"));
                var remove = FindVisualDescendants<Button>(expander).First(b => Equals(b.Content, "Удалить"));
                Assert.NotNull(remove.Command);
                remove.Command.Execute(remove.CommandParameter);
                Assert.Single(vm.EditingHeaders);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ApiKeyEditor_MasksValueAndClearsWithProviderForm()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            var main = provider.GetRequiredService<MainWindowViewModel>();
            var vm = provider.GetRequiredService<ProvidersAccountsViewModel>();
            main.NavigateCommand.Execute(ScreenId.ProvidersAccounts);
            PopulateSampleData(vm);
            var window = new MainWindow(main);
            try
            {
                Layout(window);
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                Assert.Contains(FindVisualDescendants<TextBox>(root), box => box.Text == vm.EditingBaseUrl);
                Assert.DoesNotContain(FindVisualDescendants<TextBox>(root),
                    box => box.Text.Contains("mock-secret-key-12345", StringComparison.Ordinal));
                var masked = Assert.Single(FindVisualDescendants<PasswordBox>(root));
                masked.Password = "synthetic-new-secret";
                Assert.Equal("synthetic-new-secret", vm.EditingApiKey);
                vm.NewProviderCommand.Execute(null);
                Assert.Empty(masked.Password);
                Assert.Empty(vm.EditingApiKey);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ProvidersAccountsScreen_RendersFullLayout_AndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            var mainWindowVm = provider.GetRequiredService<MainWindowViewModel>();
            var providersAccountsVm = provider.GetRequiredService<ProvidersAccountsViewModel>();

            mainWindowVm.NavigateCommand.Execute(ScreenId.ProvidersAccounts);
            PopulateSampleData(providersAccountsVm);
            Assert.Same(providersAccountsVm, mainWindowVm.CurrentScreen);

            var window = new MainWindow(mainWindowVm);
            Layout(window);

            Directory.CreateDirectory(ScreenshotOutputDir);
            var screenshotPath = Path.Combine(ScreenshotOutputDir, "providers_accounts_screen.png");
            CaptureScreenshot(window, screenshotPath);

            Assert.True(File.Exists(screenshotPath), $"Screenshot was not created at {screenshotPath}");
            Assert.True(new FileInfo(screenshotPath).Length > 1000, "Screenshot file is too small.");

            var textBlocks = FindVisualDescendants<TextBlock>((DependencyObject)window.Content)
                .Select(t => t.Text)
                .ToArray();

            Assert.Contains("Провайдеры и аккаунты", textBlocks);
            Assert.Contains("Mock Local OpenAI", textBlocks);
            Assert.Contains("Ссылка на секрет сохранена", textBlocks);
            Assert.Contains("Состояние секрета:", textBlocks);
            Assert.Contains(providersAccountsVm.ApiKeySecretStateText, textBlocks);
            Assert.DoesNotContain(providersAccountsVm.EditingApiKeySecretRef!, textBlocks);
            Assert.Contains("Плагины OpenCode", textBlocks);
            Assert.Contains("Операционные правила подтверждения (ТЗ §6.7)", textBlocks);
            Assert.Contains("Valid Local Loopback (HTTP)", textBlocks);
        });
    }

    [Fact]
    public void ProvidersAccountsScreen_RendersModalDiffOverlay_AndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            var mainWindowVm = provider.GetRequiredService<MainWindowViewModel>();
            var providersAccountsVm = provider.GetRequiredService<ProvidersAccountsViewModel>();

            mainWindowVm.NavigateCommand.Execute(ScreenId.ProvidersAccounts);
            PopulateSampleData(providersAccountsVm);

            var diffText =
                "--- opencode.jsonc (current)\n" +
                "+++ opencode.jsonc (preview)\n" +
                "@@ -1,5 +1,11 @@\n" +
                " {\n" +
                "   \"$schema\": \"https://opencode.ai/config.json\",\n" +
                "+  \"providers\": {\n" +
                "+    \"mock-openai\": {\n" +
                "+      \"backend\": \"opencode\",\n" +
                "+      \"baseUrl\": \"http://127.0.0.1:5000/v1\",\n" +
                "+      \"apiKey\": \"***REDACTED***\"\n" +
                "+    }\n" +
                "+  }\n" +
                " }";

            var redactedJson =
                "{\n" +
                "  \"$schema\": \"https://opencode.ai/config.json\",\n" +
                "  \"providers\": {\n" +
                "    \"mock-openai\": {\n" +
                "      \"backend\": \"opencode\",\n" +
                "      \"baseUrl\": \"http://127.0.0.1:5000/v1\",\n" +
                "      \"apiKey\": \"***REDACTED***\"\n" +
                "    }\n" +
                "  }\n" +
                "}";

            providersAccountsVm.PreviewDialog.Show(
                diffText,
                redactedJson,
                hasExistingConfig: true,
                hasChanges: true,
                onConfirmed: () => { });

            Assert.True(providersAccountsVm.PreviewDialog.IsVisible);

            var window = new MainWindow(mainWindowVm);
            Layout(window);

            Directory.CreateDirectory(ScreenshotOutputDir);
            var screenshotPath = Path.Combine(ScreenshotOutputDir, "providers_accounts_preview_modal.png");
            CaptureScreenshot(window, screenshotPath);

            Assert.True(File.Exists(screenshotPath), $"Screenshot was not created at {screenshotPath}");
            Assert.True(new FileInfo(screenshotPath).Length > 1000, "Screenshot file is too small.");

            var textBlocks = FindVisualDescendants<TextBlock>((DependencyObject)window.Content)
                .Select(t => t.Text)
                .ToArray();

            Assert.Contains("Предпросмотр конфигурации и diff OpenCode", textBlocks);
            Assert.Contains("Подтвердить и сохранить", textBlocks);

            var textBoxes = FindVisualDescendants<TextBox>((DependencyObject)window.Content)
                .Select(tb => tb.Text)
                .ToArray();

            Assert.Contains(textBoxes, t => t.Contains("***REDACTED***"));
        });
    }

    [Fact]
    public void ProvidersAccountsScreen_UrlValidation_ReflectsInVisualTree()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            var mainWindowVm = provider.GetRequiredService<MainWindowViewModel>();
            var providersAccountsVm = provider.GetRequiredService<ProvidersAccountsViewModel>();

            mainWindowVm.NavigateCommand.Execute(ScreenId.ProvidersAccounts);
            var window = new MainWindow(mainWindowVm);

            // 1. Loopback HTTP
            providersAccountsVm.EditingBaseUrl = "http://127.0.0.1:8080/v1";
            Layout(window);
            var textBlocks = FindVisualDescendants<TextBlock>((DependencyObject)window.Content).Select(t => t.Text).ToArray();
            Assert.Contains("Valid Local Loopback (HTTP)", textBlocks);

            // 2. Remote HTTPS
            providersAccountsVm.EditingBaseUrl = "https://api.openai.com/v1";
            Layout(window);
            textBlocks = FindVisualDescendants<TextBlock>((DependencyObject)window.Content).Select(t => t.Text).ToArray();
            Assert.Contains("Valid Secure Remote (HTTPS)", textBlocks);

            // 3. Insecure Remote HTTP
            providersAccountsVm.EditingBaseUrl = "http://api.openai.com/v1";
            Layout(window);
            textBlocks = FindVisualDescendants<TextBlock>((DependencyObject)window.Content).Select(t => t.Text).ToArray();
            Assert.Contains("Insecure Remote HTTP (Disallowed)", textBlocks);

            // 4. Invalid Format
            providersAccountsVm.EditingBaseUrl = "ftp://invalid-url";
            Layout(window);
            textBlocks = FindVisualDescendants<TextBlock>((DependencyObject)window.Content).Select(t => t.Text).ToArray();
            Assert.Contains("URL scheme must be http or https.", textBlocks);
        });
    }

    private static void PopulateSampleData(ProvidersAccountsViewModel vm)
    {
        var localProfile = new ProviderProfile(
            "mock-openai",
            "Mock Local OpenAI",
            BackendType.OpenCode,
            "http://127.0.0.1:5000/v1",
            null,
            DataClassification.PrivateSource,
            true);

        var remoteProfile = new ProviderProfile(
            "remote-openai",
            "Remote OpenAI Production",
            BackendType.OpenCode,
            "https://api.openai.com/v1",
            null,
            DataClassification.PrivateSource,
            true);

        var localItem = new ProviderItemViewModel(localProfile, "urn:llmworkgui:secret:00000000-0000-0000-0000-000000000001");
        var remoteItem = new ProviderItemViewModel(remoteProfile, "urn:llmworkgui:secret:00000000-0000-0000-0000-000000000002");

        vm.Providers.Add(localItem);
        vm.Providers.Add(remoteItem);
        vm.SelectedProvider = localItem;

        vm.EditingProviderId = "mock-openai";
        vm.EditingDisplayName = "Mock Local OpenAI";
        vm.EditingBaseUrl = "http://127.0.0.1:5000/v1";
        vm.EditingApiKey = "mock-secret-key-12345";
        vm.EditingApiKeySecretRef = "urn:llmworkgui:secret:00000000-0000-0000-0000-000000000001";
        vm.EditingIsEnabled = true;

        vm.InstalledPlugins.Add(new PluginItemViewModel(new PluginInfo("code-search", "1.0.0", true, "Fast semantic indexer")));
        vm.InstalledPlugins.Add(new PluginItemViewModel(new PluginInfo("git-review", "2.1.0", true, "Automated diff review")));

        vm.DiscoveredModels.Add(new ModelItemViewModel(new DiscoveredModelDetails(
            "gpt-4o",
            "gpt-4o",
            "Flagship multimodal model",
            128000,
            ModelCapabilityFlags.Vision | ModelCapabilityFlags.ToolCalling,
            Array.Empty<string>())));

        vm.DiscoveredModels.Add(new ModelItemViewModel(new DiscoveredModelDetails(
            "o1-preview",
            "o1-preview",
            "Reasoning model for complex workflows",
            128000,
            ModelCapabilityFlags.ReasoningVariants,
            new[] { "low", "medium", "high" })));

        vm.DiscoveredModels.Add(new ModelItemViewModel(new DiscoveredModelDetails(
            "mock-chat-model",
            "mock-chat-model",
            "Fast local mock model for integration testing",
            32000,
            ModelCapabilityFlags.ToolCalling,
            Array.Empty<string>())));

        var rule1 = new ApprovalRule(
            "rule-1",
            BackendType.OpenCode,
            localProfile.Id,
            "test-project",
            "*.cs",
            NormalizedApprovalKind.ReadFile,
            "read_file",
            null,
            "test-user",
            DateTimeOffset.UtcNow);

        var rule2 = new ApprovalRule(
            "rule-2",
            BackendType.OpenCode,
            localProfile.Id,
            "test-project",
            null,
            NormalizedApprovalKind.ShellCommand,
            "dotnet test",
            null,
            "test-user",
            DateTimeOffset.UtcNow);

        vm.ApprovalRules.Add(new ApprovalRuleItemViewModel(rule1));
        vm.ApprovalRules.Add(new ApprovalRuleItemViewModel(rule2));
    }

    private static void Layout(Window window)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        root.Measure(new Size(1400, 900));
        root.Arrange(new Rect(0, 0, 1400, 900));
        root.UpdateLayout();
    }

    private static void CaptureScreenshot(Window window, string outputPath)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var width = (int)Math.Max(1400, root.ActualWidth);
        var height = (int)Math.Max(900, root.ActualHeight);

        var renderBitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);

        renderBitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

        ScreenshotFile.Save(encoder, outputPath);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
