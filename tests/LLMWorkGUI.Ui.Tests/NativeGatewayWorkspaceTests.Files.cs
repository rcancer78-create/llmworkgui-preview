using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class NativeGatewayWorkspaceTests
{
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void LimitedProviderNoticeAndFileFieldsRenderInsideExistingComposer(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var fixture = Ready();
            fixture.Catalog.Options = [Route with
            {
                ProviderName = "Grok Bot · ограниченный, для ревью",
                Binding = Route.Binding with { ProviderProfileId = GrokBotRestrictions.ProviderProfileId }
            }];
            Pump(fixture.Vm.RefreshAsync()); fixture.Vm.SelectedRoute = fixture.Vm.Routes[0];
            var host = new ContentControl { Content = fixture.Vm, Margin = new Thickness(12) };
            var window = new Window { Width = 800, Height = 860, Content = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            try
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/LLMWorkGUI.App;component/Themes/{theme}.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
                window.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
                window.Show(); Settle(window);
                Assert.Single(Descendants<ComboBox>(host));
                Assert.Contains(Descendants<TextBlock>(host), x => x.Text == GrokBotRestrictions.Notice);
                var files = Assert.Single(Descendants<Expander>(host)); files.IsExpanded = true; Settle(window);
                Assert.Single(Descendants<TextBox>(host), x => AutomationProperties.GetName(x) == "Файл задания");
                Assert.Single(Descendants<TextBox>(host), x => AutomationProperties.GetName(x) == "Файл ответа");
                Capture(window, $"grokbot_existing_composer_{theme.ToString().ToLowerInvariant()}.png");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ExistingComposerLoadsTaskAutoSavesAnswerAndRetrySaveDoesNotSendAgain()
    {
        var root = Directory.CreateTempSubdirectory("gateway-review-ui-");
        try
        {
            StaTestRunner.Run(() =>
            {
                var fixture = Ready(); fixture.Vm.SelectedRoute = Route;
                var task = Path.Combine(root.FullName, "task.md"); var answer = Path.Combine(root.FullName, "answer.md");
                File.WriteAllText(task, "Проверь приложенный фрагмент кода");
                File.WriteAllText(answer, "existing");
                fixture.Vm.TaskFilePath = task; fixture.Vm.OutputFilePath = answer;
                Pump(fixture.Vm.LoadTaskFileAsync()); Assert.Contains("фрагмент", fixture.Vm.PromptInput);
                Pump(fixture.Vm.SendAsync()); Assert.Equal(1, fixture.Turns.Calls);
                Assert.Equal(File.ReadAllText(task), fixture.Turns.Request!.Prompt);
                Assert.Equal("existing", File.ReadAllText(answer));
                Assert.Contains("Повторная отправка", fixture.Vm.Message);
                Assert.Equal("Ответ модели", fixture.Vm.Response);
                fixture.Vm.OutputFilePath = Path.Combine(root.FullName, "new-answer.md");
                Pump(fixture.Vm.SaveResponseFileAsync());
                var saved = File.ReadAllText(fixture.Vm.OutputFilePath);
                Assert.Contains("Ответ модели", saved); Assert.Contains("SHA-256", saved);
                Assert.Contains("не является автоматически", saved); Assert.Equal(1, fixture.Turns.Calls);
            });
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void ProviderRestrictionLivesOnExistingSelectedRouteAndViewOnlyCannotSave()
    {
        StaTestRunner.Run(() =>
        {
            var fixture = Ready();
            fixture.Catalog.Options = [Route with { Binding = Route.Binding with { ProviderProfileId = GrokBotRestrictions.ProviderProfileId } }];
            Pump(fixture.Vm.RefreshAsync()); fixture.Vm.SelectedRoute = fixture.Vm.Routes[0];
            Assert.Contains("отключена", fixture.Vm.ProviderLimitations);
            fixture.Vm.PromptInput = "review"; Pump(fixture.Vm.SendAsync());
            fixture.Guard.ViewOnly = true;
            Assert.False(fixture.Vm.SaveResponseFileCommand.CanExecute(null));
            Assert.False(fixture.Vm.CanSend);
        });
    }
}
