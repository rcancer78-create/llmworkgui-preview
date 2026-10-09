using System.Security.Cryptography;
using System.Text;

namespace LLMWorkGUI.Infrastructure.GrokBot;

/// <summary>Explicit file import/export for the existing request composer. Never follows model-supplied paths.</summary>
public static class ReviewTaskFiles
{
    public static async Task<(string Text, string Sha256)> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = ValidatePath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 200_000) throw new IOException("Задание превышает 200 KB.");
        using var memory = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + count > 200_000) throw new IOException("Задание превышает 200 KB.");
            memory.Write(buffer, 0, count);
        }
        var bytes = memory.ToArray();
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) throw new IOException("Нужен непустой текстовый файл UTF-8.");
        return (text, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static async Task SaveAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        path = ValidatePath(path);
        if (File.Exists(path)) throw new IOException("Файл ответа уже существует; выберите новое имя.");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".review-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePath(path);
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException("Укажите полный путь к файлу.");
        var full = Path.GetFullPath(path);
        if (full[Path.GetPathRoot(full)!.Length..].Contains(':')) throw new IOException("ADS не поддерживается.");
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Файлы и каталоги по ссылкам не поддерживаются.");
        return full;
    }
}
