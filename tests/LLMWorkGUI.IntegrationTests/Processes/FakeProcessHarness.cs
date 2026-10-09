using System.Globalization;
using System.Text;
using LLMWorkGUI.Application.Processes;

namespace LLMWorkGUI.IntegrationTests.Processes;

internal sealed class FakeProcessHarness : IDisposable
{
    public const string ContinuousOutputLine =
        "0123456789012345678901234567890123456789012345678901234567890123";

    private readonly TestDirectory _directory = new();

    public string WorkingDirectory => _directory.Root;

    public string GetPath(string fileName)
    {
        return _directory.GetPath(fileName);
    }

    public ProcessStartSpecification CreateOutputSpecification(
        string executionId,
        string standardOutput,
        string standardError = "",
        int exitCode = 0)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");

        if (standardOutput.Length > 0)
        {
            body.Append("echo ").AppendLine(standardOutput);
        }

        if (standardError.Length > 0)
        {
            body.Append(">&2 echo ").AppendLine(standardError);
        }

        body.Append("exit /b ").AppendLine(exitCode.ToString(CultureInfo.InvariantCulture));

        return CreateCommandSpecification(executionId, body.ToString());
    }

    public ProcessStartSpecification CreateArgumentSpecification(
        string executionId,
        params string[] arguments)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.AppendLine("echo first=%1");
        body.AppendLine("echo second=%2");

        return CreateCommandSpecification(executionId, body.ToString(), arguments);
    }

    public ProcessStartSpecification CreateEnvironmentSpecification(
        string executionId,
        string variableName,
        string variableValue)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.Append("echo value=%").Append(variableName).AppendLine("%");

        return CreateCommandSpecification(
            executionId,
            body.ToString(),
            environmentVariables: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [variableName] = variableValue
            });
    }

    public ProcessStartSpecification CreateExitCodeSpecification(string executionId, int exitCode)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.Append("exit /b ").AppendLine(exitCode.ToString(CultureInfo.InvariantCulture));

        return CreateCommandSpecification(executionId, body.ToString());
    }

    public ProcessStartSpecification CreateHangSpecification(string executionId)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.AppendLine("ping -n 3600 127.0.0.1 > nul");

        return CreateCommandSpecification(executionId, body.ToString());
    }

    public ProcessStartSpecification CreateStdinReadSpecification(
        string executionId,
        ProcessStdinPolicy stdinPolicy = ProcessStdinPolicy.Closed)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.AppendLine("set \"value=\"");
        body.AppendLine("set /p \"value=prompt: \"");
        body.AppendLine("if defined value (echo stdin-value=%value%) else (echo stdin-eof)");
        body.AppendLine("exit /b 0");

        return CreateCommandSpecification(executionId, body.ToString(), stdinPolicy: stdinPolicy);
    }

    public ProcessStartSpecification CreatePowerShellStdinReadSpecification(string executionId)
    {
        var body = new StringBuilder();
        body.AppendLine("$ErrorActionPreference = 'Stop'");
        body.AppendLine("$line = [Console]::In.ReadLine()");
        body.AppendLine(
            "if ($null -eq $line) { Write-Output 'stdin-eof' } else { Write-Output ('stdin-value=' + $line) }");
        body.AppendLine("exit 0");

        return CreatePowerShellSpecification(executionId, "read-stdin", body.ToString());
    }

    public ProcessStartSpecification CreateStdinReadFailureSpecification(string executionId)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.AppendLine("set \"value=\"");
        body.AppendLine("set /p \"value=Enter value from stdin: \"");
        body.AppendLine("if not defined value (");
        body.AppendLine("  >&2 echo error: interactive input wait; stdin read returned EOF");
        body.AppendLine("  exit /b 2");
        body.AppendLine(")");
        body.AppendLine("exit /b 0");

        return CreateCommandSpecification(executionId, body.ToString());
    }

    public ProcessStartSpecification CreateRequiredArgumentSpecification(string executionId)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.AppendLine("if \"%~1\"==\"\" (");
        body.AppendLine("  >&2 echo error: missing required argument");
        body.AppendLine("  exit /b 3");
        body.AppendLine(")");
        body.AppendLine("echo first=%1");
        body.AppendLine("exit /b 0");

        return CreateCommandSpecification(executionId, body.ToString());
    }

    public ProcessStartSpecification CreateGenericFailureSpecification(
        string executionId,
        string message,
        int exitCode)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.Append(">&2 echo ").AppendLine(message);
        body.Append("exit /b ").AppendLine(exitCode.ToString(CultureInfo.InvariantCulture));

        return CreateCommandSpecification(executionId, body.ToString());
    }

    public ProcessStartSpecification CreateSilentPeriodSpecification(
        string executionId,
        int silenceSeconds)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.AppendLine("echo ready");
        body.Append("ping -n ")
            .Append((silenceSeconds + 1).ToString(CultureInfo.InvariantCulture))
            .AppendLine(" 127.0.0.1 > nul");
        body.AppendLine("exit /b 0");

        return CreateCommandSpecification(executionId, body.ToString());
    }

    public ProcessStartSpecification CreateArgumentCaptureSpecification(
        string executionId,
        string captureFilePath,
        params string[] arguments)
    {
        var escapedCapturePath = captureFilePath.Replace("'", "''", StringComparison.Ordinal);

        var body = new StringBuilder();
        body.AppendLine("$ErrorActionPreference = 'Stop'");
        body.AppendLine(
            "$lines = foreach ($a in $args) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$a)) }");
        body.Append("[IO.File]::WriteAllLines('").Append(escapedCapturePath)
            .AppendLine("', $lines, (New-Object System.Text.UTF8Encoding($false)))");

        return CreatePowerShellSpecification(executionId, "capture-arguments", body.ToString(), arguments);
    }

    public ProcessStartSpecification CreateLongLivedServerSpecification(
        string executionId,
        string pidFilePath)
    {
        var escapedPidPath = pidFilePath.Replace("'", "''", StringComparison.Ordinal);

        var body = new StringBuilder();
        body.AppendLine("$ErrorActionPreference = 'Stop'");
        body.Append("[IO.File]::WriteAllText('").Append(escapedPidPath)
            .AppendLine("', $PID.ToString())");
        body.AppendLine("Start-Sleep -Seconds 3600");

        return CreatePowerShellSpecification(executionId, "long-lived-server", body.ToString());
    }

    public ProcessStartSpecification CreateContinuousOutputSpecification(
        string executionId,
        int lineCount)
    {
        var body = new StringBuilder();
        body.AppendLine("@echo off");
        body.Append("for /L %%i in (1,1,")
            .Append(lineCount.ToString(CultureInfo.InvariantCulture))
            .Append(") do @echo ")
            .AppendLine(ContinuousOutputLine);

        return CreateCommandSpecification(executionId, body.ToString());
    }

    public ProcessStartSpecification CreateSpawnChildAndHangSpecification(
        string executionId,
        string childPidFilePath)
    {
        return CreateSpawnChildSpecification(executionId, childPidFilePath, hangAfterSpawn: true);
    }

    public ProcessStartSpecification CreateSpawnChildAndExitSpecification(
        string executionId,
        string childPidFilePath)
    {
        return CreateSpawnChildSpecification(executionId, childPidFilePath, hangAfterSpawn: false);
    }

    public static long ExpectedContinuousOutputBytes(int lineCount)
    {
        return (long)lineCount * (ContinuousOutputLine.Length + 2);
    }

    public static async Task<int> WaitForChildPidAsync(string pidFilePath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (TryReadPid(pidFilePath, out var processId))
            {
                return processId;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"The child PID file '{pidFilePath}' was not created in time.");
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

    public void Dispose()
    {
        _directory.Dispose();
    }

    private ProcessStartSpecification CreateCommandSpecification(
        string executionId,
        string scriptBody,
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        ProcessStdinPolicy stdinPolicy = ProcessStdinPolicy.Closed)
    {
        var scriptPath = WriteScript(executionId, ".cmd", scriptBody);

        var allArguments = new List<string> { "/d", "/c", scriptPath };
        if (arguments is not null)
        {
            allArguments.AddRange(arguments);
        }

        return new ProcessStartSpecification
        {
            ExecutionId = executionId,
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = allArguments,
            WorkingDirectory = WorkingDirectory,
            EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StdinPolicy = stdinPolicy
        };
    }

    private ProcessStartSpecification CreatePowerShellSpecification(
        string executionId,
        string scriptName,
        string scriptBody,
        IReadOnlyList<string>? arguments = null)
    {
        var powerShellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        var scriptPath = WriteScript(executionId, "-" + scriptName + ".ps1", scriptBody);

        var allArguments = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath
        };

        if (arguments is not null)
        {
            allArguments.AddRange(arguments);
        }

        return new ProcessStartSpecification
        {
            ExecutionId = executionId,
            FileName = powerShellPath,
            Arguments = allArguments,
            WorkingDirectory = WorkingDirectory
        };
    }

    private ProcessStartSpecification CreateSpawnChildSpecification(
        string executionId,
        string childPidFilePath,
        bool hangAfterSpawn)
    {
        var powerShellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        var body = new StringBuilder();
        body.AppendLine("$ErrorActionPreference = 'Stop'");
        body.Append("$child = Start-Process -FilePath '").Append(powerShellPath)
            .Append("' -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 3600'")
            .AppendLine(" -WindowStyle Hidden -PassThru");
        body.Append("Set-Content -LiteralPath '").Append(childPidFilePath).AppendLine("' -Value $child.Id");

        if (hangAfterSpawn)
        {
            body.AppendLine("Start-Sleep -Seconds 3600");
        }
        else
        {
            body.AppendLine("exit 0");
        }

        var scriptPath = WriteScript(executionId, ".ps1", body.ToString());

        return new ProcessStartSpecification
        {
            ExecutionId = executionId,
            FileName = powerShellPath,
            Arguments = new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath
            },
            WorkingDirectory = WorkingDirectory
        };
    }

    private string WriteScript(string executionId, string extension, string scriptBody)
    {
        var fileName = $"fake-process-{executionId}{extension}";
        var path = Path.Combine(WorkingDirectory, fileName);

        File.WriteAllText(path, scriptBody, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return path;
    }

    private static bool TryReadPid(string pidFilePath, out int processId)
    {
        processId = 0;

        if (!File.Exists(pidFilePath))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(pidFilePath).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
