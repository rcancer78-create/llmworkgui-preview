namespace LLMWorkGUI.Infrastructure.Storage;

internal static class AtomicFile
{
    public static string CreateTempPath(string directory)
    {
        return Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="destinationPath"/> so that a reader ever
    /// only sees the previous or the new content, never a partial file. With
    /// <paramref name="overwrite"/> the existing file is replaced atomically, which is what an
    /// in-place secret rotation needs: deleting first would expose a window in which the reference
    /// resolves to nothing.
    /// </summary>
    public static async Task WriteAllBytesAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken,
        bool overwrite = false)
    {
        var directory = Path.GetDirectoryName(destinationPath);

        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Destination path must include a directory.", nameof(destinationPath));
        }

        Directory.CreateDirectory(directory);

        var tempPath = CreateTempPath(directory);

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, destinationPath, overwrite);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
