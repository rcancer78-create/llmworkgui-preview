namespace LLMWorkGUI.Infrastructure.Storage;

/// <summary>
/// Conservative path checks for retention. Checks existing ancestors as well as the leaf;
/// missing leaves are allowed for archive creation. This is not an atomic defense against
/// another process replacing directories between the check and a filesystem operation.
/// </summary>
internal static class RetentionFileSystem
{
    internal static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null;
             current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Retention cannot access a reparse point: " + current);
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static string[] ReadDirectory(string path)
    {
        EnsureNoReparsePoints(path);
        return Directory.GetFileSystemEntries(path, "*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        });
    }
}
