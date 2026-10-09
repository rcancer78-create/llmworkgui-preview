using System.Diagnostics;
using System.Text.Json;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Security;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>Durable ownership, written before scratch creation. Only dead owners are recovered.</summary>
internal sealed class AdaptationScratchOwnership(string appDataDirectory)
{
    private readonly string _root = Path.GetFullPath(appDataDirectory);
    private string RecordsDirectory => Path.Combine(_root, "adaptation-scratch-owners");

    internal sealed record Owner(int Version, string DirectoryName, int ProcessId, long StartTimeUtcTicks);

    public void Register(string workspacePath)
    {
        var name = Path.GetFileName(workspacePath);
        var recordPath = RecordPath(name);
        InputSanitizer.EnsureNoReparsePoints(_root, recordPath);
        Directory.CreateDirectory(RecordsDirectory);
        using var process = Process.GetCurrentProcess();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Owner(1, name, process.Id, process.StartTime.ToUniversalTime().Ticks));
        using var stream = new FileStream(recordPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public void Complete(string workspacePath)
    {
        var path = RecordPath(Path.GetFileName(workspacePath));
        try { InputSanitizer.EnsureNoReparsePoints(_root, path); }
        catch (PathTraversalException error) { throw new IOException("Scratch ownership cleanup refused an unsafe path.", error); }
        File.Delete(path);
    }

    public void ValidateCleanup(string workspacePath)
    {
        try { ValidateCleanupCore(workspacePath); }
        catch (PathTraversalException error) { throw new IOException("Scratch cleanup refused an unsafe path.", error); }
    }

    private void ValidateCleanupCore(string workspacePath)
    {
        var expected = ResolveWorkspace(Path.GetFileName(workspacePath));
        if (!string.Equals(Path.GetFullPath(workspacePath), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Scratch ownership path mismatch.");
        InputSanitizer.EnsureNoReparsePoints(_root, expected);
        // Check every child without following a link. Untrusted scratch content cannot widen cleanup.
        if (Directory.Exists(expected)) ValidateChildren(expected);
    }

    private void ValidateChildren(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                InputSanitizer.EnsureNoReparsePoints(_root, path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
    }

    public int Recover(CancellationToken cancellationToken)
    {
        InputSanitizer.EnsureNoReparsePoints(_root, RecordsDirectory);
        if (!Directory.Exists(RecordsDirectory)) return 0;
        var pending = 0;
        foreach (var recordPath in Directory.EnumerateFiles(RecordsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                InputSanitizer.EnsureNoReparsePoints(_root, recordPath);
                Owner owner;
                using (var stream = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > 4096) throw new IOException("Invalid scratch ownership record.");
                    owner = JsonSerializer.Deserialize<Owner>(stream) ?? throw new IOException("Invalid scratch ownership record.");
                }
                if (owner.Version != 1 || owner.ProcessId <= 0 || owner.StartTimeUtcTicks <= 0 ||
                    !string.Equals(RecordPath(owner.DirectoryName), recordPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Invalid scratch ownership record.");
                if (!OwnerHasExited(owner)) continue;
                var workspacePath = ResolveWorkspace(owner.DirectoryName);
                var workspace = new ScratchWorkspace(workspacePath, ScratchScope.Adaptation, "recovery",
                    () => ValidateCleanup(workspacePath), () => Complete(workspacePath));
                workspace.CleanupWorkspaceAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or JsonException or ArgumentException or PathTraversalException)
            {
                // Keep the exact record for retry. Never log its payload or infer authority from age.
                pending++;
            }
        }
        return pending;
    }

    private static bool OwnerHasExited(Owner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return process.HasExited || process.StartTime.ToUniversalTime().Ticks != owner.StartTimeUtcTicks;
        }
        catch (ArgumentException) { return true; } // PID no longer exists.
        catch (InvalidOperationException) { return true; } // Process exited during inspection.
        catch (System.ComponentModel.Win32Exception) { return false; } // Unknown is not dead.
    }

    private string ResolveWorkspace(string name) => InputSanitizer.ResolveSafePath(
        Path.Combine(_root, "scratch", "adaptation"), ValidateName(name));

    private string RecordPath(string name) => Path.Combine(RecordsDirectory, ValidateName(name)[^32..] + ".json");

    private static string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length is < 34 or > 97 || name[^33] != '-' ||
            !Guid.TryParseExact(name[^32..], "N", out _) ||
            name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new IOException("Invalid scratch workspace name.");
        return name;
    }

    public static bool IsManaged(string dataRoot, string workspacePath)
    {
        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(workspacePath)), "adaptation", StringComparison.OrdinalIgnoreCase))
            return false;
        var registry = new AdaptationScratchOwnership(dataRoot);
        string recordPath;
        try { recordPath = registry.RecordPath(Path.GetFileName(workspacePath)); }
        catch (IOException) { return false; } // Legacy unregistered naming remains under retention.
        try
        {
            InputSanitizer.EnsureNoReparsePoints(registry._root, recordPath);
            _ = File.GetAttributes(recordPath);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PathTraversalException or System.Security.SecurityException)
        { return true; } // Refuse retention deletion on ambiguous ownership.
    }
}
