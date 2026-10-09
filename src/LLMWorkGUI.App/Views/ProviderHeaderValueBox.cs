using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LLMWorkGUI.App.ViewModels;

namespace LLMWorkGUI.App.Views;

/// <summary>New secret input clears on unload or owner change; public header text survives and stored secrets stay write-only.</summary>
public sealed class ProviderHeaderValueBox : UserControl
{
    private readonly TextBox _text = new();
    private readonly PasswordBox _password = new();
    private CustomHeaderItemViewModel? _header;
    private bool _synchronizing;

    public ProviderHeaderValueBox()
    {
        AutomationProperties.SetName(_text, "Значение HTTP-заголовка");
        AutomationProperties.SetName(_password, "Секретное значение HTTP-заголовка");
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        _text.TextChanged += (_, _) => { if (!_synchronizing && _header is not null) _header.Value = _text.Text; };
        _password.PasswordChanged += (_, _) => { if (!_synchronizing && _header is not null) _header.Value = _password.Password; };
    }

    private void Attach()
    {
        var next = DataContext as CustomHeaderItemViewModel;
        if (!ReferenceEquals(next, _header))
        {
            Detach();
            _header = next;
            if (_header is not null) _header.PropertyChanged += OnChanged;
        }
        Synchronize();
    }

    private void Detach()
    {
        var previous = _header;
        _header = null;
        if (previous is not null)
        {
            previous.PropertyChanged -= OnChanged;
            if (previous.IsSensitive)
            {
                previous.Value = string.Empty;
                _text.Clear();
            }
        }
        _password.Clear();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs args) => Synchronize();

    private void Synchronize()
    {
        _synchronizing = true;
        try
        {
            var secret = _header?.IsSensitive == true;
            var value = _header?.Value ?? string.Empty;
            var publicText = secret ? string.Empty : value;
            var secretText = secret ? value : string.Empty;
            if (_text.Text != publicText) _text.Text = publicText;
            if (_password.Password != secretText) _password.Password = secretText;
            Content = secret ? _password : _text;
            ToolTip = _header?.ValueWatermark;
        }
        finally { _synchronizing = false; }
    }
}
