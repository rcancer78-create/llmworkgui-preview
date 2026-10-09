using System.Globalization;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// One file of the pre-send preview. <see cref="IsIncluded"/> is what the checkbox shows: checked means
/// the file is sent to the model, exactly as the label states. Files with a scanner finding are
/// fail-closed — they are never included, their checkbox cannot be checked and <see cref="IsExcluded"/>
/// refuses any attempt to include them, so the labeled gesture alone can never put a secret body into
/// the prompt.
/// </summary>
public sealed class AdaptationFileItemViewModel : ObservableObject
{
    private bool _isExcluded;
    private readonly Func<bool>? _canChangeExclusion;

    public AdaptationFileItemViewModel(
        string relativePath,
        bool isRecommendedExclusion,
        IReadOnlyList<WorkflowSecretFinding>? findings,
        bool isExcluded,
        Func<bool>? canChangeExclusion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        RelativePath = relativePath;
        IsRecommendedExclusion = isRecommendedExclusion;
        Findings = findings ?? Array.Empty<WorkflowSecretFinding>();
        _canChangeExclusion = canChangeExclusion;

        // A scanner hit is always excluded, whatever the caller passed.
        _isExcluded = HasSecretFinding || isExcluded;
    }

    public string RelativePath { get; }

    /// <summary>True when the secret scanner recommends excluding this file from the model payload.</summary>
    public bool IsRecommendedExclusion { get; }

    public IReadOnlyList<WorkflowSecretFinding> Findings { get; }

    public bool HasSecretFinding => Findings.Count > 0;

    /// <summary>False for scanner hits: their inclusion decision cannot be changed by the checkbox.</summary>
    public bool CanChangeExclusion => !HasSecretFinding && (_canChangeExclusion?.Invoke() ?? true);

    internal void NotifyEditingStateChanged() => OnPropertyChanged(nameof(CanChangeExclusion));

    public string ExclusionNote => HasSecretFinding
        ? "Файл содержит найденный секрет и всегда исключается из отправки."
        : "Снимите флажок, чтобы исключить файл из отправки.";

    public string SecretSummary => HasSecretFinding
        ? string.Join(
            "; ",
            Findings.Select(finding => string.Create(
                CultureInfo.InvariantCulture,
                $"{finding.RuleName} at line {finding.LineNumber}")))
        : "No secrets detected";

    /// <summary>True when the file is sent to the model; the checkbox reflects exactly this.</summary>
    public bool IsIncluded
    {
        get => !_isExcluded;
        set => IsExcluded = !value;
    }

    /// <summary>True when the file is excluded from the model payload; scanner hits stay excluded.</summary>
    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (!CanChangeExclusion) return;
            if (!value && HasSecretFinding)
            {
                // Fail closed: a scanner hit cannot be included by the labeled gesture alone.
                return;
            }

            if (SetProperty(ref _isExcluded, value))
            {
                OnPropertyChanged(nameof(IsIncluded));
            }
        }
    }
}
