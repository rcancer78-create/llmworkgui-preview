namespace LLMWorkGUI.Infrastructure.Storage;

/// <summary>Validates an isolated evidence path before directories or files are created.</summary>
public static class TemporaryEvidencePath
{
    public static string ValidateDescendant(string candidate, string root)
    {
        var fullPath = Path.GetFullPath(candidate);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(fullRoot, comparison))
            throw new ArgumentException("The evidence path must be inside its isolated root.", nameof(candidate));

        for (string? component = fullPath; component is not null; component = Path.GetDirectoryName(component))
        {
            try
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("An evidence path cannot traverse a filesystem link.", nameof(candidate));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return fullPath;
    }
}
