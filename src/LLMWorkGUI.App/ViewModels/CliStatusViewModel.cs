using System.Collections.ObjectModel;
using System.Globalization;
using LLMWorkGUI.Application.Cli;

namespace LLMWorkGUI.App.ViewModels;

public sealed class CliStatusViewModel : ObservableObject
{
    public const string NotCheckedHeadline = "Статус CLI бэкендов ещё не проверялся.";
    public const string NotCheckedRecommendation =
        "OpenCode и Cursor Agent — необязательные внешние компоненты: GUI запускается и работает без них.";
    public const string DegradedHeadline = "Ограниченный режим: CLI бэкендов не обнаружены.";
    public const string DegradedRecommendation =
        "Установите OpenCode или Cursor Agent и добавьте его в PATH, затем повторите проверку. " +
        "Экраны проектов, сессий, истории и настроек доступны без CLI; состояние данных указано на соответствующем экране.";
    public const string DetectionFailedHeadline = "Ограниченный режим: проверка CLI бэкендов не удалась.";
    public const string DetectionFailedRecommendation =
        "Не удалось проверить окружение. Проверьте PATH и разрешения, затем повторите проверку.";
    public const string HealthyHeadline = "Исполняемые файлы бэкендов обнаружены.";

    private readonly ICliDetectionService _detectionService;
    private readonly TimeProvider _timeProvider;

    private bool _isDetectionPending;
    private bool _isChecked;
    private bool _isDegraded;
    private bool _hasAnyDetected;
    private bool _hasDetectionFailed;
    private string _headline = NotCheckedHeadline;
    private string _recommendation = NotCheckedRecommendation;
    private string _lastCheckedDisplay = "Не проверялось";
    private string _detectionSummary = "Проверка не выполнялась";

    public CliStatusViewModel(ICliDetectionService detectionService, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(detectionService);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _detectionService = detectionService;
        _timeProvider = timeProvider;

        Tools = new ObservableCollection<CliToolStatusViewModel>();
        RefreshCommand = new RelayCommand(
            () => _ = RefreshAsync(),
            () => !IsDetectionPending);
    }

    public ObservableCollection<CliToolStatusViewModel> Tools { get; }

    public RelayCommand RefreshCommand { get; }

    public bool IsDetectionPending
    {
        get => _isDetectionPending;
        private set => SetProperty(ref _isDetectionPending, value);
    }

    public bool IsChecked
    {
        get => _isChecked;
        private set => SetProperty(ref _isChecked, value);
    }

    public bool IsDegraded
    {
        get => _isDegraded;
        private set => SetProperty(ref _isDegraded, value);
    }

    public bool HasAnyDetected
    {
        get => _hasAnyDetected;
        private set => SetProperty(ref _hasAnyDetected, value);
    }

    public bool HasDetectionFailed
    {
        get => _hasDetectionFailed;
        private set => SetProperty(ref _hasDetectionFailed, value);
    }

    public string Headline
    {
        get => _headline;
        private set => SetProperty(ref _headline, value);
    }

    public string Recommendation
    {
        get => _recommendation;
        private set => SetProperty(ref _recommendation, value);
    }

    public string LastCheckedDisplay
    {
        get => _lastCheckedDisplay;
        private set => SetProperty(ref _lastCheckedDisplay, value);
    }

    public string DetectionSummary
    {
        get => _detectionSummary;
        private set => SetProperty(ref _detectionSummary, value);
    }

    public int DetectedCount => Tools.Count(tool => tool.IsDetected);

    public int ToolCount => Tools.Count;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsDetectionPending)
        {
            return;
        }

        IsDetectionPending = true;
        RelayCommand.RaiseCanExecuteChanged();

        try
        {
            // PATH can include slow disks or network shares. Even a Task-returning locator may perform
            // synchronous filesystem I/O before returning; keep that work off the WPF dispatcher.
            var snapshot = await Task.Run(
                () => _detectionService.DetectAsync(cancellationToken), cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A filesystem call may finish or fail only after the window's lifetime has ended.
        }
        catch (Exception exception)
        {
            ApplyDetectionFailure(exception);
        }
        finally
        {
            IsDetectionPending = false;
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    private void ApplySnapshot(CliDetectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Tools.Clear();

        foreach (var tool in snapshot.Tools)
        {
            Tools.Add(new CliToolStatusViewModel(tool));
        }

        IsChecked = true;
        HasDetectionFailed = false;
        HasAnyDetected = snapshot.HasAnyDetected;
        IsDegraded = snapshot.IsDegraded;
        DetectionSummary = BuildSummary(snapshot);
        Headline = snapshot.IsDegraded ? DegradedHeadline : HealthyHeadline;
        Recommendation = snapshot.IsDegraded ? DegradedRecommendation : string.Empty;
        LastCheckedDisplay = FormatTimestamp(snapshot.DetectedAtUtc);

        RaiseToolDependentProperties();
    }

    private void ApplyDetectionFailure(Exception exception)
    {
        Tools.Clear();
        IsChecked = true;
        HasDetectionFailed = true;
        HasAnyDetected = false;
        IsDegraded = true;
        DetectionSummary = $"Проверка не удалась: {UiErrorMessage.Describe(exception)}";
        Headline = DetectionFailedHeadline;
        Recommendation = DetectionFailedRecommendation;
        LastCheckedDisplay = FormatTimestamp(_timeProvider.GetUtcNow());

        RaiseToolDependentProperties();
    }

    private void RaiseToolDependentProperties()
    {
        OnPropertyChanged(nameof(DetectedCount));
        OnPropertyChanged(nameof(ToolCount));
    }

    private string BuildSummary(CliDetectionSnapshot snapshot)
    {
        var detectedNames = snapshot.Tools
            .Where(tool => tool.IsDetected)
            .Select(tool => tool.DisplayName)
            .ToArray();

        if (detectedNames.Length == 0)
        {
            return $"Обнаружено CLI: 0 из {snapshot.Tools.Count}";
        }

        return $"Обнаружено CLI: {detectedNames.Length} из {snapshot.Tools.Count}: {string.Join(", ", detectedNames)}";
    }

    private string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
