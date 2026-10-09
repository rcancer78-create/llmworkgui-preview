using System.Windows.Controls;
using System.Windows;

namespace LLMWorkGUI.App.Views;

/// <summary>
/// Consolidated Workflow Console overlay (ROADMAP Phase 11). The view is a pure presentation shell over
/// <see cref="ViewModels.WorkflowConsolidatedViewModel"/>; all navigation, quick import/export and
/// studio/monitor state live in the view model.
/// </summary>
public partial class WorkflowConsoleView : UserControl
{
    public WorkflowConsoleView()
    {
        InitializeComponent();
    }

    private void OnConsoleViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ConsoleContent.Width = Math.Max(780, e.NewSize.Width - SystemParameters.VerticalScrollBarWidth);
        ConsoleContent.MinHeight = Math.Max(720, e.NewSize.Height - SystemParameters.HorizontalScrollBarHeight);
    }
}
