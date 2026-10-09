using System.Windows;
using System.Windows.Controls;
using LLMWorkGUI.App.ViewModels.Onboarding;
using Microsoft.Win32;

namespace LLMWorkGUI.App.Views.Onboarding;

/// <summary>
/// The onboarding overlay (ROADMAP Phase 11). The code-behind only wires the optional local folder
/// picker to the view model; every step, command and state transition stays in
/// <see cref="OnboardingViewModel"/> so the wizard is fully testable headless.
/// </summary>
public partial class OnboardingView : UserControl
{
    public OnboardingView()
    {
        InitializeComponent();
    }

    private void OnBrowseWorkspaceClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OnboardingViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Выберите рабочий каталог проекта",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        viewModel.WorkspacePath = dialog.FolderName;
        _ = viewModel.ConfirmWorkspaceAsync(dialog.FolderName);
    }
}
