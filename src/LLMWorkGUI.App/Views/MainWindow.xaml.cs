using System.Windows;
using System.Windows.Input;
using LLMWorkGUI.App.Help;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;

namespace LLMWorkGUI.App.Views;

public partial class MainWindow : Window
{
    private HelpGuideWindow? _guideWindow;

    public MainWindow(MainWindowViewModel viewModel)
        : this(viewModel, null)
    {
    }

    public MainWindow(MainWindowViewModel viewModel, UnifiedWorkspaceShellViewModel? shellViewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        CommandBindings.Add(new CommandBinding(HelpCommands.OpenGuide,
            (_, args) => { OpenGuide(); args.Handled = true; },
            (_, args) => { args.CanExecute = true; args.Handled = true; }));
        InputBindings.Add(new KeyBinding(HelpCommands.OpenGuide, Key.F1, ModifierKeys.None));
        Closed += (_, _) => _guideWindow?.Close();

        if (shellViewModel is not null)
        {
            UnifiedWorkspaceShell.DataContext = shellViewModel;
            UnifiedWorkspaceShell.Visibility = Visibility.Visible;
            TopBar.Visibility = Visibility.Collapsed;
            DegradedBanner.Visibility = Visibility.Collapsed;
            ShellPanes.Visibility = Visibility.Collapsed;
            GlobalStatusBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            UnifiedWorkspaceShell.DataContext = null;
            UnifiedWorkspaceShell.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenGuide()
    {
        if (_guideWindow is not null)
        {
            if (_guideWindow.WindowState == WindowState.Minimized) _guideWindow.WindowState = WindowState.Normal;
            _guideWindow.Activate();
            return;
        }
        var guide = new HelpGuideWindow { Owner = this };
        _guideWindow = guide;
        guide.Closed += (_, _) => { if (ReferenceEquals(_guideWindow, guide)) _guideWindow = null; };
        guide.Show();
    }
}
