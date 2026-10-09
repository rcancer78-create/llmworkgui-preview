using System.Windows.Controls;

namespace LLMWorkGUI.App.Views;

/// <summary>
/// Native WPF view of the Activity Monitor (ROADMAP Phase 10D). It renders exactly what
/// <see cref="ViewModels.WorkflowActivityMonitorViewModel"/> reports: the role schema of the active
/// version plus observed run events, the five node statuses, observed star-cliproxy route evidence and
/// the details drawer. Missing fields are shown as "Not reported".
/// </summary>
public partial class WorkflowActivityMonitorView : UserControl
{
    public WorkflowActivityMonitorView()
    {
        InitializeComponent();
    }
}
