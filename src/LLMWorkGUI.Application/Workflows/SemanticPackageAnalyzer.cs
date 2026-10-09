using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Extracts the bounded, explainable semantic facts of a real workflow package directory so the ТЗ §6.14
/// guard can be decided from the packages themselves instead of from the model-declared
/// <c>isSemanticChange</c> flag.
/// <para>
/// Two tiers are compared. The <b>structured</b> tier is <i>every</i> root formal manifest
/// (<c>workflow.json</c> and <c>manifest.json</c>, each read and reported on its own because the
/// activation parser prefers <c>workflow.json</c>): declared roles, entrypoints, and the manifest with
/// only the routing leaves of its <i>top-level</i> <c>bindings</c>/<c>roleBindings</c> map removed are
/// compared exactly, so a pure rebinding stays equal while a rewritten stage list, quality gate,
/// escalation rule or role policy does not — including one that is nested under a binding entry, or parked
/// anywhere below <c>metadata</c> or <c>creationMetadata</c>, which are never routing context.
/// The <b>prose</b> tier is every semantic-bearing document, which carries stage, quality-gate and
/// escalation semantics as text. Prose cannot be machine-verified, so a changed digest is reported and the
/// guard fails closed until the operator acknowledges exactly that change.
/// </para>
/// Analysis is bounded by <see cref="MaxEnumeratedEntries"/>, <see cref="MaxAnalyzedFiles"/>,
/// <see cref="MaxAnalyzedFileBytes"/> and <see cref="MaxAnalyzedTotalBytes"/>. A bounded sample of known
/// dropped paths and a global finding for all uninspected content are reported as
/// <see cref="SemanticChangeKind.UnverifiableContent"/> instead of being silently trusted, and those
/// findings are never clearable by an operator decision. No file content is ever
/// retained: a document contributes only its path and SHA-256 digest.
/// </summary>
public static class SemanticPackageAnalyzer
{
    /// <summary>Upper bound on the number of files inspected in one package.</summary>
    public const int MaxAnalyzedFiles = 512;

    /// <summary>Upper bound on the size of one inspected file. Larger semantic files are unverifiable.</summary>
    public const int MaxAnalyzedFileBytes = 1024 * 1024;

    /// <summary>Upper bound on the total number of bytes read from one package.</summary>
    public const int MaxAnalyzedTotalBytes = 8 * 1024 * 1024;

    /// <summary>Upper bound on manifest nesting depth. Deeper documents are unverifiable.</summary>
    public const int MaxManifestDepth = 32;

    /// <summary>Upper bound on the number of declared role and entrypoint names kept from a manifest.</summary>
    public const int MaxDeclaredNames = 64;

    /// <summary>
    /// Upper bound on the number of dropped-by-budget paths that are named individually. The package-level
    /// unverifiable finding is always reported, so a package that exceeds even this stays inconclusive.
    /// </summary>
    public const int MaxReportedUncoveredFiles = 64;

    /// <summary>Bounds inspected entries, including directories and the uncovered-path sample. One extra entry may be observed to prove truncation.</summary>
    public const int MaxEnumeratedEntries = MaxAnalyzedFiles + MaxReportedUncoveredFiles + 1;

    private const int BinarySniffSizeBytes = 8192;

    private static readonly string[] ManifestFileNames = ["workflow.json", "manifest.json"];

    /// <summary>
    /// The only manifest keys the execution parser reads as the model-routing map, and therefore the only
    /// keys that may contribute routing context: <c>ReadRawJson(root, "bindings", "roleBindings")</c> in
    /// <c>WorkflowManifestParser</c> looks them up on the root object only. <c>metadata</c> and
    /// <c>creationMetadata</c> are provenance and semantic data, never routing: the parser reads them as
    /// creation metadata, so a rewrite of anything below them is compared, including an object that is
    /// itself named <c>bindings</c> or <c>roleBindings</c>.
    /// </summary>
    private static readonly HashSet<string> BindingContainerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bindings",
        "roleBindings"
    };

    /// <summary>
    /// The exact, known routing leaf names. A leaf is ignored only when its value is a scalar <b>and</b> it
    /// sits on the routing surface itself; the same name anywhere else is an ordinary compared property,
    /// because an ignore list applied by name at arbitrary depth turns every nested stage, quality-gate,
    /// escalation or role policy into a silent allow.
    /// </summary>
    private static readonly HashSet<string> RoutingLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "account",
        "accountId",
        "adapterModelId",
        "adapterRouteId",
        "agent",
        "agentId",
        "backend",
        "backendId",
        "capabilities",
        "channel",
        "channelId",
        "createdAt",
        "createdAtUtc",
        "createdBy",
        "fallback",
        "fallbackModelId",
        "fallbackRouteId",
        "generatedAtUtc",
        "generator",
        "lastModifiedAtUtc",
        "model",
        "modelId",
        "modelName",
        "original",
        "originalModelId",
        "originalRoute",
        "originalRouteId",
        "profile",
        "profileId",
        "provider",
        "providerId",
        "reasoningEffort",
        "route",
        "routeId",
        "sessionId",
        "source",
        "sourceModelId",
        "sourceRoute",
        "sourceRouteId",
        "sourceVersionId",
        "target",
        "targetModel",
        "targetModelId",
        "targetRoute",
        "targetRouteId",
        "toolVersion"
    };

    /// <summary>
    /// Manifest keys whose contents are compared exactly as declared names. They leave the structural form
    /// only in the plain name-array shape the manifest parser actually reads; any other shape stays in the
    /// comparison so a policy cannot be parked under a declared-name key.
    /// </summary>
    private static readonly HashSet<string> DeclaredNameManifestKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "declaredRoles",
        "roles",
        "entrypoints"
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md",
        ".markdown",
        ".mdx"
    };

    private static readonly HashSet<string> SemanticDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompts",
        "prompt",
        "instructions",
        "stages",
        "stage",
        "steps",
        "skills",
        "agents",
        "roles",
        "docs",
        "documentation",
        "workflow",
        "workflows",
        "quality",
        "gates",
        "escalation",
        "policies"
    };

    private static readonly HashSet<string> SemanticDocumentStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "readme",
        "skill",
        "workflow",
        "instructions",
        "agents",
        "claude",
        "process",
        "stages",
        "quality",
        "escalation"
    };

    /// <summary>
    /// Reads the semantic facts of the package rooted at <paramref name="directoryPath"/>. Returns
    /// <see cref="SemanticPackageFacts.Empty"/> when the directory cannot be inspected, which the
    /// comparison treats as an unavailable baseline rather than as an empty package.
    /// </summary>
    public static SemanticPackageFacts Read(string directoryPath)
        => Read(directoryPath, (path, options) => new DirectoryInfo(path).EnumerateFileSystemInfos("*", options));

    internal static SemanticPackageFacts Read(string directoryPath,
        Func<string, EnumerationOptions, IEnumerable<FileSystemInfo>> enumerateEntries)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return SemanticPackageFacts.Empty;
        }

        // Enumeration options do not protect a root (or its ancestor) that is itself a link.
        if (!HasInspectableAncestry(directoryPath))
            return SemanticPackageFacts.Empty;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        var files = new List<(string RelativePath, string FullPath)>();
        var unverifiable = new List<string>();
        var directories = new Stack<string>();
        directories.Push(Path.GetFullPath(directoryPath));
        var visited = 0;
        var enumerationTruncated = false;
        var inspectionSucceeded = true;

        while (directories.Count > 0 && !enumerationTruncated)
        {
            var directory = directories.Pop();
            var relativeDirectory = NormalizeRelativePath(Path.GetRelativePath(directoryPath, directory));
            try
            {
                // Recheck queued directories before entering them; never intentionally traverse a link.
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    unverifiable.Add(relativeDirectory);
                    continue;
                }
                foreach (var entry in enumerateEntries(directory, options))
                {
                    if (visited == MaxEnumeratedEntries)
                    {
                        // A completely enumerated package at the exact limit is still complete.
                        // Observe one further entry without inspecting it to distinguish an actual tail.
                        enumerationTruncated = true;
                        break;
                    }
                    var fullPath = entry.FullName;
                    var relativePath = NormalizeRelativePath(Path.GetRelativePath(directoryPath, fullPath));
                    visited++;
                    try
                    {
                        var attributes = entry.Attributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            unverifiable.Add(relativePath);
                        else if ((attributes & FileAttributes.Directory) != 0)
                            directories.Push(fullPath);
                        else
                            files.Add((relativePath, fullPath));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        unverifiable.Add(relativePath);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unverifiable.Add(relativeDirectory);
                if (relativeDirectory == ".") inspectionSucceeded = false;
            }
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

        var truncated = enumerationTruncated || files.Count > MaxAnalyzedFiles;
        var uncovered = new List<string>();

        if (truncated)
        {
            // Sort only the bounded sample. Retain the known tail paths; the unenumerated remainder
            // is covered by the global non-clearable truncation finding.
            foreach (var (relativePath, _) in files.Skip(MaxAnalyzedFiles))
            {
                if (IsSemanticBearingFile(relativePath))
                {
                    uncovered.Add(relativePath);
                }
            }

            files = files.Take(MaxAnalyzedFiles).ToList();
        }

        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalBytes = 0;

        foreach (var (relativePath, fullPath) in files)
        {
            if (IsRootManifestPath(relativePath))
            {
                continue;
            }

            if (!IsSemanticBearingFile(relativePath))
            {
                continue;
            }

            if (!TryReadAnalyzableFile(fullPath, ref totalBytes, out var content))
            {
                unverifiable.Add(relativePath);
                continue;
            }

            digests[relativePath] = Convert
                .ToHexString(SHA256.HashData(content))
                .ToLowerInvariant();
        }

        var manifests = new List<SemanticManifestFacts>();
        foreach (var file in files.Where(file => IsRootManifestPath(file.RelativePath))
                     .OrderBy(file => file.RelativePath, StringComparer.Ordinal))
            manifests.Add(ReadManifest(file.RelativePath, file.FullPath, ref totalBytes));

        return new SemanticPackageFacts
        {
            InspectionSucceeded = inspectionSucceeded,
            Manifests = manifests,
            DocumentDigests = digests,
            UnverifiableFiles = unverifiable
                .Concat(uncovered.Take(MaxReportedUncoveredFiles))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            AnalysisTruncated = truncated
        };
    }

    private static bool HasInspectableAncestry(string directoryPath)
    {
        try
        {
            for (DirectoryInfo? directory = new(Path.GetFullPath(directoryPath)); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Explains whether a package-relative path carries the stage, quality-gate, escalation or role
    /// semantics that ТЗ §6.14 protects. Scripts, configuration, assets and binaries are deliberately not
    /// semantic-bearing: adaptation is explicitly allowed to rewrite launch commands and config artifacts.
    /// </summary>
    public static bool IsSemanticBearingFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = NormalizeRelativePath(relativePath);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return false;
        }

        if (MarkdownExtensions.Contains(Path.GetExtension(normalized)))
        {
            return true;
        }

        foreach (var segment in segments.Take(segments.Length - 1))
        {
            if (SemanticDirectoryNames.Contains(segment))
            {
                return true;
            }
        }

        return segments.Length == 1 && SemanticDocumentStems.Contains(Path.GetFileNameWithoutExtension(normalized));
    }

    private static bool IsRootManifestPath(string relativePath)
    {
        foreach (var fileName in ManifestFileNames)
        {
            if (string.Equals(relativePath, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadAnalyzableFile(string fullPath, ref int alreadyReadBytes, out byte[] content)
    {
        content = Array.Empty<byte>();

        try
        {
            if (!HasInspectableAncestry(fullPath)) return false;
            // On Windows a read-only share prevents replacement, deletion and concurrent writers
            // while this handle is open. Inspect the opened object and bound actual reads, rather
            // than allocating from a path's earlier, potentially stale FileInfo metadata.
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if ((File.GetAttributes(stream.SafeFileHandle) & FileAttributes.ReparsePoint) != 0
                || !OpenedPathMatches(stream, fullPath)) return false;
            var length = stream.Length;
            var remaining = Math.Min(MaxAnalyzedFileBytes, MaxAnalyzedTotalBytes - alreadyReadBytes);
            if (length > remaining) return false;
            var bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                alreadyReadBytes += read;
                if (read == 0) return false;
                offset += read;
            }
            if (stream.Length != length) return false;

            if (ContainsNullByte(bytes.AsSpan(0, Math.Min(bytes.Length, BinarySniffSizeBytes))))
            {
                return false;
            }

            content = bytes;

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SemanticManifestFacts ReadManifest(string relativePath, string fullPath, ref int alreadyReadBytes)
    {
        if (!TryReadAnalyzableFile(fullPath, ref alreadyReadBytes, out var bytes))
        {
            return new SemanticManifestFacts(relativePath, readable: false, structureForm: null);
        }

        try
        {
            var text = StripPreamble(bytes);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                MaxDepth = MaxManifestDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new SemanticManifestFacts(relativePath, readable: false, structureForm: null);
            }

            // Declared names are read for their own bounded comparison; the canonical form below no longer
            // depends on them, so nothing here has to precede the structural rendering.
            var declaredRoles = ReadDeclaredNames(
                document.RootElement,
                out var declaredRolesTruncated,
                "declaredRoles",
                "roles");
            var entrypoints = ReadDeclaredNames(
                document.RootElement,
                out var entrypointsTruncated,
                "entrypoints");

            return new SemanticManifestFacts(
                relativePath,
                readable: true,
                Canonicalize(document.RootElement),
                declaredRoles,
                entrypoints,
                declaredRolesTruncated,
                entrypointsTruncated);
        }
        catch (JsonException)
        {
            return new SemanticManifestFacts(relativePath, readable: false, structureForm: null);
        }
        catch (DecoderFallbackException)
        {
            return new SemanticManifestFacts(relativePath, readable: false, structureForm: null);
        }
    }

    private static bool OpenedPathMatches(FileStream stream, string requestedPath)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return false;
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        return string.Equals(actual, Path.GetFullPath(requestedPath), StringComparison.OrdinalIgnoreCase);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        StringBuilder path, uint length, uint flags);

    /// <summary>
    /// Reads the declared names of the given manifest keys, de-duplicated and bounded to
    /// <see cref="MaxDeclaredNames"/>. <paramref name="truncated"/> reports that the raw array held more
    /// entries than the cap before de-duplication, so the returned prefix is <b>not</b> the whole list:
    /// the manifest parser reads every entry, so a name, duplicate or case variant past the cap would
    /// otherwise be invisible to the comparison.
    /// </summary>
    private static IReadOnlyList<string> ReadDeclaredNames(
        JsonElement root,
        out bool truncated,
        params string[] keys)
    {
        var names = new List<string>();
        var walked = 0;

        foreach (var key in keys)
        {
            if (!TryGetPropertyIgnoreCase(root, key, out var element)
                || element.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in element.EnumerateArray())
            {
                var name = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object => ReadEntrypointPath(item),
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Counted before de-duplication, exactly as the manifest parser walks the array, so a
                // duplicated or case-variant entry past the cap still counts towards the cap.
                walked++;

                var trimmed = name.Trim();

                if (!names.Contains(trimmed, StringComparer.Ordinal))
                {
                    names.Add(trimmed);
                }
            }
        }

        truncated = walked > MaxDeclaredNames;

        return names.Count == 0
            ? Array.Empty<string>()
            : names.OrderBy(name => name, StringComparer.Ordinal).Take(MaxDeclaredNames).ToArray();
    }

    private static string? ReadEntrypointPath(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && (string.Equals(property.Name, "path", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "command", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "file", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "entry", StringComparison.OrdinalIgnoreCase)))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Renders the manifest deterministically with object keys ordinal-sorted, so two packages that differ
    /// only by key order or by a pure model rebinding compare equal while every other structural
    /// difference survives as text.
    /// </summary>
    private static string Canonicalize(JsonElement element)
    {
        var builder = new StringBuilder();

        WriteCanonical(element, builder, depth: 0, RoutingPosition.None);

        return builder.ToString();
    }

    /// <summary>
    /// Where in the manifest the value being rendered sits, as a <b>position</b> rather than as a name. The
    /// routing surface is exactly the top-level <c>bindings</c>/<c>roleBindings</c> map, so the ignores are
    /// applied by position and never by name at arbitrary depth:
    /// <list type="bullet">
    /// <item><see cref="None"/> — everything else, including every nested policy. Nothing routing-related
    /// is ignored, so a scalar named <c>modelId</c>, <c>agent</c>, <c>source</c> or <c>target</c> is
    /// compared like any other.</item>
    /// <item><see cref="InBindingContainer"/> — the direct properties of the routing map itself, where a
    /// routing-leaf scalar is a route and a role-named scalar is the legacy
    /// <c>"Executor": "model-id"</c> shape.</item>
    /// <item><see cref="InBindingEntry"/> — the direct properties of one entry of that map, where a
    /// routing-leaf scalar (<c>modelId</c>, <c>targetModelId</c>, …) is still a route and nothing else is.</item>
    /// </list>
    /// </summary>
    private enum RoutingPosition
    {
        None,
        InBindingContainer,
        InBindingEntry
    }

    private static void WriteCanonical(
        JsonElement element,
        StringBuilder builder,
        int depth,
        RoutingPosition position)
    {
        if (depth > MaxManifestDepth)
        {
            builder.Append("\"<too-deep>\"");

            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');

                var first = true;

                foreach (var property in element.EnumerateObject()
                             .Where(property => !IsIgnoredProperty(property, depth, position))
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (depth == 0
                        && BindingContainerNames.Contains(property.Name)
                        && !HasSemanticContent(property.Value))
                    {
                        // A routing map that held nothing but routes compares equal to an absent one, so a
                        // rebinding cannot fabricate a structural difference.
                        continue;
                    }

                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;

                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    WriteCanonical(
                        property.Value,
                        builder,
                        depth + 1,
                        ChildPosition(depth, position, property.Name, property.Value));
                }

                builder.Append('}');

                break;

            case JsonValueKind.Array:
                builder.Append('[');

                var firstItem = true;

                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;

                    WriteCanonical(item, builder, depth + 1, ItemPosition(position));
                }

                builder.Append(']');

                break;

            default:
                builder.Append(element.GetRawText());

                break;
        }
    }

    /// <summary>
    /// The position a child value is rendered at. The routing surface is entered only by a top-level
    /// <c>bindings</c>/<c>roleBindings</c> key, which is the only shape the execution parser reads as
    /// bindings, and only an entry of that map narrows further to a routing-leaf-only ignore.
    /// </summary>
    private static RoutingPosition ChildPosition(
        int depth,
        RoutingPosition position,
        string propertyName,
        JsonElement value) => position switch
        {
            RoutingPosition.InBindingContainer => value.ValueKind == JsonValueKind.Object
                ? RoutingPosition.InBindingEntry
                : RoutingPosition.None,
            RoutingPosition.InBindingEntry => RoutingPosition.None,
            _ => depth == 0 && BindingContainerNames.Contains(propertyName)
                ? RoutingPosition.InBindingContainer
                : RoutingPosition.None
        };

    /// <summary>
    /// The position an array item is rendered at. An item of the routing map itself is one binding entry;
    /// every other array is ordinary nested content and is compared in full.
    /// </summary>
    private static RoutingPosition ItemPosition(RoutingPosition position) =>
        position == RoutingPosition.InBindingContainer
            ? RoutingPosition.InBindingEntry
            : RoutingPosition.None;

    private static bool IsIgnoredProperty(
        JsonProperty property,
        int depth,
        RoutingPosition position)
    {
        if (position != RoutingPosition.None && IsRoutingLeaf(property, position))
        {
            return true;
        }

        return depth == 0
            && DeclaredNameManifestKeys.Contains(property.Name)
            && IsPlainNameArray(property.Value);
    }

    /// <summary>
    /// A routing leaf is ignored only when its value is a scalar <b>and</b> it sits on the routing surface
    /// or on a direct entry of it. A role-named scalar is the legacy <c>"Executor": "model-id"</c> binding
    /// shape only at that direct level, so a role claim anywhere else is compared. An object or an array
    /// under a routing key is descended into, which is what makes a nested stage, quality-gate, escalation
    /// or role policy visible again.
    /// </summary>
    private static bool IsRoutingLeaf(JsonProperty property, RoutingPosition position)
    {
        if (!IsScalar(property.Value))
        {
            return false;
        }

        if (RoutingLeafNames.Contains(property.Name))
        {
            return true;
        }

        return position == RoutingPosition.InBindingContainer && IsRoleName(property.Name);
    }

    /// <summary>
    /// Whether a binding container carries anything besides routes: an object whose members are all routes
    /// and an empty array compare equal to an absent container. A scalar is <b>never</b> omitted, because a
    /// <c>metadata</c>, <c>creationMetadata</c>, <c>bindings</c> or <c>roleBindings</c> string is a claim in
    /// prose form, and dropping it would let a rewrite hide in it.
    /// </summary>
    private static bool HasSemanticContent(JsonElement element) =>
        HasSemanticContent(element, RoutingPosition.InBindingContainer);

    /// <summary>
    /// The same question asked recursively, because a binding entry may itself be an object:
    /// <c>"bindings":{"Executor":{"modelId":"acct-1"}}</c> is a rebinding written in the richer shape, not a
    /// policy, so it has to compare equal to the legacy <c>"Executor":"acct-1"</c> scalar and to an absent
    /// container. Recursion stops one entry deep, exactly like the ignore itself: below a direct entry the
    /// whole subtree is compared with no routing-leaf denylist, so a nested object or array is a policy even
    /// when every leaf below it happens to be called <c>modelId</c> or <c>source</c>.
    /// </summary>
    private static bool HasSemanticContent(JsonElement element, RoutingPosition position)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.EnumerateObject().Any(property => !IsRoutingOnly(property, position));
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            // An array on the routing surface is a list of binding entries, so it is pure rebinding only when
            // every entry is; anywhere else a non-empty array is ordinary content.
            return position == RoutingPosition.InBindingContainer
                ? element.EnumerateArray()
                    .Any(item => HasSemanticContent(item, RoutingPosition.InBindingEntry))
                : element.GetArrayLength() > 0;
        }

        return true;
    }

    /// <summary>
    /// One member of a binding container contributes nothing but routing when it is a routing scalar, or when
    /// it is an object-shaped binding entry whose every member is itself a routing scalar. Nothing else is
    /// dropped: a nested object or array below an entry is a nested policy, and an entry that carries any
    /// other member is compared.
    /// </summary>
    private static bool IsRoutingOnly(JsonProperty property, RoutingPosition position)
    {
        if (IsRoutingLeaf(property, position))
        {
            return true;
        }

        return position == RoutingPosition.InBindingContainer
            && property.Value.ValueKind == JsonValueKind.Object
            && !HasSemanticContent(property.Value, RoutingPosition.InBindingEntry);
    }

    private static bool IsRoleName(string name) =>
        WorkflowRoleText.TryParse(name, out var role) && role != WorkflowRole.Unknown;

    private static bool IsScalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            or JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false
    };

    /// <summary>
    /// True only for the plain name array the manifest parser actually reads. A declared-name key holding
    /// anything else stays in the structural comparison, so a stage, gate, escalation or role policy cannot
    /// be parked under <c>roles</c> or <c>entrypoints</c>.
    /// </summary>
    private static bool IsPlainNameArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
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

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static bool ContainsNullByte(ReadOnlySpan<byte> bytes) => bytes.IndexOf((byte)0) >= 0;

    private static string StripPreamble(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
        }

        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
