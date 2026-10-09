using System.Text;
using System.Text.Json;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

/// <summary>
/// Real OS executable that speaks just enough newline-delimited JSON-RPC over stdio to answer the
/// ACP <c>initialize</c> handshake. It proves the end-to-end binding of the transport to a live
/// managed process without requiring an installed <c>cursor-agent</c>.
/// </summary>
internal sealed class FakeAcpAgentExecutable : IDisposable
{
    private readonly TestDirectory _directory = new();

    public FakeAcpAgentExecutable(string handshakeResultJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeResultJson);

        ExecutablePath = _directory.GetPath("fake-acp-agent.cmd");

        var scriptPath = _directory.GetPath("fake-acp-agent.ps1");
        var resultPath = _directory.GetPath("initialize-result.json");

        // The result must be single-line: a pretty-printed fixture would inject newlines into the
        // frame and break newline-delimited JSON-RPC framing.
        File.WriteAllText(resultPath, Compact(handshakeResultJson), Utf8NoBom);

        // The agent reads newline-delimited JSON-RPC requests from stdin and answers 'initialize'
        // with the sanitized fixture result, correlated by request id. Any other method gets a
        // JSON-RPC error, so the client can never mistake it for a supported capability.
        var script = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "$OutputEncoding = New-Object System.Text.UTF8Encoding($false)",
            "$stdout = [Console]::OpenStandardOutput()",
            "$writer = New-Object System.IO.StreamWriter($stdout, (New-Object System.Text.UTF8Encoding($false)))",
            "$writer.AutoFlush = $true",
            "$resultJson = [IO.File]::ReadAllText('" + Escape(resultPath) + "')",
            "while ($true) {",
            "  $line = [Console]::In.ReadLine()",
            "  if ($null -eq $line) { break }",
            "  if ([string]::IsNullOrWhiteSpace($line)) { continue }",
            "  try { $request = $line | ConvertFrom-Json } catch { continue }",
            "  if ($null -eq $request.id) { continue }",
            "  $id = ($request.id | ConvertTo-Json -Compress)",
            "  if ($request.method -eq 'initialize') {",
            "    $frame = '{\"jsonrpc\":\"2.0\",\"id\":' + $id + ',\"result\":' + $resultJson + '}'",
            "  } else {",
            "    $frame = '{\"jsonrpc\":\"2.0\",\"id\":' + $id + " +
            "',\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}'",
            "  }",
            "  $writer.Write($frame)",
            "  $writer.Write(\"`n\")",
            "}");

        File.WriteAllText(scriptPath, script, Utf8NoBom);

        var launcher = new StringBuilder();
        launcher.AppendLine("@echo off");
        launcher.Append("powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"")
            .Append(scriptPath)
            .AppendLine("\"");

        File.WriteAllText(ExecutablePath, launcher.ToString(), Utf8NoBom);
    }

    private static UTF8Encoding Utf8NoBom => new(encoderShouldEmitUTF8Identifier: false);

    public string ExecutablePath { get; }

    public void Dispose() => _directory.Dispose();

    private static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);

        return JsonSerializer.Serialize(
            document.RootElement,
            new JsonSerializerOptions { WriteIndented = false });
    }

    private static string Escape(string path) => path.Replace("'", "''", StringComparison.Ordinal);
}
