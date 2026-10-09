using System.Collections.ObjectModel;
using System.Windows.Input;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LLMWorkGUI.Infrastructure.GrokBot;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.Hosting;
using System.Windows.Threading;

namespace LLMWorkGUI.App.ViewModels;

public sealed class NativeGatewayWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly INativeGatewayTurnService? _turns;
    private readonly INativeGatewayRouteCatalog? _catalog;
    private readonly OpenedProjectResolver? _projects;
    private readonly IApplicationInstanceGuard? _guard;
    private readonly SensitiveDataFilter _filter;
    private readonly INativeGatewayEgressService? _egress;
    private readonly IEgressPreviewPresenter? _egressPresenter;
    private readonly CancellationToken _applicationStopping;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private readonly Dispatcher? _dispatcher;
    private readonly object _operationGate = new();
    private readonly object _shutdownGate = new();
    private TaskCompletionSource? _operationCompletion;
    private int _disposed;
    private int _shutdownRequested;
    private Project? _project;
    private NativeGatewayRouteOption? _selectedRoute;
    private CancellationTokenSource? _cancellation;
    private bool _busy, _sending, _cancelRequested, _needsRefresh = true;
    private string _prompt = string.Empty, _response = string.Empty, _outcome = string.Empty;
    private string _message = "Обновите маршруты открытого проекта. Непроверенные аккаунты и модели недоступны для отправки.";
    private string _projectDisplay = "Проект не выбран", _executionDisplay = string.Empty;
    private string _taskFilePath = string.Empty, _outputFilePath = string.Empty;
    private string _sourceHash = string.Empty, _sentSourceHash = string.Empty;
    private string _responseHeader = string.Empty;

    public NativeGatewayWorkspaceViewModel(INativeGatewayTurnService? turns, INativeGatewayRouteCatalog? catalog,
        OpenedProjectResolver? projects, IApplicationInstanceGuard? guard, SensitiveDataFilter? filter = null,
        INativeGatewayEgressService? egress = null, IEgressPreviewPresenter? egressPresenter = null,
        IHostApplicationLifetime? lifetime = null)
    {
        _turns = turns; _catalog = catalog; _projects = projects; _guard = guard; _filter = filter ?? new();
        _egress = egress; _egressPresenter = egressPresenter;
        _applicationStopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => CanRefresh);
        SendCommand = new RelayCommand(() => _ = SendAsync(), () => CanSend);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
        LoadTaskFileCommand = new RelayCommand(() => _ = LoadTaskFileAsync(), () => IsIdle && !string.IsNullOrWhiteSpace(TaskFilePath));
        SaveResponseFileCommand = new RelayCommand(() => _ = SaveResponseFileAsync(), () => IsIdle && Response.Length > 0 && _guard is { IsPrimarySupervisor: true, IsViewOnly: false });
        ChooseTaskFileCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Текст задания|*.md;*.txt|Все файлы|*.*" };
            if (dialog.ShowDialog() == true) { TaskFilePath = dialog.FileName; _ = LoadTaskFileAsync(); }
        }, () => IsIdle);
        ChooseOutputFileCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Ответ Markdown|*.md|Текст|*.txt", FileName = "review-response.md", OverwritePrompt = false, CheckFileExists = false, Title = "Выберите новое имя файла ответа" };
            if (dialog.ShowDialog() == true) OutputFilePath = dialog.FileName;
        }, () => IsIdle);
        _stoppingRegistration = _applicationStopping.Register(BeginShutdown);
    }

    public ObservableCollection<NativeGatewayRouteOption> Routes { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LoadTaskFileCommand { get; }
    public ICommand SaveResponseFileCommand { get; }
    public ICommand ChooseTaskFileCommand { get; }
    public ICommand ChooseOutputFileCommand { get; }
    public string TaskFilePath { get => _taskFilePath; set { if (!_busy) SetProperty(ref _taskFilePath, value); } }
    public string OutputFilePath { get => _outputFilePath; set { if (!_busy) SetProperty(ref _outputFilePath, value); } }
    public string ProviderLimitations => SelectedRoute?.Limitations ?? string.Empty;
    public bool IsBusy => _busy;
    private bool IsStopping => Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _shutdownRequested) != 0 || _applicationStopping.IsCancellationRequested;
    public bool IsIdle => !_busy && !IsStopping;
    public bool CanRefresh => IsIdle && _catalog is not null && _projects is not null;
    public bool CanSend => IsIdle && !_needsRefresh && _turns is not null && _project is not null
        && _guard is { IsPrimarySupervisor: true, IsViewOnly: false }
        && SelectedRoute is not null && Routes.Contains(SelectedRoute) && !string.IsNullOrWhiteSpace(PromptInput)
        && (SelectedRoute.Binding.ProviderProfileId != GrokBotRestrictions.ProviderProfileId || _egress is not null && _egressPresenter is not null);
    public bool CanCancel => _busy && _sending && !IsStopping && !_cancelRequested && _cancellation is not null;
    public string ProjectDisplay { get => _projectDisplay; private set => SetProperty(ref _projectDisplay, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string Outcome { get => _outcome; private set => SetProperty(ref _outcome, value); }
    public string Response { get => _response; private set => SetProperty(ref _response, value); }
    public string ExecutionDisplay { get => _executionDisplay; private set => SetProperty(ref _executionDisplay, value); }
    public string AccessNotice => _guard is { IsPrimarySupervisor: true, IsViewOnly: false }
        ? "Отправка доступна только по подтверждённым маршрутам. Каждый запрос выполняется отдельно; продолжение диалога пока недоступно."
        : "Доступен только просмотр. Для отправки нужен основной экземпляр приложения.";

    public NativeGatewayRouteOption? SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            if (_busy || value is not null && !Routes.Contains(value)) return;
            if (SetProperty(ref _selectedRoute, value)) { OnPropertyChanged(nameof(ProviderLimitations)); NotifyCommands(); }
        }
    }
    public string PromptInput
    {
        get => _prompt;
        set { if (!_busy && SetProperty(ref _prompt, value)) { _sourceHash = string.Empty; NotifyCommands(); } }
    }

    public async Task LoadTaskFileAsync()
    {
        if (!IsIdle || string.IsNullOrWhiteSpace(TaskFilePath)) return;
        var path = TaskFilePath; if (!TryBeginOperation()) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping, _shutdown.Token);
        try
        {
            var loaded = await Task.Run(() => ReviewTaskFiles.ReadAsync(path), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_filter.ContainsSensitiveData(loaded.Text)) throw new IOException("В задании обнаружены секреты.");
            _prompt = loaded.Text; _sourceHash = loaded.Sha256; OnPropertyChanged(nameof(PromptInput));
            Message = "Задание загружено. Проверьте текст перед отправкой; материалы по упомянутым путям автоматически не загружаются.";
        }
        catch (Exception) { _prompt = string.Empty; _sourceHash = string.Empty; OnPropertyChanged(nameof(PromptInput)); Message = "Не удалось загрузить задание: нужен непустой файл UTF-8 до 200 KB без секретов и ссылок."; }
        finally { SetBusy(false); }
    }

    public async Task SaveResponseFileAsync()
    {
        if (!IsIdle || Response.Length == 0 || _guard is not { IsPrimarySupervisor: true, IsViewOnly: false }) return;
        if (!TryBeginOperation()) return;
        try { await SaveResponseCoreAsync(); }
        finally { SetBusy(false); }
    }

    private async Task SaveResponseCoreAsync()
    {
        try
        {
            _guard!.EnsureSupervisorPermitted();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping, _shutdown.Token);
            await Task.Run(() => ReviewTaskFiles.SaveAsync(OutputFilePath, _responseHeader + Response), cancellation.Token);
            Message = "Ответ записан в файл.";
        }
        catch (Exception) { Message = "Ответ получен, но файл не записан. Выберите новое имя в существующем каталоге и нажмите «Сохранить ответ». Повторная отправка модели не требуется."; }
    }

    public async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        var previous = SelectedRoute;
        if (!TryBeginOperation()) return;
        _needsRefresh = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping, _shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var snapshot = await Task.Run(async () =>
            {
                var project = await _projects!.ResolveAsync(null, timeout.Token).ConfigureAwait(false);
                var routes = project is null ? [] : await _catalog!.ListAsync(project.Id, project.RootPath, timeout.Token).ConfigureAwait(false);
                return (project, routes);
            }, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            _project = snapshot.project;
            Routes.Clear(); foreach (var route in snapshot.routes) Routes.Add(route);
            _selectedRoute = Routes.FirstOrDefault(r => previous is not null && r.Id == previous.Id && r.Binding == previous.Binding);
            OnPropertyChanged(nameof(SelectedRoute));
            OnPropertyChanged(nameof(ProviderLimitations));
            ProjectDisplay = _project is null ? "Проект не выбран" : _filter.Redact(_project.DisplayName);
            Message = _project is null ? "Откройте проект в приложении, затем обновите маршруты."
                : Routes.Count == 0 ? "Нет доступных маршрутов LLMGateway. Проверьте настройки провайдеров, авторизацию, модели и занятость проекта."
                : "Выберите маршрут для нового запроса.";
            _needsRefresh = false;
        }
        catch (Exception)
        {
            _project = null; Routes.Clear(); _selectedRoute = null;
            OnPropertyChanged(nameof(SelectedRoute)); ProjectDisplay = "Проект не выбран";
            OnPropertyChanged(nameof(ProviderLimitations));
            Message = "Не удалось обновить маршруты LLMGateway. Отправка заблокирована; повторите обновление.";
        }
        finally { SetBusy(false); }
    }

    public async Task SendAsync()
    {
        if (!CanSend) return;
        var project = _project!; var route = SelectedRoute!; var prompt = PromptInput;
        if (!TryBeginOperation()) return;
        _sentSourceHash = _sourceHash;
        _sending = true; _cancelRequested = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping, _shutdown.Token); _cancellation = cancellation;
        NotifyCommands();
        Message = DescribeRunningRequest(route); Outcome = string.Empty; Response = string.Empty; ExecutionDisplay = string.Empty;
        Guid? previewId = null;
        string? approvedWireHash = null;
        var callStarted = false;
        try
        {
            var current = await Task.Run(() => _projects!.ResolveAsync(null, cancellation.Token), cancellation.Token);
            if (current is null || current.Id != project.Id
                || !string.Equals(ProjectLock.CanonicalizeRoot(current.RootPath), ProjectLock.CanonicalizeRoot(project.RootPath), StringComparison.OrdinalIgnoreCase))
            {
                _needsRefresh = true;
                Message = DescribeUnsentProjectChange(route);
                return;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            _guard!.EnsureSupervisorPermitted();
            var request = new NativeGatewayTurnRequest(project.Id, project.RootPath, route.Id, Guid.NewGuid().ToString("D"), prompt)
                { ExpectedBinding = route.Binding };
            if (route.Binding.ProviderProfileId == GrokBotRestrictions.ProviderProfileId)
            {
                var preview = await Task.Run(() => _egress!.PrepareAsync(request, cancellation.Token), cancellation.Token);
                previewId = preview.Id;
                Message = "Подтвердите каждый фрагмент передаваемого текста.";
                var confirmed = await _egressPresenter!.ConfirmAsync(preview, route.Display, cancellation.Token);
                if (cancellation.IsCancellationRequested)
                { Message = DescribeUnsentTransfer(route); return; }
                if (confirmed is null || confirmed.Count != preview.Fragments.Count || confirmed.Distinct().Count() != confirmed.Count
                    || preview.Fragments.Any(f => !confirmed.Contains(f.Id)))
                { Message = DescribeUnsentTransfer(route); return; }
                var opened = await Task.Run(() => _projects!.ResolveAsync(null, cancellation.Token), cancellation.Token);
                if (opened is null || opened.Id != project.Id || !string.Equals(ProjectLock.CanonicalizeRoot(opened.RootPath),
                    ProjectLock.CanonicalizeRoot(project.RootPath), StringComparison.OrdinalIgnoreCase))
                { _needsRefresh = true; Message = DescribeUnsentProjectChange(route); return; }
                _guard.EnsureSupervisorPermitted();
                cancellation.Token.ThrowIfCancellationRequested();
                foreach (var fragment in preview.Fragments) _egress!.ApproveFragment(preview.Id, fragment.Id, fragment.ContentSha256);
                approvedWireHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(preview.Fragments.Select(f => f.Content)))));
                request = request with { EgressPreviewId = preview.Id };
                Message = DescribeRunningRequest(route);
            }
            callStarted = true;
            var result = await Task.Run(() => _turns!.ExecuteAsync(request, cancellation.Token), cancellation.Token);
            ExecutionDisplay = _filter.Redact($"Локальная сессия: {result.SessionId} · Выполнение: {result.ExecutionId}");
            var uncertain = result.RequiresReconciliation || result.State is not
                (ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.Cancelled or ExecutionState.TimedOut);
            _needsRefresh = uncertain || result.State != ExecutionState.Succeeded;
            Outcome = uncertain ? "Исход LLMGateway не подтверждён. Маршрут " + route.Id
                    + " (LLMGateway, " + route.Binding.NativeModelId
                    + "). Запрос мог быть отправлен. Итоговый результат неизвестен. Повтор небезопасен. Блокировка проекта сохранена."
                : result.State switch
                {
                    ExecutionState.Succeeded => "Ответ получен. Маршрут " + route.Id
                        + " (LLMGateway, " + route.Binding.NativeModelId
                        + "). Нативная идентичность ответа не подтверждена.",
                    ExecutionState.Cancelled => "Запрос LLMGateway отменён. Маршрут " + route.Id
                        + " (LLMGateway, " + route.Binding.NativeModelId + "). Доставка неизвестна.",
                    ExecutionState.TimedOut => "Время ожидания LLMGateway истекло. Маршрут " + route.Id
                        + " (LLMGateway, " + route.Binding.NativeModelId + "). Доставка неизвестна.",
                    _ => "Запрос LLMGateway завершился отказом. Маршрут " + route.Id
                        + " (LLMGateway, " + route.Binding.NativeModelId
                        + "). Доставка неизвестна. Повтор небезопасен."
                };
            if (uncertain && route.Binding.ProviderProfileId == GrokBotRestrictions.ProviderProfileId)
                Outcome += " Очистка временного агента не подтверждена; проверьте Grok Bot.";
            if (!uncertain && result.State == ExecutionState.Succeeded) Response = _filter.Redact(result.Content ?? string.Empty);
            Message = uncertain || result.State is ExecutionState.Failed or ExecutionState.Cancelled or ExecutionState.TimedOut
                ? Outcome
                : _needsRefresh ? "Перед новой отправкой обновите маршруты." : "Запрос завершён.";
            if (!uncertain && result.State == ExecutionState.Succeeded && Response.Length > 0)
            {
                _responseHeader = $"# Ответ на задание\n\nПровайдер: {_filter.Redact(route.ProviderName)}\n\n{ExecutionDisplay}\n\nИдентичность модели не подтверждена; ответ не является автоматически принятым вердиктом workflow.\n\n"
                    + (_sentSourceHash.Length > 0 ? $"SHA-256 исходного задания: {_sentSourceHash}\n\n" : string.Empty)
                    + $"SHA-256 исходного текста задания в редакторе: {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))}\n\n"
                    + (approvedWireHash is null ? string.Empty : $"SHA-256 подтверждённого полного передаваемого текста: {approvedWireHash}\n\n")
                    + (route.Limitations.Length > 0 ? route.Limitations + "\n\n" : string.Empty) + "---\n\n";
                if (!string.IsNullOrWhiteSpace(OutputFilePath)) await SaveResponseCoreAsync();
            }
        }
        catch (OperationCanceledException)
        {
            _needsRefresh = true;
            var identity = " Маршрут " + route.Id + " (LLMGateway, " + route.Binding.NativeModelId + ").";
            Message = callStarted
                ? "Отмена LLMGateway не подтверждена." + identity + " Доставка неизвестна. Повтор небезопасен."
                : "Отмена LLMGateway не начата." + identity + " Запрос не доставлялся.";
        }
        catch (Exception)
        {
            _needsRefresh = true;
            var identity = " Маршрут " + route.Id + " (LLMGateway, " + route.Binding.NativeModelId + ").";
            Message = callStarted
                ? "Результат запроса LLMGateway не подтверждён." + identity
                    + " Запрос мог быть доставлен. Повтор небезопасен. Состояние здоровья не изменялось."
                : "Отправка LLMGateway не начата." + identity + " Запрос не доставлялся.";
        }
        finally { if (previewId is { } id) _egress?.Revoke(id); _cancellation = null; _sending = false; SetBusy(false); }
    }

    private static string DescribeRunningRequest(NativeGatewayRouteOption route) =>
        "Выполняется запрос LLMGateway. Маршрут " + route.Id + " (LLMGateway, " + route.Binding.NativeModelId + ").";

    private static string DescribeUnsentProjectChange(NativeGatewayRouteOption route) =>
        "Открытый проект изменился. Маршрут " + route.Id + " (LLMGateway, " + route.Binding.NativeModelId
        + "). Запрос не доставлялся. Список маршрутов нужно прочитать заново.";

    private static string DescribeUnsentTransfer(NativeGatewayRouteOption route) =>
        "Передача отменена. Маршрут " + route.Id + " (LLMGateway, " + route.Binding.NativeModelId
        + "). Запрос не отправлялся.";

    public void Cancel()
    {
        if (!CanCancel) return;
        _cancelRequested = true;
        var route = SelectedRoute;
        var identity = route is null
            ? ""
            : " Маршрут " + route.Id + " (LLMGateway, " + route.Binding.NativeModelId + ").";
        Message = "Отмена запрошена." + identity
            + " Запрос мог быть отправлен. Доставка неизвестна. Повтор небезопасен. Остановка нативного выполнения ещё не подтверждена.";
        NotifyCommands(); _cancellation!.Cancel();
    }

    private void SetBusy(bool value)
    {
        TaskCompletionSource? completed = null;
        lock (_operationGate)
        {
            if (value && !_busy) _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _busy = value;
            if (!value) completed = _operationCompletion;
        }
        try { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(IsIdle)); NotifyCommands(); }
        finally { completed?.TrySetResult(); }
    }

    private bool TryBeginOperation()
    {
        lock (_operationGate)
        {
            if (_busy || IsStopping) return false;
            _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _busy = true;
        }
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(IsIdle)); NotifyCommands();
        return true;
    }

    /// <summary>Stops admission and cancels active I/O. Its actual result still owns completion and
    /// any ambiguous native execution; cancelling here never confirms a remote stop.</summary>
    public void BeginShutdown()
    {
        lock (_operationGate) Volatile.Write(ref _shutdownRequested, 1);
        lock (_shutdownGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _shutdown.Cancel();
        }
        void Notify() { OnPropertyChanged(nameof(IsIdle)); NotifyCommands(); }
        if (_dispatcher is null || _dispatcher.CheckAccess()) Notify();
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            _dispatcher.BeginInvoke(new Action(Notify));
    }

    /// <summary>Waits for the admitted composer operation's finally block. Deadline cancellation
    /// only ends the wait, retaining the still-running operation and its ownership.</summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task operation;
        lock (_operationGate) operation = _operationCompletion?.Task ?? Task.CompletedTask;
        return operation.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stoppingRegistration.Dispose();
        lock (_shutdownGate) _shutdown.Cancel();
        // A timed-out host drain can still leave an active operation. Keep its token source valid
        // until its own completion rather than disposing dependencies out from under it here.
        _ = WaitForIdleAsync().ContinueWith(_ => { lock (_shutdownGate) _shutdown.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(CanRefresh)); OnPropertyChanged(nameof(CanCancel));
        RelayCommand.RaiseCanExecuteChanged();
    }
}
