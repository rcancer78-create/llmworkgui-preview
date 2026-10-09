using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Infrastructure.Processes;

/// <summary>
/// Disk-only framing: never publish a prefix before the full diagnostic record can be inspected.
/// Pipe transport and its byte counters remain independent. Oversized or broken structured records
/// are suppressed rather than split into pieces that could expose an unrecognisable credential tail.
/// </summary>
internal sealed class DiagnosticSpoolRedactor
{
    internal const int MaximumRecordCharacters = 64 * 1024;
    private const string Suppressed = "[REDACTED: incomplete or oversized diagnostic record]\n";
    private readonly CredentialTextRedactor _redactor = new();
    private readonly StringBuilder _record = new();
    private readonly char[] _pemWindow = new char[96];
    private int _pemPosition, _pemLength, _hyphens, _pemBoundaryKind;
    private bool _started, _json, _inString, _escaped, _privateKey, _overflow;
    private readonly char[] _logPrefix = new char[9]; // longest recognized prefix: [WARNING]
    private int _logPrefixLength;
    private bool _logPrefixCandidate;
    private bool _awaitLogTail, _prefixedLog;
    private int _jsonStart;
    private int _depth;
    public bool RecordsSuppressed { get; private set; }

    public string Append(ReadOnlySpan<char> input, bool complete = false)
    {
        var output = new StringBuilder();
        foreach (var value in input)
        {
            if (!_started && !char.IsWhiteSpace(value))
            {
                _started = true;
                _json = value is '{' or '[';
                _logPrefixCandidate = value == '[';
            }
            if (_record.Length < MaximumRecordCharacters && !_overflow) _record.Append(value);
            else { _overflow = true; _record.Clear(); }
            if (_awaitLogTail && !char.IsWhiteSpace(value))
            {
                _awaitLogTail = false;
                _json = value is '{' or '[';
                if (_json) _jsonStart = Math.Max(0, _record.Length - 1);
            }
            // A diagnostic may place public prose before a JSON object. Once its
            // opener appears, keep the entire structured tail framed and validated.
            if (_prefixedLog && !_json && value == '{')
            {
                _json = true;
                _jsonStart = Math.Max(0, _record.Length - 1);
            }

            if (_json)
            {
                if (_inString)
                {
                    if (_escaped) _escaped = false;
                    else if (value == '\\') _escaped = true;
                    else if (value == '"') _inString = false;
                }
                else if (value == '"') _inString = true;
                else if (value is '{' or '[') _depth++;
                else if (value is '}' or ']') _depth--;
                // Inspect only the bounded first prefix; a ruled-out JSON prefix is never
                // reconsidered at later balanced brackets. Do not copy the growing record.
                if (_logPrefixCandidate)
                {
                    if (_logPrefixLength == _logPrefix.Length) _logPrefixCandidate = false;
                    else
                    {
                        _logPrefix[_logPrefixLength++] = value;
                        var prefix = _logPrefix.AsSpan(0, _logPrefixLength);
                        _logPrefixCandidate = IsPossibleLogPrefix(prefix);
                        if (value == ']' && _depth == 0 && _logPrefixCandidate)
                        {
                            _json = false;
                            _logPrefixCandidate = false;
                            _awaitLogTail = true;
                            _prefixedLog = true;
                        }
                    }
                }
            }
            else TrackPrivateKey(value);

            if (value == '\n' && !_privateKey && (!_json || _depth <= 0)) Emit(output);
        }
        if (complete && (_record.Length != 0 || _overflow)) Emit(output);
        return output.ToString();
    }

    private static bool IsPossibleLogPrefix(ReadOnlySpan<char> prefix) =>
        "[INFO]".AsSpan().StartsWith(prefix, StringComparison.Ordinal)
        || "[WARN]".AsSpan().StartsWith(prefix, StringComparison.Ordinal)
        || "[WARNING]".AsSpan().StartsWith(prefix, StringComparison.Ordinal)
        || "[ERROR]".AsSpan().StartsWith(prefix, StringComparison.Ordinal)
        || "[DEBUG]".AsSpan().StartsWith(prefix, StringComparison.Ordinal)
        || "[TRACE]".AsSpan().StartsWith(prefix, StringComparison.Ordinal);

    private void Emit(StringBuilder output)
    {
        var text = _record.ToString();
        var suppress = _overflow;
        if (_json && !suppress)
        {
            try { using var parsed = JsonDocument.Parse(text.AsMemory(_jsonStart)); }
            catch (JsonException) { suppress = true; }
        }
        if (suppress)
        {
            output.Append(Suppressed);
            RecordsSuppressed = true;
        }
        else
        {
            var safe = _json && _jsonStart > 0
                ? _redactor.Redact(text[.._jsonStart]) + _redactor.RedactDiagnostic(text[_jsonStart..])
                : _redactor.RedactDiagnostic(text);
            output.Append(safe);
            // Changed JSON is serialized without its surrounding whitespace by the shared redactor.
            // Keep the physical record terminator; never glue the next diagnostic to this object.
            if (_json && text.EndsWith('\n') && !safe.EndsWith('\n'))
                output.Append(text.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        }
        _record.Clear();
        _started = _json = _inString = _escaped = _privateKey = _overflow = false;
        _depth = _pemPosition = _pemLength = _hyphens = _pemBoundaryKind = _logPrefixLength = 0;
        _logPrefixCandidate = false;
        _awaitLogTail = false;
        _prefixedLog = false;
        _jsonStart = 0;
    }

    private void TrackPrivateKey(char value)
    {
        // A small rolling window continues recognising PEM boundaries even after record overflow.
        _pemWindow[_pemPosition] = value;
        _pemPosition = (_pemPosition + 1) % _pemWindow.Length;
        _pemLength = Math.Min(_pemLength + 1, _pemWindow.Length);
        _hyphens = value == '-' ? _hyphens + 1 : 0;
        if (value == ' ')
        {
            if (WindowEndsWith("-----BEGIN ")) _pemBoundaryKind = 1;
            else if (WindowEndsWith("-----END ")) _pemBoundaryKind = -1;
        }
        if (_hyphens == 5 && _pemBoundaryKind != 0 && WindowEndsWith("PRIVATE KEY-----"))
        {
            _privateKey = _pemBoundaryKind == 1;
            _pemBoundaryKind = 0;
        }
        else if (!(char.IsAsciiLetterUpper(value) || char.IsAsciiDigit(value) || value is ' ' or '-'))
            _pemBoundaryKind = 0;
    }

    private bool WindowEndsWith(string suffix)
    {
        if (_pemLength < suffix.Length) return false;
        for (var i = 0; i < suffix.Length; i++)
            if (_pemWindow[(_pemPosition - suffix.Length + i + _pemWindow.Length) % _pemWindow.Length] != suffix[i])
                return false;
        return true;
    }
}
