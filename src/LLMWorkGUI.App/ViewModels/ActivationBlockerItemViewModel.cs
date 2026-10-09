using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// One blocker row of the workflow library activation gate. The operator records an explicit decision per
/// row; activation stays refused until every row is acknowledged, so a blocker is never accepted
/// silently or as part of a blanket flag.
/// <para>
/// A row that reports content the bounded semantic analysis could not read is not acknowledgeable at all:
/// a decision for it would stand in for content nobody ever saw.
/// </para>
/// </summary>
public sealed class ActivationBlockerItemViewModel : ObservableObject
{
    private readonly Action? _onAcknowledgedChanged;
    private bool _isAcknowledged;

    public ActivationBlockerItemViewModel(
        AdaptationValidationIssue issue,
        bool isAcknowledged = false,
        Action? onChanged = null)
    {
        ArgumentNullException.ThrowIfNull(issue);

        Issue = issue;
        _isAcknowledged = isAcknowledged && CanBeAcknowledged;
        _onAcknowledgedChanged = onChanged;
    }

    public AdaptationValidationIssue Issue { get; }

    public AdaptationBlockerKind Kind => Issue.Kind;

    public string KindDisplay => Issue.Kind.ToString();

    public string RoleDisplay => Issue.Role ?? "Workflow";

    public string Message => Issue.Message;

    public string Display => string.Concat(KindDisplay, " [", RoleDisplay, "]: ", Message);

    /// <summary>False for content the bounded analysis could not verify; such a row keeps its decision open.</summary>
    public bool CanBeAcknowledged => !Issue.IsNotClearable;

    /// <summary>Explicit operator decision for this single blocker row.</summary>
    public bool IsAcknowledged
    {
        get => _isAcknowledged;
        set
        {
            if (!CanBeAcknowledged)
            {
                value = false;
            }

            if (SetProperty(ref _isAcknowledged, value))
            {
                _onAcknowledgedChanged?.Invoke();
            }
        }
    }
}
