using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>One warning or blocker row of the adaptation validation panel.</summary>
public sealed class AdaptationValidationIssueViewModel : ObservableObject
{
    public AdaptationValidationIssueViewModel(AdaptationValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        Issue = issue;
    }

    public AdaptationValidationIssue Issue { get; }

    public AdaptationBlockerKind Kind => Issue.Kind;

    public string KindDisplay => Issue.Kind.ToString();

    public string RoleDisplay => Issue.Role ?? "Workflow";

    public string Message => Issue.Message;

    public string Display => string.Concat(KindDisplay, " [", RoleDisplay, "]: ", Message);
}
