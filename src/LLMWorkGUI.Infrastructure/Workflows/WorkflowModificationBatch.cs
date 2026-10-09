using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Security;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>Checks the final workspace, including overwrites, before the first mutation.</summary>
internal static class WorkflowModificationBatch
{
    public static IReadOnlyDictionary<string, string> Prepare(string root,
        IReadOnlyDictionary<string, string> modifications, CancellationToken token)
    {
        if (modifications.Count > WorkflowImportLimits.MaxFileCount)
            throw new WorkflowValidationException("Modification batch exceeds the file count limit.");
        var writes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in modifications)
        {
            token.ThrowIfCancellationRequested();
            var path = InputSanitizer.ResolveSafePath(root, pair.Key);
            InputSanitizer.EnsureNoReparsePoints(root, path);
            if (Directory.Exists(path) || !writes.TryAdd(path, pair.Value))
                throw new WorkflowValidationException("Modification paths collide.");
            if (Encoding.UTF8.GetByteCount(pair.Value) > WorkflowImportLimits.MaxSingleFileBytes)
                throw new WorkflowValidationException("Modification exceeds the per-file UTF-8 size limit.");
        }
        foreach (var path in writes.Keys)
            for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
                if (writes.ContainsKey(parent) || File.Exists(parent))
                    throw new WorkflowValidationException("Modification file and directory paths collide.");

        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        var directoryCount = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            InputSanitizer.EnsureNoReparsePoints(root, directory);
            if (++directoryCount > WorkflowImportLimits.MaxFileCount)
                throw new WorkflowValidationException("Workspace exceeds the directory count limit.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                InputSanitizer.EnsureNoReparsePoints(root, path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }
                if (!sizes.TryAdd(path, new FileInfo(path).Length) || sizes.Count > WorkflowImportLimits.MaxFileCount)
                    throw new WorkflowValidationException("Workspace exceeds the file count limit or has colliding paths.");
            }
        }
        foreach (var write in writes) sizes[write.Key] = Encoding.UTF8.GetByteCount(write.Value);
        if (sizes.Count > WorkflowImportLimits.MaxFileCount
            || sizes.Values.Any(length => length > WorkflowImportLimits.MaxSingleFileBytes)
            || sizes.Values.Sum() > WorkflowImportLimits.MaxUncompressedTotalBytes)
            throw new WorkflowValidationException("Modified workspace exceeds its size or file count limits.");
        return writes;
    }
}
