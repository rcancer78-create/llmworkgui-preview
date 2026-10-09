using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Reviews;

namespace LLMWorkGUI.Infrastructure.GrokBot;

public sealed class GrokBotReviewTransport : IGrokBotReviewTransport
{
    private readonly string _script;
    private readonly bool _disabled;
    public GrokBotReviewTransport() : this(Path.Combine(AppContext.BaseDirectory, "GrokBot", "review-bridge.mjs")) { _disabled = true; }
    internal GrokBotReviewTransport(string script) => _script = script;
    public async Task<bool> CheckSessionAsync(string nodeExecutable, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(nodeExecutable, new { action = "status" }, cancellationToken).ConfigureAwait(false);
        return result.GetProperty("ready").GetBoolean();
    }

    public async Task<(string Text, bool CleanupPending)> ReviewAsync(string nodeExecutable, string prompt, CancellationToken cancellationToken)
    {
        GrokBotPromptEnvelope.Validate(prompt);
        var result = await ExecuteAsync(nodeExecutable, new { prompt }, cancellationToken).ConfigureAwait(false);
        var text = result.GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Grok Bot вернул пустой ответ.");
        return (text, result.GetProperty("cleanupPending").GetBoolean());
    }

    public async Task<(string Text, bool CleanupPending)> ReviewAsync(string nodeExecutable, string prompt,
        DateTimeOffset notAfterUtc, CancellationToken cancellationToken)
    {
        GrokBotPromptEnvelope.Validate(prompt);
        if (DateTimeOffset.UtcNow >= notAfterUtc) throw new LLMWorkGUI.Application.Security.EgressApprovalException();
        var result = await ExecuteAsync(nodeExecutable, new { prompt, notAfterUtc = notAfterUtc.ToUniversalTime().ToString("O") },
            cancellationToken, notAfterUtc).ConfigureAwait(false);
        var text = result.GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Grok Bot вернул пустой ответ.");
        return (text, result.GetProperty("cleanupPending").GetBoolean());
    }

    private async Task<JsonElement> ExecuteAsync(string executable, object request, CancellationToken cancellationToken,
        DateTimeOffset? notAfterUtc = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disabled) throw new InvalidOperationException(LLMWorkGUI.Application.Providers.GrokBotRestrictions.Notice);
        var script = _script;
        if (!File.Exists(script)) throw new InvalidOperationException("Компонент файлового ревью Grok Bot не установлен.");
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new InvalidOperationException("Нужен подтверждённый полный путь к Node.js.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(script)!
        };
        start.ArgumentList.Add(script);
        // No caller-controlled runtime injection, including in synthetic transport tests.
        start.Environment.Remove("GROK_BOT_ROOT");
        start.Environment.Remove("NODE_OPTIONS");
        cancellationToken.ThrowIfCancellationRequested();
        if (notAfterUtc is { } expires && DateTimeOffset.UtcNow >= expires) throw new LLMWorkGUI.Application.Security.EgressApprovalException();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Node.js.");
        using var pipes = new CancellationTokenSource();
        var output = ReadBoundedAsync(process.StandardOutput, 4_000_000, pipes.Token);
        var error = DrainAsync(process.StandardError, pipes.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        try
        {
            if (notAfterUtc is { } expiry && DateTimeOffset.UtcNow >= expiry) throw new LLMWorkGUI.Application.Security.EgressApprovalException();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var raw = await output.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token).ConfigureAwait(false);
            await error.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(raw);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Grok Bot не завершил ревью. Проверьте вход и лимит; запрос автоматически не повторялся."
                    + (document.RootElement.TryGetProperty("cleanupPending", out var pending) && pending.ValueKind == JsonValueKind.True
                        ? " Очистка временного агента не подтверждена; проверьте Grok Bot." : string.Empty));
            return document.RootElement.Clone();
        }
        finally
        {
            var stopped = process.HasExited;
            try
            {
                if (!stopped)
                {
                    try
                    {
                        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        await process.StandardInput.WriteLineAsync("cancel".AsMemory(), grace.Token).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync(grace.Token).ConfigureAwait(false);
                        await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
                    if (!process.HasExited)
                    {
                        try
                        {
                            using var killDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync(killDeadline.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
                        {
                            // The child may have exited between HasExited and Kill. If it has not,
                            // report the explicit unconfirmed-stop outcome after draining below.
                        }
                    }
                    stopped = process.HasExited;
                }
            }
            finally
            {
                pipes.Cancel(); process.StandardOutput.Dispose(); process.StandardError.Dispose();
                var readers = Task.WhenAll(output, error);
                try { await readers.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception)
                {
                    if (!readers.IsCompleted) _ = readers.ContinueWith(t => { _ = t.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            if (!stopped) throw new InvalidOperationException("Остановка Grok Bot не подтверждена. Автоматический повтор запрещён; проверьте временный агент в Grok Bot.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; var oversized = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (text.Length + count > limit) oversized = true;
            else if (!oversized) text.Append(buffer, 0, count);
        }
        if (oversized) throw new InvalidOperationException("Ответ Grok Bot превышает допустимый размер.");
        return text.ToString();
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0) { }
    }
}
