using System.Security.Cryptography;
using System.Text;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Compares the semantic facts of a source package against a candidate package and reports every
/// difference as a <see cref="SemanticChange"/>. The comparison is deterministic, bounded and free of
/// file content: a document difference is reported as a pair of SHA-256 digests, a manifest difference as
/// the added and removed declared names or as a pair of structure-form digests.
/// <para>
/// Every root formal manifest is compared on its own path, so an unchanged <c>manifest.json</c> can never
/// mask a rewritten <c>workflow.json</c>, which is the file the activation parser reads first. A package
/// that declares several root manifests is additionally reported when they disagree, because its executed
/// semantics would then depend on file precedence instead of on a single declaration.
/// </para>
/// <para>
/// Every <b>clearable</b> manifest finding names the structure-form digests of the content it was decided
/// on, so the acknowledgement the activation gate matches is bound to the exact bytes that were reviewed:
/// a decision taken for one rewrite cannot authorise another one.
/// </para>
/// <para>
/// A semantic-bearing file that could not be read within the bounded analysis limits, a manifest that
/// could not be parsed, a declared-name list that the bound truncated, and a package whose file budget was
/// exhausted are reported as <see cref="SemanticChangeKind.UnverifiableContent"/> and marked
/// <see cref="SemanticChange.NonClearable"/>: an incomplete analysis is a gap in the evidence, not a finding
/// the operator may acknowledge away, so no consent path can suppress it.
/// </para>
/// </summary>
public static class SemanticPackageComparison
{
    private const string PackageSubject = "(package)";

    /// <summary>Marks a side of a comparison that does not declare the manifest at all.</summary>
    private const string AbsentMarker = "absent";

    /// <summary>Marks a manifest that exists but whose structure form could not be extracted.</summary>
    private const string UnreadableMarker = "unreadable";

    /// <summary>Compares the two fact sets and returns the detected differences in a stable order.</summary>
    public static IReadOnlyList<SemanticChange> Compare(
        SemanticPackageFacts source,
        SemanticPackageFacts candidate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);

        var changes = new List<SemanticChange>();

        if (!source.InspectionSucceeded || !candidate.InspectionSucceeded)
        {
            changes.Add(new SemanticChange(SemanticChangeKind.UnverifiableContent, PackageSubject,
                $"The package root could not be inspected (source available: {source.InspectionSucceeded}; candidate available: {candidate.InspectionSucceeded}); unavailable evidence is not an empty package.",
                nonClearable: true));
        }

        CompareTruncation(source, candidate, changes);
        CompareManifestPrecedence(source, candidate, changes);
        CompareManifestAvailability(source, candidate, changes);
        CompareManifestContents(source, candidate, changes);
        CompareUnverifiableFiles(source, candidate, changes);
        CompareSemanticDocuments(source, candidate, changes);

        return changes;
    }

    private static void CompareTruncation(
        SemanticPackageFacts source,
        SemanticPackageFacts candidate,
        List<SemanticChange> changes)
    {
        if (source.AnalysisTruncated)
        {
            changes.Add(new SemanticChange(
                SemanticChangeKind.UnverifiableContent,
                PackageSubject,
                $"The source package exceeds the bounded semantic analysis limit of {SemanticPackageAnalyzer.MaxAnalyzedFiles} inspected files or {SemanticPackageAnalyzer.MaxEnumeratedEntries} traversed entries, so the content outside that budget could not be compared.",
                nonClearable: true));
        }

        if (candidate.AnalysisTruncated)
        {
            changes.Add(new SemanticChange(
                SemanticChangeKind.UnverifiableContent,
                PackageSubject,
                $"The candidate package exceeds the bounded semantic analysis limit of {SemanticPackageAnalyzer.MaxAnalyzedFiles} inspected files or {SemanticPackageAnalyzer.MaxEnumeratedEntries} traversed entries, so the content outside that budget could not be compared.",
                nonClearable: true));
        }
    }

    /// <summary>
    /// A package that ships more than one root formal manifest declares its workflow ambiguously: the
    /// activation parser reads <c>workflow.json</c> first, so the executed semantics depend on file
    /// precedence. That is harmless while the manifests agree on their roles, entrypoints and structure, and
    /// a rewrite of either one is reported on its own path anyway. As soon as they disagree, the package is
    /// reported once per manifest set, because which file a reader honours becomes a silent choice.
    /// </summary>
    private static void CompareManifestPrecedence(
        SemanticPackageFacts source,
        SemanticPackageFacts candidate,
        List<SemanticChange> changes)
    {
        var sourceSubject = SubjectOf(source.Manifests);
        var candidateSubject = SubjectOf(candidate.Manifests);
        var sameSet = string.Equals(candidateSubject, sourceSubject, StringComparison.Ordinal);
        var sourceAmbiguous = IsAmbiguous(source.Manifests);
        var candidateAmbiguous = IsAmbiguous(candidate.Manifests);

        // One finding per ambiguous manifest set. When both sides declare the same set, the finding names the
        // digests of both, so the decision is bound to the whole set; when the sets differ, each side is
        // judged on its own and a decision for one set can never cover the other.
        if (sourceAmbiguous && candidateAmbiguous && sameSet)
        {
            changes.Add(PrecedenceChange(sourceSubject, source.Manifests, candidate.Manifests));
        }
        else if (sourceAmbiguous)
        {
            changes.Add(PrecedenceChange(
                sourceSubject,
                source.Manifests,
                sameSet ? candidate.Manifests : Array.Empty<SemanticManifestFacts>()));
        }
        else if (candidateAmbiguous)
        {
            changes.Add(PrecedenceChange(
                candidateSubject,
                sameSet ? source.Manifests : Array.Empty<SemanticManifestFacts>(),
                candidate.Manifests));
        }
    }

    private static SemanticChange PrecedenceChange(
        string subject,
        IReadOnlyList<SemanticManifestFacts> sourceSet,
        IReadOnlyList<SemanticManifestFacts> candidateSet) => new(
        SemanticChangeKind.ManifestPrecedence,
        subject,
        "The package declares more than one root formal manifest and they disagree, so its effective "
        + "stage, quality-gate and escalation semantics depend on file precedence instead of on a "
        + $"single declaration ({DescribeSet(sourceSet, candidateSet)}).");

    private static string SubjectOf(IReadOnlyList<SemanticManifestFacts> manifests) => string.Join(
        "+",
        manifests
            .Select(manifest => manifest.Path)
            .OrderBy(path => path, StringComparer.Ordinal));

    private static bool IsAmbiguous(IReadOnlyList<SemanticManifestFacts> manifests) =>
        manifests.Count >= 2 && !AreSemanticallyConsistent(manifests);

    /// <summary>
    /// The structure-form digests of every root manifest of the set, on both sides, so the finding names the
    /// exact content it was decided on and a decision for one set can never cover another.
    /// </summary>
    private static string DescribeSet(
        IReadOnlyList<SemanticManifestFacts> sourceSet,
        IReadOnlyList<SemanticManifestFacts> candidateSet)
    {
        var paths = sourceSet
            .Select(manifest => manifest.Path)
            .Concat(candidateSet.Select(manifest => manifest.Path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal);

        return string.Join(
            "; ",
            paths.Select(path =>
                $"{path} source sha256:{DigestOf(Find(sourceSet, path))}, "
                + $"candidate sha256:{DigestOf(Find(candidateSet, path))}"));
    }

    private static SemanticManifestFacts? Find(IReadOnlyList<SemanticManifestFacts> manifests, string path)
    {
        foreach (var manifest in manifests)
        {
            if (string.Equals(manifest.Path, path, StringComparison.Ordinal))
            {
                return manifest;
            }
        }

        return null;
    }

    private static string DigestOf(SemanticManifestFacts? manifest) => manifest is null
        ? AbsentMarker
        : StructureDigest(manifest.StructureForm);

    /// <summary>
    /// SHA-256 of the canonical structure form, which is the evidence the manifest finding was decided on.
    /// An unreadable manifest is named explicitly instead of being reduced to an empty digest.
    /// </summary>
    private static string StructureDigest(string? structureForm) => structureForm is null
        ? UnreadableMarker
        : Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(structureForm)))
            .ToLowerInvariant();

    private static bool AreSemanticallyConsistent(IReadOnlyList<SemanticManifestFacts> manifests)
    {
        var first = manifests[0];

        if (!first.Readable)
        {
            return false;
        }

        foreach (var manifest in manifests.Skip(1))
        {
            if (!manifest.Readable
                || !string.Equals(manifest.StructureForm, first.StructureForm, StringComparison.Ordinal)
                || !SameNames(manifest.DeclaredRoles, first.DeclaredRoles)
                || !SameNames(manifest.Entrypoints, first.Entrypoints))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameNames(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.SequenceEqual(right, StringComparer.Ordinal);

    private static void CompareManifestAvailability(
        SemanticPackageFacts source,
        SemanticPackageFacts candidate,
        List<SemanticChange> changes)
    {
        var sourceNames = new HashSet<string>(
            source.Manifests.Select(manifest => manifest.Path),
            StringComparer.OrdinalIgnoreCase);

        var candidateNames = new HashSet<string>(
            candidate.Manifests.Select(manifest => manifest.Path),
            StringComparer.OrdinalIgnoreCase);

        foreach (var name in candidateNames
                     .Except(sourceNames, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            changes.Add(new SemanticChange(
                SemanticChangeKind.ManifestAvailability,
                name,
                $"The candidate adds the root formal manifest '{name}' that the source version does not "
                + $"declare (source sha256:{AbsentMarker}, candidate sha256:"
                + $"{StructureDigest(candidate.FindManifest(name)?.StructureForm)})."));
        }

        foreach (var name in sourceNames
                     .Except(candidateNames, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            changes.Add(new SemanticChange(
                SemanticChangeKind.ManifestAvailability,
                name,
                $"The candidate removes the root formal manifest '{name}' declared by the source version "
                + $"(source sha256:{StructureDigest(source.FindManifest(name)?.StructureForm)}, "
                + $"candidate sha256:{AbsentMarker})."));
        }
    }

    private static void CompareManifestContents(
        SemanticPackageFacts source,
        SemanticPackageFacts candidate,
        List<SemanticChange> changes)
    {
        // Every manifest either side declares is walked, not only the shared ones: a declared-name list that
        // the bound truncated leaves a gap in the evidence even when the manifest is only present on one
        // side, and an unshared manifest is already reported by availability.
        var paths = source.Manifests
            .Select(manifest => manifest.Path)
            .Union(candidate.Manifests.Select(manifest => manifest.Path), StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var sourceManifest = source.FindManifest(path);
            var candidateManifest = candidate.FindManifest(path);

            ReportTruncatedDeclaredNames(
                path,
                "declaredRoles",
                static manifest => manifest.DeclaredRolesTruncated,
                sourceManifest,
                candidateManifest,
                changes);
            ReportTruncatedDeclaredNames(
                path,
                "entrypoints",
                static manifest => manifest.EntrypointsTruncated,
                sourceManifest,
                candidateManifest,
                changes);

            if (sourceManifest is { Readable: false } || candidateManifest is { Readable: false })
            {
                changes.Add(new SemanticChange(
                    SemanticChangeKind.UnverifiableContent,
                    path,
                    "The formal workflow manifest cannot be parsed confidently, so its declared stages, quality gates and escalation rules cannot be compared.",
                    nonClearable: true));

                continue;
            }

            if (sourceManifest is null || candidateManifest is null)
            {
                continue;
            }

            CompareDeclaredNames(
                sourceManifest.DeclaredRoles,
                candidateManifest.DeclaredRoles,
                path,
                "declaredRoles",
                SemanticChangeKind.DeclaredRoleSet,
                sourceManifest,
                candidateManifest,
                changes);

            CompareDeclaredNames(
                sourceManifest.Entrypoints,
                candidateManifest.Entrypoints,
                path,
                "entrypoints",
                SemanticChangeKind.EntrypointSet,
                sourceManifest,
                candidateManifest,
                changes);

            if (!string.Equals(
                    sourceManifest.StructureForm,
                    candidateManifest.StructureForm,
                    StringComparison.Ordinal))
            {
                changes.Add(new SemanticChange(
                    SemanticChangeKind.ManifestStructure,
                    path,
                    "The formal workflow manifest changes outside the known model-routing and provenance "
                    + "leaves, so a stage, quality-gate, escalation or role policy definition was rewritten "
                    + $"(source sha256:{StructureDigest(sourceManifest.StructureForm)}, candidate sha256:"
                    + $"{StructureDigest(candidateManifest.StructureForm)})."));
            }
        }
    }

    /// <summary>
    /// A declared-name list longer than <see cref="SemanticPackageAnalyzer.MaxDeclaredNames"/> is kept only
    /// as a bounded prefix, while the manifest parser reads every entry, so the entries past the cap cannot
    /// be compared: a rewritten last name, a duplicate or a case variant would otherwise be an empty diff.
    /// That is a gap in the evidence, so it is reported on the manifest path and never clearable.
    /// </summary>
    private static void ReportTruncatedDeclaredNames(
        string path,
        string section,
        Func<SemanticManifestFacts, bool> isTruncated,
        SemanticManifestFacts? sourceManifest,
        SemanticManifestFacts? candidateManifest,
        List<SemanticChange> changes)
    {
        if ((sourceManifest is null || !isTruncated(sourceManifest))
            && (candidateManifest is null || !isTruncated(candidateManifest)))
        {
            return;
        }

        changes.Add(new SemanticChange(
            SemanticChangeKind.UnverifiableContent,
            path,
            $"The {section} of the formal workflow manifest '{path}' declare more than the bounded analysis "
            + $"limit of {SemanticPackageAnalyzer.MaxDeclaredNames} entries, so the entries past that limit "
            + "could not be compared and only a prefix of the declaration was analysed.",
            nonClearable: true));
    }

    private static void CompareDeclaredNames(
        IReadOnlyList<string> source,
        IReadOnlyList<string> candidate,
        string subject,
        string section,
        SemanticChangeKind kind,
        SemanticManifestFacts sourceManifest,
        SemanticManifestFacts candidateManifest,
        List<SemanticChange> changes)
    {
        var added = candidate
            .Where(name => !source.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var removed = source
            .Where(name => !candidate.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (added.Length == 0 && removed.Length == 0)
        {
            return;
        }

        var detail = $"Manifest {section} of '{subject}' differ from the source version"
            + $" (added: {FormatNames(added)}; removed: {FormatNames(removed)}; "
            + $"source sha256:{StructureDigest(sourceManifest.StructureForm)}, candidate sha256:{StructureDigest(candidateManifest.StructureForm)}; "
            + $"source names sha256:{DeclaredNamesDigest(sourceManifest)}, candidate names sha256:{DeclaredNamesDigest(candidateManifest)}).";

        changes.Add(new SemanticChange(kind, $"{subject}#{section}", detail));
    }

    private static string DeclaredNamesDigest(SemanticManifestFacts manifest) => StructureDigest(
        System.Text.Json.JsonSerializer.Serialize(new
        {
            roles = manifest.DeclaredRoles.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            entrypoints = manifest.Entrypoints.OrderBy(name => name, StringComparer.Ordinal).ToArray()
        }));

    private static void CompareUnverifiableFiles(
        SemanticPackageFacts source,
        SemanticPackageFacts candidate,
        List<SemanticChange> changes)
    {
        var unverifiable = source.UnverifiableFiles
            .Concat(candidate.UnverifiableFiles)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var path in unverifiable)
        {
            changes.Add(new SemanticChange(
                SemanticChangeKind.UnverifiableContent,
                path,
                $"The package path '{path}' could not be inspected or read (inaccessible or linked content, non-text data, the {SemanticPackageAnalyzer.MaxAnalyzedFileBytes}-byte limit, or the bounded file budget of {SemanticPackageAnalyzer.MaxAnalyzedFiles} files), so it cannot be compared confidently.",
                nonClearable: true));
        }
    }

    private static void CompareSemanticDocuments(
        SemanticPackageFacts source,
        SemanticPackageFacts candidate,
        List<SemanticChange> changes)
    {
        // A path that could not be read on either side is already reported as unverifiable content; adding an
        // add/remove difference for it would describe the same finding twice.
        var unverifiable = source.UnverifiableFiles
            .Concat(candidate.UnverifiableFiles)
            .ToHashSet(StringComparer.Ordinal);

        var paths = source.DocumentDigests.Keys
            .Concat(candidate.DocumentDigests.Keys)
            .Where(path => !unverifiable.Contains(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var presentInSource = source.DocumentDigests.TryGetValue(path, out var sourceDigest);
            var presentInCandidate = candidate.DocumentDigests.TryGetValue(path, out var candidateDigest);

            if (presentInSource && presentInCandidate)
            {
                if (!string.Equals(sourceDigest, candidateDigest, StringComparison.Ordinal))
                {
                    changes.Add(new SemanticChange(
                        SemanticChangeKind.SemanticDocument,
                        path,
                        $"The semantic-bearing file '{path}' is rewritten (source sha256:{sourceDigest}, candidate sha256:{candidateDigest})."));
                }

                continue;
            }

            changes.Add(new SemanticChange(
                SemanticChangeKind.SemanticDocument,
                path,
                presentInCandidate
                    ? $"The candidate adds the semantic-bearing file '{path}', which the source version does not declare (source sha256:{AbsentMarker}, candidate sha256:{candidateDigest})."
                    : $"The candidate removes the semantic-bearing file '{path}' declared by the source version (source sha256:{sourceDigest}, candidate sha256:{AbsentMarker})."));
        }
    }

    private static string FormatNames(IReadOnlyList<string> names) =>
        names.Count == 0 ? "none" : $"{names.Count} name(s), sha256:{StructureDigest(System.Text.Json.JsonSerializer.Serialize(names))}";
}
