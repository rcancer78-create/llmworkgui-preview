using System.Globalization;
using System.Text;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

internal sealed class FakeCursorAgentExecutable : IDisposable
{
    private readonly TestDirectory _directory = new();

    public FakeCursorAgentExecutable()
    {
        ExecutablePath = _directory.GetPath("cursor-agent.cmd");
        ReadyFilePath = _directory.GetPath("ready.txt");
        ChildProcessIdFilePath = _directory.GetPath("child.pid");

        var spawnScriptPath = _directory.GetPath("spawn-child.ps1");
        File.WriteAllText(
            spawnScriptPath,
            string.Join(
                Environment.NewLine,
                "$ErrorActionPreference = 'Stop'",
                "$p = Start-Process -FilePath 'ping.exe' -ArgumentList '-n','3600','127.0.0.1' -WindowStyle Hidden -PassThru",
                "[IO.File]::WriteAllText('" + ChildProcessIdFilePath.Replace("'", "''", StringComparison.Ordinal) + "', $p.Id.ToString())"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.Append("> \"").Append(ReadyFilePath).AppendLine("\" echo ready");
        body.Append("powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"")
            .Append(spawnScriptPath)
            .AppendLine("\"");
        body.AppendLine("ping -n 3600 127.0.0.1 > nul");

        File.WriteAllText(
            ExecutablePath,
            body.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public string ExecutablePath { get; }

    public string ReadyFilePath { get; }

    public string ChildProcessIdFilePath { get; }

    public static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"The file '{path}' was not created in time.");
    }

    public static async Task<int> WaitForPidAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (TryReadPid(path, out var processId))
            {
                return processId;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"The pid file '{path}' was not populated in time.");
    }

    public static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessAlive(processId))
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return !IsProcessAlive(processId);
    }

    public void Dispose() => _directory.Dispose();

    private static bool TryReadPid(string path, out int processId)
    {
        processId = 0;

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            return int.TryParse(
                File.ReadAllText(path).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out processId);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
