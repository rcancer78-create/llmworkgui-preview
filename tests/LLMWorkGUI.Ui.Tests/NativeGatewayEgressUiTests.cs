using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LLMGateway.Core;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>Actual common WPF dialog + production composer/SQLite/authority. Native dispatch is captured,
/// and the real authority is consumed by the receiver; no native store or network is constructed.</summary>
public sealed class NativeGatewayEgressUiTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void EveryDisplayedFragmentMustBeConfirmedBeforeComposerDispatch(bool approveAll, bool expired, bool hostStopping)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var root = Directory.CreateTempSubdirectory("native-egress-ui-");
            ServiceProvider? provider = null;
            try
            {
                var services = new ServiceCollection(); services.AddApplication(); services.AddInfrastructure(root.FullName); services.AddAppUi();
                using var lifetime = new ConsentStoppingLifetime();
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(lifetime);
                var presenter = new Presenter(approveAll, expired, hostStopping ? lifetime : null);
                var receiver = new Receiver(presenter);
                services.AddSingleton<IEgressPreviewPresenter>(presenter);
                if (expired) services.AddSingleton<TimeProvider>(new PastClock());
                services.AddSingleton<INativeGatewayTurnService>(receiver);
                services.AddSingleton<ILlmGateway>(_ => throw new InvalidOperationException("Native construction must remain lazy"));
                provider = services.BuildServiceProvider();
                var factory = provider.GetRequiredService<ISqliteConnectionFactory>();
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync());
                var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace"));
                Pump(provider.GetRequiredService<IProjectRepository>().UpsertAsync(new Project("project", "Review project", workspace.FullName, null, false, true, null, null, DataClassification.Restricted)));
                var profile = GrokBotRestrictions.ProviderProfileId;
                var catalog = new GatewayCatalogMapper(new SensitiveDataFilter()).Map(
                    [new ProviderInfo(ProviderKind.GrokBot, "Grok Bot", "node", true, null,
                        new(false, "Not reported", MultiAccountSupport.Single, "Single", null, null, false, 64000))],
                    [new AccountInfo("grokbot-default", "Review account", ProviderKind.GrokBot, true, true,
                        AccountAvailability.Unknown, null, null, null, AccountAuthMode.NativeLogin, null, null, null, null, null)],
                    [new GatewayModel("grokbot/grokbot-default/grok-bot", "grok-bot", "Review model", ProviderKind.GrokBot, "grokbot-default", true)],
                    [], DateTimeOffset.UtcNow);
                var account = Assert.Single(catalog.Accounts).Id;
                var model = Assert.Single(catalog.Models).Id;
                Pump(Seed(factory, profile, account, model));
                receiver.Authority = provider.GetRequiredService<IEgressApprovalService>();
                var vm = provider.GetRequiredService<NativeGatewayWorkspaceViewModel>();
                Pump(vm.RefreshAsync()); vm.SelectedRoute = Assert.Single(vm.Routes);
                vm.PromptInput = "api_key=synthetic-ui-canary\nReview this method.";
                Assert.True(vm.CanSend); Pump(vm.SendAsync());
                Assert.Null(presenter.Failure); Assert.True(presenter.Opened);
                Assert.Equal(approveAll && !expired && !hostStopping ? 1 : 0, receiver.Calls);
                Assert.False(vm.IsBusy);
                Assert.False(File.Exists(Path.Combine(root.FullName, "llmgateway", "accounts.json")));
                if (approveAll && !expired && !hostStopping)
                {
                    Assert.NotNull(receiver.Request!.EgressPreviewId); Assert.NotNull(receiver.Payload);
                    Assert.DoesNotContain("synthetic-ui-canary", string.Concat(receiver.Payload.Fragments.Select(f => f.Content)));
                    Assert.Contains("review answer", vm.Response);
                }
                else
                {
                    Assert.Contains("не отправлялся", vm.Message);
                    Assert.Contains("Маршрут route (LLMGateway, grok-bot)", vm.Message);
                    Assert.DoesNotContain("Повтор небезопасен", vm.Message);
                    Assert.DoesNotContain("могла получить", vm.Message);
                    Assert.DoesNotContain("мог быть доставлен", vm.Message);
                    Assert.DoesNotContain("local-session", vm.Message);
                    Assert.Equal(0, receiver.Calls);
                }
                Assert.Throws<EgressApprovalException>(() => receiver.Authority.ApproveFragment(presenter.Preview!.Id,
                    presenter.Preview.Fragments[0].Id, presenter.Preview.Fragments[0].ContentSha256));
            }
            finally { provider?.Dispose(); SqliteConnection.ClearAllPools(); root.Delete(true); }
        });
    }

    private static async Task Seed(ISqliteConnectionFactory factory, string profile, string account, string model)
    {
        await using var connection = await factory.OpenConnectionAsync(); await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProviderProfiles (Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
                VALUES ($profile,'Grok Bot','NativeGateway','Restricted',1,$now,$now);
            INSERT INTO Accounts (Id,ProviderProfileId,ProviderNativeId,DisplayName,AuthState,Health,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
                VALUES ($account,$profile,'grokbot-default','Review account','Valid','Healthy',1,$now,$now);
            INSERT INTO Models (Id,Backend,ProviderProfileId,ProviderModelId,DisplayName,CapabilityState,Provenance,IsEnabled,Health,DiscoveredAtUtc)
                VALUES ($model,'NativeGateway',$profile,'grok-bot','Review model','Supported','PluginReported',1,'Healthy',$now);
            INSERT INTO Routes (Id,Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,IsEnabled,Health,ManualPriority,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('route','NativeGateway',$profile,$account,$model,'Restricted',1,'Healthy',0,$now,$now);
            """;
        command.Parameters.AddWithValue("$profile", profile); command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$model", model); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T typed) yield return typed;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    private sealed class PastClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddMinutes(-6);
    }
    private sealed class Presenter(bool approveAll, bool expired, ConsentStoppingLifetime? stopping) : IEgressPreviewPresenter
    {
        public EgressPreview? Preview; public Exception? Failure; public bool Opened;
        public Task<IReadOnlyList<string>?> ConfirmAsync(EgressPreview preview, string destination, CancellationToken token)
        {
            Preview = preview;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Window? dialog = null;
                try
                {
                    dialog = System.Windows.Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Подтверждение передачи текста");
                    dialog.Left = -10000; dialog.Top = -10000; dialog.UpdateLayout(); Opened = true;
                    var text = string.Concat(Children<TextBox>(dialog).Select(t => t.Text));
                    Assert.DoesNotContain("synthetic-ui-canary", text); Assert.Contains("Review this method.", text);
                    var checks = Children<CheckBox>(dialog).ToArray(); Assert.Equal(2, checks.Length);
                    var send = Children<Button>(dialog).Single(b => AutomationProperties.GetName(b) == "Подтвердить передачу всех фрагментов");
                    Assert.False(send.IsEnabled); checks[0].IsChecked = true; Assert.False(send.IsEnabled);
                    if (stopping is not null)
                    {
                        stopping.StopApplication();
                        Assert.True(token.IsCancellationRequested);
                        return; // the real dialog observes the linked stopping token and closes
                    }
                    if (expired) { checks[1].IsChecked = true; Assert.False(send.IsEnabled); return; } // real timer closes it
                    if (approveAll)
                    {
                        checks[1].IsChecked = true; Assert.True(send.IsEnabled);
                        checks[0].IsChecked = false; Assert.False(send.IsEnabled); checks[0].IsChecked = true;
                        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else dialog.Close();
                }
                catch (Exception error) { Failure = error; dialog?.Close(); }
            }));
            return new EgressPreviewDialogPresenter().ConfirmAsync(preview, destination, token);
        }
    }
    private sealed class ConsentStoppingLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
    private sealed class Receiver(Presenter presenter) : INativeGatewayTurnService
    {
        public IEgressApprovalService Authority = null!; public int Calls; public NativeGatewayTurnRequest? Request;
        public ApprovedEgressPayload? Payload;
        public async Task<NativeGatewayTurnResult> ExecuteAsync(NativeGatewayTurnRequest request, CancellationToken token = default)
        {
            Calls++; Request = request;
            var preview = presenter.Preview!;
            Payload = await Authority.ConsumeAsync(request.EgressPreviewId!.Value, preview.Target,
                [new("instructions", "Инструкция провайдера", GrokBotRestrictions.ReviewInstructions, DataClassification.PublicSource),
                    new("prompt", "Текст задания", request.Prompt, DataClassification.Restricted)], token);
            return new("session", "execution", ExecutionState.Succeeded, ExecutionFailureReason.None, "review answer", false);
        }
    }
}
