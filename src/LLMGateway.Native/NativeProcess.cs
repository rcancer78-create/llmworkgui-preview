using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using LLMGateway.Core;

namespace LLMGateway.Native;

public sealed record NativeLaunch(
    LaunchTarget Target,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment,
    string WorkingDirectory,
    string? StandardInput = null);

public sealed record NativeRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Success => !TimedOut && ExitCode == 0;
    public string Combined => (StandardOutput + "\n" + StandardError).Trim();
}

/// <summary>Local process and, on Windows, private job observations; never proves remote operation termination.</summary>
public sealed record NativeProcessTermination(int ProcessId, bool KillRequested, bool RootExitConfirmed, int? ExitCode)
{
    public bool ContainmentEstablished { get; init; }
    public bool ContainedTreeExitConfirmed { get; init; }
}

/// <summary>One native client with UTF-8 pipes. On Windows its private job contains descendants from creation through cleanup.</summary>
public sealed class NativeProcess : IAsyncDisposable, INativeProcessIdentity
{
    private const int MaxStderr = 64 * 1024;
    private const int MaxCommandOutput = 4 * 1024 * 1024;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);
    private static long _nextGeneration;
    private readonly Process _process;
    private readonly WindowsNativeProcess? _contained;
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private readonly StreamReader _error;
    private readonly int _processId;
    private readonly object _lifetimeGate = new();
    private readonly StringBuilder _stderr = new();
    private readonly CancellationTokenSource _stopStderr = new();
    private readonly Task _stderrPump;
    private readonly CancellationTokenSource _stopInput = new();
    private Task _inputPump = Task.CompletedTask;
    private bool _inputDeliveryFailed;
    private Task<NativeProcessTermination>? _termination;
    private Task? _disposal;

    private NativeProcess(Process process, WindowsNativeProcess? contained = null)
    {
        _process = process;
        _contained = contained;
        _input = contained?.Input ?? process.StandardInput;
        _output = contained?.Output ?? process.StandardOutput;
        _error = contained?.Error ?? process.StandardError;
        _processId = process.Id;
        ProcessGeneration = Interlocked.Increment(ref _nextGeneration);
        if (ProcessGeneration <= 0) throw new InvalidOperationException("Native process generation exhausted.");
        _stderrPump = PumpStderrAsync();
        if (_contained is not null) _ = CleanupAfterRootExitAsync();
    }

    public int Id => _processId;
    /// <summary>Identity issued after actual OS creation by this process owner; distinct from PID and requested route.</summary>
    public long ProcessGeneration { get; }
    public bool HasExited => _process.HasExited;
    public StreamWriter Input => _input;

    public string StandardError
    {
        get { lock (_stderr) return _stderr.ToString(); }
    }

    public static NativeProcess Start(NativeLaunch launch, bool keepInputOpen = false)
        => StartCore(launch, keepInputOpen, suspended: false);

    /// <summary>On Windows, bind ownership/authorization while the created child is held inside its private job,
    /// before its code runs or prompt stdin is written. Failure terminates the owned child without resuming it.</summary>
    public static async Task<NativeProcess> StartAuthorizedAsync(NativeLaunch launch,
        Func<NativeProcess, CancellationToken, Task> beforeResume, CancellationToken cancellationToken,
        bool keepInputOpen = false)
    {
        ArgumentNullException.ThrowIfNull(beforeResume);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new GatewayException(GatewayErrorKind.Unsupported, "Приостановленный запуск с привязкой ownership доступен только на Windows.");
        var native = StartCore(launch, keepInputOpen: true, suspended: true);
        try
        {
            await beforeResume(native, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (native.HasExited) throw new InvalidOperationException("Native process exited before authorization completed.");
            native._contained!.Resume();
            native.BeginInput(launch.StandardInput, keepInputOpen);
            return native;
        }
        catch
        {
            await native.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static NativeProcess StartCore(NativeLaunch launch, bool keepInputOpen, bool suspended)
    {
        foreach (var (name, value) in launch.Environment)
            if (string.IsNullOrEmpty(name) || name.Contains('\0') || name.Contains('=') || value?.Contains('\0') == true)
                throw GatewayException.Invalid("Native process environment is invalid.");
        var arguments = launch.Target.PrefixArguments.Concat(launch.Arguments).ToList();
        if (launch.Target.Kind == LaunchKind.Script)
            throw new GatewayException(GatewayErrorKind.Unsupported,
                "Нераспознанная CLI-обёртка не поддерживается. Укажите исполняемый файл клиента или распознаваемый launcher.");
        var info = new ProcessStartInfo
        {
            FileName = launch.Target.FileName,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (key, value) in launch.Environment)
        {
            if (value is null) info.Environment.Remove(key);
            else info.Environment[key] = value;
        }
        info.Environment["NO_COLOR"] = "1";
        info.Environment["FORCE_COLOR"] = "0";

        Process? process = null;
        WindowsNativeProcess? contained = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                contained = WindowsNativeProcess.Start(info, suspended);
                process = contained.Process;
            }
            else
            {
                process = new Process { StartInfo = info, EnableRaisingEvents = true };
                if (!process.Start()) throw new GatewayException(GatewayErrorKind.ProviderUnavailable, "Не удалось запустить нативный клиент.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            contained?.Dispose();
            process?.Dispose();
            throw new GatewayException(GatewayErrorKind.ProviderUnavailable, "Не удалось запустить нативный клиент с контролем процессов.", ex);
        }

        NativeProcess native;
        try { native = new NativeProcess(process, contained); }
        catch
        {
            if (contained is not null) contained.Dispose();
            else { try { process.Kill(entireProcessTree: true); } finally { process.Dispose(); } }
            throw;
        }
        if (!suspended) native.BeginInput(launch.StandardInput, keepInputOpen);
        return native;
    }

    private void BeginInput(string? standardInput, bool keepInputOpen)
    {
        if (standardInput is { } input)
        {
            // Written in the background: a large prompt must not block while the child fills its stdout pipe.
            _inputPump = Task.Run(async () =>
            {
                try
                {
                    await Input.WriteAsync(input.AsMemory(), _stopInput.Token).ConfigureAwait(false);
                    await Input.FlushAsync(_stopInput.Token).ConfigureAwait(false);
                    if (!keepInputOpen) Input.Close();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                {
                    // Keep this owned task nonfaulting so cleanup always joins it before disposing pipes.
                    _inputDeliveryFailed = true;
                }
            });
        }
        else if (!keepInputOpen)
        {
            Input.Close();
        }
    }

    /// <summary>Runs a short status/model/version command, rejecting stdout beyond 4 Mi UTF-16 characters.</summary>
    public static async Task<NativeRunResult> RunAsync(NativeLaunch launch, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        await using var process = Start(launch);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var output = new StringBuilder();
        try
        {
            var buffer = new char[8192];
            int read;
            while ((read = await process._output.ReadAsync(buffer.AsMemory(), timeoutSource.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaxCommandOutput)
                    throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент превысил лимит вывода команды.");
                output.Append(buffer, 0, read);
            }
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new NativeRunResult(process._process.ExitCode, output.ToString(), process.StandardError, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NativeRunResult(-1, output.ToString(), process.StandardError, true);
        }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = new NativeBoundedLineReader(_output, MaxCommandOutput);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) yield break;
            if (line.Length > 0) yield return line;
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        // Natural root exit may leave a detached helper holding pipes or writing in the project.
        var termination = await StopAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!termination.RootExitConfirmed || _contained is not null && !termination.ContainedTreeExitConfirmed)
            throw new GatewayException(GatewayErrorKind.Upstream, "Завершение локального дерева нативного клиента не подтверждено.");
        // The root and its contained descendants have stopped; no owned reader can hold this pipe open.
        await _inputPump.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_process.ExitCode == 0 && _inputDeliveryFailed)
            throw new GatewayException(GatewayErrorKind.Upstream, "Передача входного текста нативному клиенту не завершена.");
        await _stderrPump.WaitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public void Kill()
    {
        try
        {
            if (_contained is not null) _contained.Kill();
            else if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    /// <summary>Bounded, shared cleanup independent of caller cancellation. Does not authorize releasing project ownership.</summary>
    public Task<NativeProcessTermination> StopAsync()
    {
        lock (_lifetimeGate) return _termination ??= StopCoreAsync();
    }

    private async Task<NativeProcessTermination> StopCoreAsync()
    {
        var killRequested = false;
        try
        {
            if (!_process.HasExited || _contained is not null && !_contained.IsEmpty)
            {
                killRequested = true;
                Kill();
            }
            using var deadline = new CancellationTokenSource(CleanupTimeout);
            await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            while (_contained is not null && !_contained.IsEmpty)
                await Task.Delay(20, deadline.Token).ConfigureAwait(false);
            return new(_processId, killRequested, true, _process.ExitCode)
            {
                ContainmentEstablished = _contained is not null,
                ContainedTreeExitConfirmed = _contained is not null
            };
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return new(_processId, killRequested, false, null) { ContainmentEstablished = _contained is not null };
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate) return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task CleanupAfterRootExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
        {
            // Dispose closes the job even when a process wait can no longer be observed.
        }
    }

    private async Task DisposeCoreAsync()
    {
        _stopInput.Cancel();
        var termination = await StopAsync().ConfigureAwait(false);
        await _inputPump.ConfigureAwait(false);
        _stopInput.Dispose();
        try
        {
            try { await _stderrPump.WaitAsync(CleanupTimeout).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        finally
        {
            // Descendants may retain inherited pipe handles after the root exits.
            _stopStderr.Cancel();
            await _stderrPump.ConfigureAwait(false);
            _stopStderr.Dispose();
            if (_contained is not null) _contained.Dispose();
            else _process.Dispose();
        }
        if (!termination.RootExitConfirmed || _contained is not null && !termination.ContainedTreeExitConfirmed)
            throw new GatewayException(GatewayErrorKind.Upstream, "Завершение локального дерева нативного клиента не подтверждено.");
    }

    private async Task PumpStderrAsync()
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await _error.ReadAsync(buffer.AsMemory(), _stopStderr.Token).ConfigureAwait(false);
                if (read == 0) break;
                lock (_stderr)
                {
                    if (_stderr.Length < MaxStderr) _stderr.Append(buffer, 0, Math.Min(read, MaxStderr - _stderr.Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
        }
    }

}
