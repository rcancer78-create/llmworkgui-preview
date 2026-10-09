using System.Globalization;
using System.Diagnostics;
using System.Text;
using LLMGateway.Native;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.IntegrationTests.Processes;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

internal sealed class OpenCodeFakeServer : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly List<Process> _ownedRoots = [];

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
        var node = new ExecutableResolver().Resolve("node")?.FileName
            ?? throw new InvalidOperationException("Node.js 22 is required for fake CLI tests.");
        var body = new StringBuilder()
            .AppendLine("@echo off")
            .Append('"').Append(node).Append("\" \"")
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

    public bool StartProcess(Process process)
    {
        var started = process.Start();
        if (started && ProcessIdFilePath is not null)
        {
            // Keep this exact native lifetime for failure cleanup, independently of the
            // supervisor's handle. A later PID-file lookup must never authorize a kill.
            var root = Process.GetProcessById(process.Id);
            _ = root.SafeHandle;
            lock (_ownedRoots) _ownedRoots.Add(root);
        }
        return started;
    }

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
        List<Process> roots;
        lock (_ownedRoots) roots = [.. _ownedRoots];
        var descendants = new List<Process>();
        try
        {
            // This runs after the physical-cleanup assertions. Native ancestry and creation
            // times bind descendants to retained roots, including a root that has exited.
            descendants = NativeProcessTreeSnapshot.Capture(roots);
            foreach (var owned in descendants.Concat(roots))
            {
                try
                {
                    if (!owned.HasExited)
                    {
                        owned.Kill(entireProcessTree: true);
                        if (!owned.WaitForExit(5000)) { throw new TimeoutException("Owned fake server did not stop."); }
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException)
                { } // An exiting helper can disappear between probes; never target a reused PID.
            }
        }
        finally
        {
            foreach (var owned in descendants.Concat(roots)) owned.Dispose();
            _directory.Dispose();
        }
    }

    private string WriteHelperScript()
    {
        var path = _directory.GetPath("server-helper.cjs");
        var body = string.Join(
            Environment.NewLine,
            "const fs = require('node:fs');",
            "const [pidFile, startupLine] = process.argv.slice(2);",
            "fs.writeFileSync(pidFile + '.helper', String(process.pid));",
            "fs.writeFileSync(pidFile, String(process.ppid));",
            "if (startupLine) process.stdout.write(startupLine + '\\n');",
            "setInterval(() => {}, 1000);",
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
