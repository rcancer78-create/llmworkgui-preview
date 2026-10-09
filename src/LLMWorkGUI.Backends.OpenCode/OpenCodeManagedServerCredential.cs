using System.Net.Http.Headers;
using System.Text;

namespace LLMWorkGUI.Backends.OpenCode;

/// <summary>Password for the OpenCode process this application started. It is not an account secret.</summary>
public sealed class OpenCodeManagedServerCredential
{
    public const string Username = "opencode";
    private readonly object _gate = new();
    private string? _password;
    private readonly Dictionary<int, string> _passwordsByPort = new();

    public void Publish(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        lock (_gate) _password = password;
    }

    public void BindPort(int port, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        lock (_gate)
        {
            _passwordsByPort[port] = password;
            if (string.Equals(_password, password, StringComparison.Ordinal))
                _password = null;
        }
    }

    public void ClearIfCurrent(string password) => ClearPassword(password);

    public void ClearPassword(string password)
    {
        lock (_gate)
        {
            if (string.Equals(_password, password, StringComparison.Ordinal))
                _password = null;
            foreach (var port in _passwordsByPort.Where(pair => string.Equals(pair.Value, password, StringComparison.Ordinal))
                         .Select(pair => pair.Key).ToArray())
                _passwordsByPort.Remove(port);
        }
    }

    public bool TryApply(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.Authorization is not null) return false;
        string? password;
        lock (_gate)
        {
            if (request.RequestUri is { } uri && _passwordsByPort.TryGetValue(uri.Port, out var bound))
                password = bound;
            else if (_passwordsByPort.Count == 0)
                password = _password;
            else
                return false;
        }
        if (password is null) return false;
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + password)));
        return true;
    }
}
