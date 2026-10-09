using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LLMWorkGUI.App.ViewModels;

namespace LLMWorkGUI.App.Views;

/// <summary>Masked transient input only. Never displays a stored key; clears on account change/unload/save.</summary>
public sealed class AccountApiKeyBox : UserControl
{
    private readonly PasswordBox _editor = new() { Padding = new Thickness(6, 3, 6, 3), MaxLength = 16384 };
    private AccountManagementViewModel? _account;
    private bool _synchronizing;
    public AccountApiKeyBox()
    {
        AutomationProperties.SetName(_editor, "Новый API-ключ аккаунта"); Content = _editor;
        DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach(); Unloaded += (_, _) => Detach();
        _editor.PasswordChanged += (_, _) => { if (!_synchronizing && _account is not null) _account.EnteredApiKey = _editor.Password; };
    }
    private void Attach()
    {
        var next = DataContext as AccountManagementViewModel;
        if (!ReferenceEquals(next, _account))
        {
            Detach(); _account = next;
            if (_account is not null) _account.PropertyChanged += Changed;
        }
        Synchronize();
    }
    private void Detach()
    {
        if (_account is not null) { _account.PropertyChanged -= Changed; _account.ClearAccountKey(); }
        _account = null; Synchronize();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is null or nameof(AccountManagementViewModel.EnteredApiKey)) Synchronize(); }
    private void Synchronize()
    {
        var value = _account?.EnteredApiKey ?? ""; if (_editor.Password == value) return;
        _synchronizing = true;
        try { _editor.Password = value; } finally { _synchronizing = false; }
    }
}
