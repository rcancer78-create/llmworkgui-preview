using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Security;

namespace LLMWorkGUI.Infrastructure.Workflows;

public sealed class WorkflowManifestParser : IWorkflowManifestParser
{
    private const int MaxManifestSizeBytes = 4 * 1024 * 1024;

    private static readonly string[] FormalManifestFileNames =
    {
        "workflow.json",
        "manifest.json"
    };

    private static readonly string[] PrimaryDocumentationNames =
    {
        "README.md",
        "SKILL.md",
        "WORKFLOW.md",
        "INSTRUCTIONS.md"
    };

    private static readonly byte[] Utf8Preamble = { 0xEF, 0xBB, 0xBF };
    private static readonly byte[] Utf16LittleEndianPreamble = { 0xFF, 0xFE };
    private static readonly byte[] Utf16BigEndianPreamble = { 0xFE, 0xFF };

    private static readonly HashSet<string> LegacyEntrypointFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "run.ps1",
        "run.bat",
        "run.cmd",
        "run.sh",
        "entrypoint.ps1",
        "entrypoint.bat",
        "entrypoint.cmd",
        "entrypoint.sh",
        "entrypoint.py",
        "main.ps1",
        "main.bat",
        "main.cmd",
        "main.sh",
        "main.py",
        "start.ps1",
        "start.bat",
        "start.cmd",
        "start.sh",
        "orchestrator.ps1",
        "orchestrator.sh",
        "orchestrator.py"
    };

    public async Task<WorkflowManifest> ParseManifestAsync(
        Stream archiveStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);

        try
        {
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);

            var manifestEntry = FindFormalManifestEntry(archive);

            if (manifestEntry is not null)
            {
                var json = await ReadManifestJsonAsync(manifestEntry, cancellationToken).ConfigureAwait(false);

                return ParseFormalManifest(json, manifestEntry.FullName);
            }

            return await DiscoverHeuristicallyAsync(archive, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new WorkflowValidationException(WorkflowValidationFailure.InvalidArchive, "Archive is not a valid ZIP archive.", exception);
        }
    }

    private static ZipArchiveEntry? FindFormalManifestEntry(ZipArchive archive)
    {
        foreach (var fileName in FormalManifestFileNames)
        {
            foreach (var entry in archive.Entries)
            {
                if (!IsDirectoryEntry(entry)
                    && string.Equals(entry.FullName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private static async Task<string> ReadManifestJsonAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length > MaxManifestSizeBytes)
        {
            throw new WorkflowValidationException("Workflow manifest exceeds the maximum allowed size.");
        }

        await using var stream = entry.Open();

        var buffer = ArrayPool<byte>.Shared.Rent(MaxManifestSizeBytes + 1);

        try
        {
            var total = 0;

            while (total <= MaxManifestSizeBytes)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(total, MaxManifestSizeBytes + 1 - total), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > MaxManifestSizeBytes)
            {
                throw new WorkflowValidationException("Workflow manifest exceeds the maximum allowed size.");
            }

            return DecodeText(buffer.AsSpan(0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static WorkflowManifest ParseFormalManifest(string json, string manifestPath)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new WorkflowValidationException(
                $"Workflow manifest '{manifestPath}' is not valid JSON.",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new WorkflowValidationException($"Workflow manifest '{manifestPath}' must be a JSON object.");
            }

            return new WorkflowManifest(
                HasFormalManifest: true,
                ManifestPath: manifestPath,
                WorkflowId: ReadString(root, "id", "workflowId", "workflow_id"),
                WorkflowVersion: ReadString(root, "version", "workflowVersion", "workflow_version"),
                Name: ReadString(root, "name", "displayName"),
                Entrypoints: ParseEntrypoints(root),
                DeclaredRoles: ParseDeclaredRoles(root),
                BindingsJson: ReadRawJson(root, "bindings", "roleBindings"),
                CreationMetadataJson: ReadRawJson(root, "creationMetadata", "metadata"));
        }
    }

    private static List<WorkflowEntrypointDescriptor> ParseEntrypoints(JsonElement root)
    {
        var entrypoints = new List<WorkflowEntrypointDescriptor>();
        var declaredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!TryGetProperty(root, "entrypoints", out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return entrypoints;
        }

        foreach (var element in property.EnumerateArray())
        {
            var path = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Object => ReadString(element, "path", "command", "file", "entry"),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                path = InputSanitizer.NormalizeArchiveEntryPath(path);
            }
            catch (PathTraversalException exception)
            {
                throw new WorkflowValidationException("Declared workflow entrypoint path is not allowed.", exception);
            }
            if (!declaredPaths.Add(path))
                throw new WorkflowValidationException("Declared workflow entrypoint paths collide.");

            entrypoints.Add(new WorkflowEntrypointDescriptor(
                path,
                ClassifyEntrypoint(path),
                IsDeclared: true));
        }

        return entrypoints;
    }

    private static List<WorkflowRole> ParseDeclaredRoles(JsonElement root)
    {
        var roles = new List<WorkflowRole>();

        if (!TryGetProperty(root, "declaredRoles", out var property)
            && !TryGetProperty(root, "roles", out property))
        {
            return roles;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return roles;
        }

        foreach (var element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = element.GetString();

            if (value is not null && TryParseRole(value, out var role) && !roles.Contains(role))
            {
                roles.Add(role);
            }
        }

        return roles;
    }

    private static List<WorkflowEntrypointDescriptor> DiscoverEntrypoints(ZipArchive archive)
    {
        var entrypoints = new List<WorkflowEntrypointDescriptor>();

        foreach (var entry in archive.Entries)
        {
            if (IsDirectoryEntry(entry) || !LegacyEntrypointFileNames.Contains(entry.Name))
            {
                continue;
            }

            var path = NormalizeEntryPath(entry.FullName);

            if (path is null)
            {
                continue;
            }

            entrypoints.Add(new WorkflowEntrypointDescriptor(
                path,
                ClassifyEntrypoint(path),
                IsDeclared: false));
        }

        entrypoints.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));

        return entrypoints;
    }

    private static async Task<WorkflowManifest> DiscoverHeuristicallyAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entrypoints = DiscoverEntrypoints(archive);
        var roles = await DiscoverRolesAsync(archive, cancellationToken).ConfigureAwait(false);

        return new WorkflowManifest(
            HasFormalManifest: false,
            ManifestPath: null,
            WorkflowId: null,
            WorkflowVersion: null,
            Name: null,
            Entrypoints: entrypoints,
            DeclaredRoles: roles,
            BindingsJson: null,
            CreationMetadataJson: null);
    }

    private static async Task<List<WorkflowRole>> DiscoverRolesAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var roles = new List<WorkflowRole>();
        var documentationEntry = FindPrimaryDocumentationEntry(archive);

        if (documentationEntry is null)
        {
            return roles;
        }

        var text = await ReadDocumentationTextAsync(documentationEntry, cancellationToken).ConfigureAwait(false);

        if (text is null)
        {
            return roles;
        }

        foreach (var role in Enum.GetValues<WorkflowRole>())
        {
            if (role == WorkflowRole.Unknown)
            {
                continue;
            }

            if (Regex.IsMatch(text, $@"\b{role}\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                roles.Add(role);
            }
        }

        return roles;
    }

    private static async Task<string?> ReadDocumentationTextAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(IWorkflowPreviewService.MaxPreviewSizeBytes);

        try
        {
            await using var stream = entry.Open();

            var total = 0;

            while (total < IWorkflowPreviewService.MaxPreviewSizeBytes)
            {
                var read = await stream
                    .ReadAsync(
                        buffer.AsMemory(total, IWorkflowPreviewService.MaxPreviewSizeBytes - total),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (buffer.AsSpan(0, Math.Min(total, 8192)).IndexOf((byte)0) >= 0)
            {
                return null;
            }

            return DecodeText(buffer.AsSpan(0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static ZipArchiveEntry? FindPrimaryDocumentationEntry(ZipArchive archive)
    {
        var rootMarkdown = new List<(string Name, ZipArchiveEntry Entry)>();

        foreach (var entry in archive.Entries)
        {
            if (IsDirectoryEntry(entry))
            {
                continue;
            }

            var path = NormalizeEntryPath(entry.FullName);

            if (path is null || path.Contains('/') || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rootMarkdown.Add((path, entry));
        }

        if (rootMarkdown.Count == 0)
        {
            return null;
        }

        foreach (var name in PrimaryDocumentationNames)
        {
            foreach (var (candidateName, entry) in rootMarkdown)
            {
                if (string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }

        return rootMarkdown
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Select(candidate => candidate.Entry)
            .First();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ReadRawJson(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var property)
                && property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return property.GetRawText();
            }
        }

        return null;
    }

    private static bool TryParseRole(string value, out WorkflowRole role)
    {
        foreach (var candidate in Enum.GetValues<WorkflowRole>())
        {
            if (candidate != WorkflowRole.Unknown
                && string.Equals(candidate.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        role = WorkflowRole.Unknown;
        return false;
    }

    private static WorkflowEntrypointKind ClassifyEntrypoint(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ps1" or ".psm1" => WorkflowEntrypointKind.PowerShell,
            ".bat" or ".cmd" => WorkflowEntrypointKind.Batch,
            ".sh" or ".bash" => WorkflowEntrypointKind.Shell,
            ".py" => WorkflowEntrypointKind.Python,
            _ => WorkflowEntrypointKind.Other
        };
    }

    private static string? NormalizeEntryPath(string entryName)
    {
        try
        {
            return InputSanitizer.NormalizeArchiveEntryPath(entryName);
        }
        catch (PathTraversalException)
        {
            return null;
        }
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    {
        return entry.FullName.EndsWith('/')
            || entry.FullName.EndsWith('\\')
            || entry.Name.Length == 0;
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8Preamble))
        {
            return Encoding.UTF8.GetString(bytes[Utf8Preamble.Length..]);
        }

        if (bytes.StartsWith(Utf16LittleEndianPreamble))
        {
            return Encoding.Unicode.GetString(bytes[Utf16LittleEndianPreamble.Length..]);
        }

        if (bytes.StartsWith(Utf16BigEndianPreamble))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[Utf16BigEndianPreamble.Length..]);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
