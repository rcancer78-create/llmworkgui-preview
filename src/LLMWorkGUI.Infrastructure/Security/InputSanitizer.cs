namespace LLMWorkGUI.Infrastructure.Security;

public static class InputSanitizer
{
    public static string CanonicalizeBaseDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
    }

    public static string NormalizeArchiveEntryPath(string entryName)
    {
        ArgumentNullException.ThrowIfNull(entryName);
        EnsureNoControlCharacters(entryName);

        if (string.IsNullOrWhiteSpace(entryName))
        {
            throw new PathTraversalException("Archive entry path must not be empty.");
        }

        var normalized = entryName.Replace('\\', '/');

        if (normalized.Contains(':'))
        {
            throw new PathTraversalException("Archive entry path must not contain drive qualifiers or alternate data streams.");
        }

        if (Path.IsPathRooted(normalized))
        {
            throw new PathTraversalException("Archive entry path must be relative.");
        }

        var segments = new List<string>();

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new PathTraversalException("Archive entry path must not contain parent directory segments.");
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw new PathTraversalException("Archive entry path segments must not end with a space or a dot.");
            }

            EnsureNotPercentEncodedTraversal(segment);
            var deviceName = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (deviceName is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (deviceName.Length == 4 && (deviceName.StartsWith("COM", StringComparison.Ordinal)
                    || deviceName.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".Contains(deviceName[3])))
                throw new PathTraversalException("Archive entry path contains a reserved device name.");
            if (segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
                throw new PathTraversalException("Archive entry path contains a non-portable filename character.");
            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new PathTraversalException("Archive entry path does not contain any file or directory name.");
        }

        return string.Join('/', segments);
    }

    public static string ResolveSafePath(string baseDirectory, string untrustedRelativePath)
    {
        ArgumentNullException.ThrowIfNull(untrustedRelativePath);

        var canonicalBase = CanonicalizeBaseDirectory(baseDirectory);
        EnsureNoControlCharacters(untrustedRelativePath);

        if (string.IsNullOrWhiteSpace(untrustedRelativePath))
        {
            throw new PathTraversalException("Path must not be empty.");
        }

        if (untrustedRelativePath.Contains(':'))
        {
            throw new PathTraversalException("Path must not contain drive qualifiers or alternate data streams.");
        }

        if (Path.IsPathRooted(untrustedRelativePath))
        {
            throw new PathTraversalException("Path must be relative to the base directory.");
        }

        var relativePath = NormalizeArchiveEntryPath(untrustedRelativePath);
        var combined = Path.GetFullPath(
            Path.Combine(canonicalBase, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsStrictlyInside(canonicalBase, combined))
        {
            throw new PathTraversalException("Resolved path escapes the base directory.");
        }

        return combined;
    }

    public static bool TryResolveSafePath(
        string baseDirectory,
        string? untrustedRelativePath,
        out string resolvedPath)
    {
        if (untrustedRelativePath is null)
        {
            resolvedPath = string.Empty;
            return false;
        }

        try
        {
            resolvedPath = ResolveSafePath(baseDirectory, untrustedRelativePath);
            return true;
        }
        catch (PathTraversalException)
        {
            resolvedPath = string.Empty;
            return false;
        }
    }

    public static bool IsPathInsideBase(string baseDirectory, string? candidateRelativePath)
    {
        return TryResolveSafePath(baseDirectory, candidateRelativePath, out _);
    }

    public static void EnsureNoReparsePoints(string baseDirectory, string resolvedPath)
    {
        var canonicalBase = CanonicalizeBaseDirectory(baseDirectory);
        if (!IsSamePath(canonicalBase, resolvedPath) && !IsStrictlyInside(canonicalBase, Path.GetFullPath(resolvedPath)))
            throw new PathTraversalException("Path is outside the import tree.");
        var current = resolvedPath;

        while (!string.IsNullOrEmpty(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { attributes = 0; }
            catch (DirectoryNotFoundException) { attributes = 0; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PathTraversalException(
                    $"Reparse points are not allowed inside the import tree: '{current}'.");
            }

            if (IsSamePath(current, canonicalBase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static bool IsStrictlyInside(string canonicalBase, string fullPath)
    {
        if (IsSamePath(fullPath, canonicalBase))
        {
            return false;
        }

        var prefix = canonicalBase.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalBase
            : canonicalBase + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, PathComparison);
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            PathComparison);
    }

    private static void EnsureNoControlCharacters(string value)
    {
        foreach (var character in value)
        {
            if (character == '\0' || char.IsControl(character))
            {
                throw new PathTraversalException("Paths must not contain control characters.");
            }
        }
    }

    private static void EnsureNotPercentEncodedTraversal(string segment)
    {
        var decoded = Uri.UnescapeDataString(segment);

        if (string.Equals(decoded, segment, StringComparison.Ordinal))
        {
            return;
        }

        if (decoded.Contains("..", StringComparison.Ordinal)
            || decoded.Contains('/')
            || decoded.Contains('\\')
            || decoded.Contains(':'))
        {
            throw new PathTraversalException("Archive entry path contains an encoded path traversal sequence.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
