using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using LLMWorkGUI.App.ViewModels;

namespace LLMWorkGUI.App.Views;

/// <summary>Masked transient provider input; clears on unload or owner change and never loads stored credentials.</summary>
public sealed class ProviderApiKeyBox : UserControl
{
    private readonly PasswordBox _editor = new();
    private ProvidersAccountsViewModel? _provider;
    private bool _synchronizing;

    public ProviderApiKeyBox()
    {
        _editor.Padding = new Thickness(6, 3, 6, 3);
        AutomationProperties.SetName(_editor, "API-ключ провайдера");
        Content = _editor;
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        _editor.PasswordChanged += (_, _) =>
        {
            if (!_synchronizing && _provider is not null) _provider.EditingApiKey = _editor.Password;
        };
    }

    private void Attach()
    {
        var next = DataContext as ProvidersAccountsViewModel;
        if (!ReferenceEquals(next, _provider))
        {
            Detach();
            _provider = next;
            if (_provider is not null) _provider.PropertyChanged += OnProviderChanged;
        }
        Synchronize();
    }

    private void Detach()
    {
        var previous = _provider;
        _provider = null;
        if (previous is not null)
        {
            previous.PropertyChanged -= OnProviderChanged;
            previous.EditingApiKey = string.Empty;
        }
        Synchronize();
    }

    private void OnProviderChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(ProvidersAccountsViewModel.EditingApiKey)) Synchronize();
    }

    private void Synchronize()
    {
        var value = _provider?.EditingApiKey ?? string.Empty;
        if (_editor.Password == value) return;
        _synchronizing = true;
        try { _editor.Password = value; }
        finally { _synchronizing = false; }
    }
}
