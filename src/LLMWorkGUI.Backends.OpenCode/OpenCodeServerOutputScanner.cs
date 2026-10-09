using System.Text;
using System.Text.RegularExpressions;

namespace LLMWorkGUI.Backends.OpenCode;

public sealed partial class OpenCodeServerOutputScanner
{
    private const int MaxBufferedCharacters = 8192;
    private const int RetainedCharacters = 4096;

    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private int? _assignedPort;

    [GeneratedRegex(
        @"https?://(?:127\.0\.0\.1|localhost)(?::(?<port>\d{1,5}))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListeningPattern();

    public int? AssignedPort
    {
        get
        {
            lock (_gate)
            {
                return _assignedPort;
            }
        }
    }

    public string BufferedText
    {
        get
        {
            lock (_gate)
            {
                return _buffer.ToString();
            }
        }
    }

    public bool Append(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        lock (_gate)
        {
            if (_assignedPort is not null)
            {
                return true;
            }

            _buffer.Append(chunk);

            if (_buffer.Length > MaxBufferedCharacters)
            {
                _buffer.Remove(0, _buffer.Length - RetainedCharacters);
            }

            if (TryParsePort(_buffer.ToString(), out var port))
            {
                _assignedPort = port;
                return true;
            }

            return false;
        }
    }

    public static bool TryParsePort(string? text, out int port)
    {
        port = 0;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (Match match in ListeningPattern().Matches(text))
        {
            if (!match.Groups["port"].Success)
            {
                continue;
            }

            if (int.TryParse(match.Groups["port"].Value, out var candidate)
                && candidate is > 0 and <= 65535)
            {
                port = candidate;
                return true;
            }
        }

        return false;
    }
}
