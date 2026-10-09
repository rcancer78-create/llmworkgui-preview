using System.Globalization;
using System.Diagnostics;
using System.Text;
using LLMWorkGUI.IntegrationTests.Processes;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

internal sealed class OpenCodeFakeServer : IDisposable
{
    private readonly TestDirectory _directory = new();

    public OpenCodeFakeServer(string? startupLine, int? exitCode = null)
    {
        if (exitCode is { } code)
        {
            var exitBody = new StringBuilder()
                .AppendLine("@echo off")
                .Append("exit /b ")
                .AppendLine(code.ToString(CultureInfo.InvariantCulture));

            ScriptPath = WriteScript("opencode.cmd", exitBody.ToString());
            ProcessIdFilePath = null;
            return;
        }

        var processIdFilePath = _directory.GetPath("server.pid");
        var helperPath = WriteHelperScript();
        var body = new StringBuilder()
            .AppendLine("@echo off")
            .Append("powershell -NoProfile -ExecutionPolicy Bypass -File \"")
            .Append(helperPath)
            .Append("\" \"")
            .Append(processIdFilePath)
            .Append("\" \"")
            .Append(startupLine ?? string.Empty)
            .AppendLine("\"");

        ScriptPath = WriteScript("opencode.cmd", body.ToString());
        ProcessIdFilePath = processIdFilePath;
    }

    public string ScriptPath { get; }

    public string? ProcessIdFilePath { get; }
    public string? HelperProcessIdFilePath => ProcessIdFilePath is null ? null : ProcessIdFilePath + ".helper";

    public Task<int> GetHelperProcessIdAsync() => HelperProcessIdFilePath is null
        ? throw new InvalidOperationException("This fake server has no helper process.")
        : FakeProcessHarness.WaitForChildPidAsync(HelperProcessIdFilePath, TimeSpan.FromSeconds(20));

    public async Task<int> GetServerProcessIdAsync(TimeSpan? timeout = null)
    {
        if (ProcessIdFilePath is null)
        {
            throw new InvalidOperationException("This fake server does not report a process id.");
        }

        return await FakeProcessHarness
            .WaitForChildPidAsync(ProcessIdFilePath, timeout ?? TimeSpan.FromSeconds(20))
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            // Test failure cleanup is separate from assertions that production actually stopped
            // the helper. Match its recorded lifetime and retain a handle before any kill.
            if (HelperProcessIdFilePath is { } pidFile && File.Exists(pidFile) && File.Exists(pidFile + ".start")
                && int.TryParse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture, out var pid)
                && long.TryParse(File.ReadAllText(pidFile + ".start"), CultureInfo.InvariantCulture, out var ticks))
            {
                try
                {
                    using var helper = Process.GetProcessById(pid);
                    _ = helper.SafeHandle;
                    if (helper.StartTime.ToUniversalTime().Ticks == ticks && !helper.HasExited)
                    {
                        helper.Kill(entireProcessTree: true);
                        if (!helper.WaitForExit(5000)) { throw new TimeoutException("Owned fake helper did not stop."); }
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException)
                { } // An exiting helper can disappear between probes; never target a reused PID.
            }
        }
        finally { _directory.Dispose(); }
    }

    private string WriteHelperScript()
    {
        var path = _directory.GetPath("server-helper.ps1");
        var body = string.Join(
            Environment.NewLine,
            "param([string]$PidFile, [string]$StartupLine)",
            "$ErrorActionPreference = 'Stop'",
            "$parent = (Get-CimInstance Win32_Process -Filter \"ProcessId=$PID\").ParentProcessId",
            "$helper = [Diagnostics.Process]::GetCurrentProcess()",
            "[IO.File]::WriteAllText($PidFile + '.helper.start', [string]$helper.StartTime.ToUniversalTime().Ticks)",
            "[IO.File]::WriteAllText($PidFile + '.helper', [string]$PID)",
            "[IO.File]::WriteAllText($PidFile, [string]$parent)",
            "if ($StartupLine) { Write-Output $StartupLine }",
            "Start-Sleep -Seconds 3600",
            string.Empty);

        File.WriteAllText(path, body, new UTF8Encoding(false));

        return path;
    }

    private string WriteScript(string fileName, string body)
    {
        var path = _directory.GetPath(fileName);
        File.WriteAllText(path, body, new UTF8Encoding(false));

        return path;
    }
}
