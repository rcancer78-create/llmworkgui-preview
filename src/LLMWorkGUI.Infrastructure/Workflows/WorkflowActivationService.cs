using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Candidate activation and rollback engine (ТЗ §6.14, ADR-0006). Directly before the pointer moves the
/// candidate is re-extracted into <see cref="ScratchScope.Adaptation"/> scratch, re-scanned for secrets
/// and re-validated against the live sanitized catalog, so a stale approval cannot activate a version
/// that no longer resolves. Activation writes only the <c>WorkflowBindings</c> row through
/// <see cref="IWorkflowBindingService"/>; blobs and <c>WorkflowVersion</c> records stay byte-identical.
/// The temporary scratch workspace is cleaned up in a <c>finally</c> block on every code path.
/// </summary>
public sealed class WorkflowActivationService : IWorkflowActivationService
{
    private const string ActivationScopePrefix = "activation-";
    private const int ScopeIdRandomSuffixLength = 8;

    private static readonly string[] ManifestFileNames = ["workflow.json", "manifest.json"];

    private static readonly JsonSerializerOptions MappingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IWorkflowVersionRepository _versionRepository;
    private readonly IWorkflowBindingRepository _bindingRepository;
    private readonly IWorkflowBindingService _bindingService;
    private readonly ISanitizedCatalogProvider _catalogProvider;
    private readonly IWorkflowSecretScanner _secretScanner;
    private readonly IScratchWorkspaceManager _scratchWorkspaceManager;
    private readonly IWorkflowManifestParser _manifestParser;
    private readonly ISemanticDiffEngine _semanticDiffEngine;
    private readonly TimeProvider _timeProvider;

    public WorkflowActivationService(
        IWorkflowVersionRepository versionRepository,
        IWorkflowBindingRepository bindingRepository,
        IWorkflowBindingService bindingService,
        ISanitizedCatalogProvider catalogProvider,
        IWorkflowSecretScanner secretScanner,
        IScratchWorkspaceManager scratchWorkspaceManager,
        IWorkflowManifestParser manifestParser,
        ISemanticDiffEngine semanticDiffEngine,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(versionRepository);
        ArgumentNullException.ThrowIfNull(bindingRepository);
        ArgumentNullException.ThrowIfNull(bindingService);
        ArgumentNullException.ThrowIfNull(catalogProvider);
        ArgumentNullException.ThrowIfNull(secretScanner);
        ArgumentNullException.ThrowIfNull(scratchWorkspaceManager);
        ArgumentNullException.ThrowIfNull(manifestParser);
        ArgumentNullException.ThrowIfNull(semanticDiffEngine);

        _versionRepository = versionRepository;
        _bindingRepository = bindingRepository;
        _bindingService = bindingService;
        _catalogProvider = catalogProvider;
        _secretScanner = secretScanner;
        _scratchWorkspaceManager = scratchWorkspaceManager;
        _manifestParser = manifestParser;
        _semanticDiffEngine = semanticDiffEngine;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowActivationValidationResult> ValidateForActivationAsync(
        string workflowVersionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowVersionId);

        var version = await _versionRepository
            .GetByIdAsync(workflowVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WorkflowValidationException(
                $"The workflow version '{workflowVersionId}' does not exist.");

        var workspace = await _scratchWorkspaceManager
            .CreateWorkspaceAsync(ScratchScope.Adaptation, BuildScopeId(), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _scratchWorkspaceManager
                .ExtractBlobToWorkspaceAsync(version.BlobId, workspace, cancellationToken)
                .ConfigureAwait(false);

            await _scratchWorkspaceManager
                .PostOperationSourceHashCheckAsync(version.BlobId, cancellationToken)
                .ConfigureAwait(false);

            var issues = new List<AdaptationValidationIssue>();

            var scanReport = await _secretScanner
                .ScanScratchWorkspaceAsync(workspace, cancellationToken)
                .ConfigureAwait(false);

            foreach (var finding in scanReport.Findings)
            {
                issues.Add(new AdaptationValidationIssue(
                    AdaptationBlockerKind.DetectedSecret,
                    role: null,
                    $"Secret '{finding.RuleName}' detected at {finding.RelativePath}:{finding.LineNumber}."));
            }

            var catalog = await _catalogProvider
                .GetSanitizedCatalogAsync(cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<SemanticRoleMapping> mappings;
            try
            {
                mappings = await ResolveMappingsAsync(version, workspace.DirectoryPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WorkflowValidationException)
            {
                mappings = Array.Empty<SemanticRoleMapping>();
                issues.Add(SemanticBaselineIssue("The workflow bindings are malformed and cannot be verified."));
            }

            issues.AddRange(ValidateReferences(mappings, catalog));
            issues.AddRange(await ValidateSemanticChangesAsync(
                    version,
                    mappings,
                    workspace.DirectoryPath,
                    cancellationToken)
                .ConfigureAwait(false));

            return new WorkflowActivationValidationResult(issues);
        }
        finally
        {
            await workspace.CleanupWorkspaceAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<WorkflowActivationResult> ActivateVersionAsync(
        WorkflowActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        WorkflowActivationValidationResult validation;

        try
        {
            validation = await ValidateForActivationAsync(request.WorkflowVersionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowValidationException exception)
        {
            return WorkflowActivationResult.Failed(exception.Message);
        }

        if (!validation.IsFullyAcknowledgedBy(request.AcknowledgedBlockerIssues))
        {
            return WorkflowActivationResult.Blocked(
                validation,
                BuildBlockedMessage(
                    request.AcknowledgedBlockerIssues.Count > 0,
                    validation.HasUnclearableBlockers));
        }

        try
        {
            var result = request.PreserveExistingRoutePolicy
                ? await _bindingService.SetActiveVersionAsync(request.ProjectId, request.WorkflowPackageId,
                    request.WorkflowVersionId, cancellationToken).ConfigureAwait(false)
                : await _bindingService.BindWorkflowToProjectAsync(
                    request.ProjectId,
                    request.WorkflowPackageId,
                    request.WorkflowVersionId,
                    request.RoutePolicyId,
                    cancellationToken)
                .ConfigureAwait(false);

            return WorkflowActivationResult.Success(result.Binding, validation);
        }
        catch (WorkflowValidationException exception)
        {
            return WorkflowActivationResult.Failed(exception.Message, validation);
        }
    }

    /// <summary>
    /// Re-points an existing binding to a version of the same package. Activation history is not stored
    /// (no version row is ever stamped), so the safe equivalent is applied instead: the target goes
    /// through exactly the same fail-closed validation and per-blocker decision gate as activation. A
    /// rollback can therefore never activate a version that <see cref="ActivateVersionAsync"/> itself
    /// could not activate, and the blobs stay byte-identical.
    /// </summary>
    public async Task<WorkflowRollbackResult> RollbackToVersionAsync(
        string projectId,
        string workflowPackageId,
        string targetVersionId,
        bool acknowledgeBlockers = false,
        IReadOnlyCollection<AdaptationBlockerKind>? acknowledgedBlockerKinds = null,
        IReadOnlyCollection<AdaptationValidationIssue>? acknowledgedBlockerIssues = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowPackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersionId);

        var version = await _versionRepository
            .GetByIdAsync(targetVersionId, cancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            return WorkflowRollbackResult.Failed(
                targetVersionId,
                $"The workflow version '{targetVersionId}' does not exist.");
        }

        if (!string.Equals(version.WorkflowPackageId, workflowPackageId, StringComparison.Ordinal))
        {
            return WorkflowRollbackResult.Failed(
                targetVersionId,
                $"The workflow version '{targetVersionId}' does not belong to package '{workflowPackageId}'.");
        }

        var existing = await _bindingRepository
            .GetByProjectAndPackageAsync(projectId, workflowPackageId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return WorkflowRollbackResult.Failed(
                targetVersionId,
                $"The workflow package '{workflowPackageId}' is not bound to project '{projectId}'.");
        }

        WorkflowActivationValidationResult validation;

        try
        {
            validation = await ValidateForActivationAsync(targetVersionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowValidationException exception)
        {
            return WorkflowRollbackResult.Failed(targetVersionId, exception.Message);
        }

        if (!validation.IsFullyAcknowledgedBy(acknowledgedBlockerIssues))
        {
            return WorkflowRollbackResult.Blocked(
                validation,
                targetVersionId,
                BuildBlockedMessage(
                    acknowledgedBlockerIssues?.Count > 0,
                    validation.HasUnclearableBlockers));
        }

        try
        {
            var result = await _bindingService
                .SetActiveVersionAsync(projectId, workflowPackageId, targetVersionId, cancellationToken)
                .ConfigureAwait(false);

            return WorkflowRollbackResult.Success(
                result.Binding,
                existing.ActiveVersionId,
                targetVersionId);
        }
        catch (WorkflowValidationException exception)
        {
            return WorkflowRollbackResult.Failed(targetVersionId, exception.Message, validation);
        }
    }

    /// <summary>
    /// Explains the refusal without ever leaking a blocked state into a success. A decision set that
    /// exists but does not match the fresh issue list is reported separately, because the operator has
    /// to look at the newly reported issues again instead of repeating the old confirmation. A reported
    /// issue that names content the bounded analysis could not read is reported as a permanent refusal,
    /// because no decision can stand in for content that was never verified.
    /// </summary>
    private static string BuildBlockedMessage(
        bool hasStaleDecisions,
        bool hasUnclearableBlockers = false)
    {
        if (hasUnclearableBlockers)
        {
            return "Activation blocked: part of the candidate could not be verified, and that gap cannot be "
                + "acknowledged. Re-run the adaptation so the comparison covers the whole package.";
        }

        return hasStaleDecisions
            ? "Activation blocked: the reported blockers are not acknowledged by the recorded decisions; "
                + "confirm every reported issue again."
            : "Activation blocked: candidate has unresolved blockers that were not acknowledged.";
    }

    private async Task<IReadOnlyList<SemanticRoleMapping>> ResolveMappingsAsync(
        WorkflowVersion version,
        string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        var serializedBindings = version.BindingsJson;

        if (string.IsNullOrWhiteSpace(serializedBindings))
        {
            serializedBindings = await TryReadManifestBindingsAsync(workspaceDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        return ParseMappings(serializedBindings);
    }

    private async Task<string?> TryReadManifestBindingsAsync(
        string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        var manifestPath = FindManifestPath(workspaceDirectory);

        if (manifestPath is null)
        {
            return null;
        }

        var manifestBytes = await File
            .ReadAllBytesAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(Path.GetFileName(manifestPath), CompressionLevel.NoCompression);

            await using var entryStream = entry.Open();

            await entryStream
                .WriteAsync(manifestBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        buffer.Position = 0;

        var manifest = await _manifestParser
            .ParseManifestAsync(buffer, cancellationToken)
            .ConfigureAwait(false);

        return manifest.BindingsJson;
    }

    private static string? FindManifestPath(string workspaceDirectory)
    {
        foreach (var fileName in ManifestFileNames)
        {
            var candidate = Path.Combine(workspaceDirectory, fileName);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var path in Directory.EnumerateFiles(workspaceDirectory))
        {
            var fileName = Path.GetFileName(path);

            foreach (var manifestFileName in ManifestFileNames)
            {
                if (string.Equals(fileName, manifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<SemanticRoleMapping> ParseMappings(string? bindingsJson)
    {
        if (string.IsNullOrWhiteSpace(bindingsJson))
        {
            return Array.Empty<SemanticRoleMapping>();
        }

        try
        {
            using var document = JsonDocument.Parse(bindingsJson);

            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => ParseMappingArray(document.RootElement),
                JsonValueKind.Object => ParseMappingObject(document.RootElement),
                _ => throw new WorkflowValidationException("Workflow bindings must be an object or array.")
            };
        }
        catch (JsonException)
        {
            throw new WorkflowValidationException("Workflow bindings contain invalid JSON.");
        }
    }

    private static IReadOnlyList<SemanticRoleMapping> ParseMappingArray(JsonElement array)
    {
        var mappings = new List<SemanticRoleMapping>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new WorkflowValidationException("A workflow binding is invalid.");
            }

            try
            {
                var mapping = JsonSerializer.Deserialize<SemanticRoleMapping>(
                    element.GetRawText(),
                    MappingJsonOptions);

                if (mapping is not null)
                {
                    mappings.Add(mapping);
                }
            }
            catch (JsonException)
            {
                throw new WorkflowValidationException("A workflow binding is invalid.");
            }
            catch (ArgumentException)
            {
                throw new WorkflowValidationException("A workflow binding is invalid.");
            }
            catch (NotSupportedException)
            {
                throw new WorkflowValidationException("A workflow binding is invalid.");
            }
        }

        return mappings;
    }

    private static IReadOnlyList<SemanticRoleMapping> ParseMappingObject(JsonElement root)
    {
        var mappings = new List<SemanticRoleMapping>();

        foreach (var property in root.EnumerateObject())
        {
            if (!WorkflowRoleParser.TryParse(property.Name, out var role)
                || role == WorkflowRole.Unknown)
            {
                throw new WorkflowValidationException("A workflow binding is invalid.");
            }

            var binding = ReadRoleBinding(property.Value);

            if (string.IsNullOrWhiteSpace(binding.ModelId))
            {
                throw new WorkflowValidationException("A workflow binding is invalid.");
            }

            try
            {
                mappings.Add(new SemanticRoleMapping(
                    role,
                    binding.OriginalRoute ?? "unbound",
                    binding.TargetRoute ?? binding.ModelId,
                    binding.ModelId,
                    binding.Rationale ?? "Declared by the workflow manifest.",
                    binding.IsSemanticChange));
            }
            catch (ArgumentException)
            {
                throw new WorkflowValidationException("A workflow binding is invalid.");
            }
        }

        return mappings;
    }

    private static (string? ModelId, string? OriginalRoute, string? TargetRoute, string? Rationale, bool IsSemanticChange)
        ReadRoleBinding(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return (element.GetString(), null, null, null, false);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null, null, false);
        }

        return (
            ReadString(element, "targetModelId", "modelId", "model", "target"),
            ReadString(element, "originalRoute", "sourceRoute", "originalModelId", "source"),
            ReadString(element, "targetRoute", "route"),
            ReadString(element, "rationale", "reason"),
            ReadBoolean(element, "isSemanticChange", "semanticChange"));
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }

        return null;
    }

    private static bool ReadBoolean(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return property.Value.GetBoolean();
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Revalidates the ТЗ §6.14 semantic guard against real evidence instead of the model's own claims.
    /// <para>
    /// Role drift is decided from the stored mappings and the declared role baseline of the version, so an
    /// empty or unparseable mapping list against a non-empty baseline is a role removal, and a mapping the
    /// model marked as a semantic change stays a blocker.
    /// </para>
    /// <para>
    /// A candidate that carries adaptation provenance is additionally compared against the real source
    /// package, freshly extracted and integrity-checked in this call, so a stage, quality-gate or escalation
    /// rewrite survives into activation. The result is a fresh issue list and nothing else: this path holds
    /// no recorded approval, so the only way past a detected change is the operator acknowledging that
    /// exact issue in the decision UI, which comes back through
    /// <see cref="WorkflowActivationRequest.AcknowledgedBlockerIssues"/> and is matched against the list
    /// produced here. A pre-send scope flag, a model response and package metadata are all irrelevant.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<AdaptationValidationIssue>> ValidateSemanticChangesAsync(
        WorkflowVersion version,
        IReadOnlyList<SemanticRoleMapping> mappings,
        string candidateDirectory,
        CancellationToken cancellationToken)
    {
        var provenance = WorkflowCompatibilityReport.ReadProvenance(version.CompatibilityReportJson);
        if (provenance.IsInvalid)
        {
            return new[]
            {
                SemanticBaselineIssue(
                    "The adaptation provenance report is invalid, so the source baseline cannot be revalidated.")
            };
        }

        var sourceVersionId = provenance.SourceVersionId;

        if (string.IsNullOrWhiteSpace(sourceVersionId))
        {
            // No adaptation provenance: the candidate is its own semantic baseline, so only role drift applies.
            return _semanticDiffEngine.Compare(new SemanticDiffRequest(
                version.DeclaredRolesJson,
                mappings,
                allowExpandedSemanticScope: false)).Issues;
        }

        var sourceVersion = await _versionRepository
            .GetByIdAsync(sourceVersionId, cancellationToken)
            .ConfigureAwait(false);

        if (sourceVersion is null
            || !string.Equals(
                sourceVersion.WorkflowPackageId,
                version.WorkflowPackageId,
                StringComparison.Ordinal))
        {
            return new[]
            {
                SemanticBaselineIssue(
                    $"The source version '{sourceVersionId}' of this candidate can no longer be read, "
                    + "so its stage, quality-gate and escalation baseline cannot be revalidated.")
            };
        }

        var sourceFacts = await ReadSourcePackageFactsAsync(sourceVersion, cancellationToken)
            .ConfigureAwait(false);

        var candidateFacts = SemanticPackageAnalyzer.Read(candidateDirectory);

        if (sourceFacts is null)
        {
            return new[]
            {
                SemanticBaselineIssue(
                    $"The source package of the candidate can no longer be extracted, so its stage, "
                    + "quality-gate and escalation baseline cannot be revalidated.")
            };
        }

        return _semanticDiffEngine.Compare(new SemanticDiffRequest(
            version.DeclaredRolesJson,
            mappings,
            allowExpandedSemanticScope: false,
            sourceFacts,
            candidateFacts)).Issues;
    }

    private async Task<SemanticPackageFacts?> ReadSourcePackageFactsAsync(
        WorkflowVersion sourceVersion,
        CancellationToken cancellationToken)
    {
        var workspace = await _scratchWorkspaceManager
            .CreateWorkspaceAsync(ScratchScope.Adaptation, BuildScopeId(), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _scratchWorkspaceManager
                .ExtractBlobToWorkspaceAsync(sourceVersion.BlobId, workspace, cancellationToken)
                .ConfigureAwait(false);

            await _scratchWorkspaceManager
                .PostOperationSourceHashCheckAsync(sourceVersion.BlobId, cancellationToken)
                .ConfigureAwait(false);

            return SemanticPackageAnalyzer.Read(workspace.DirectoryPath);
        }
        catch (WorkflowValidationException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            // The source blob failed its SHA-256 integrity check, so its baseline is unusable. The caller
            // reports this as a blocker instead of letting a validation turn into an unhandled failure.
            return null;
        }
        finally
        {
            await workspace.CleanupWorkspaceAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A candidate whose source baseline can no longer be read, or whose source blob no longer passes its
    /// integrity check, is a gap in the evidence and not a difference the operator is being asked to accept:
    /// the candidate's stages, gates and escalation rules are never compared against anything, so the issue
    /// is never clearable and no decision set can move the active-version pointer.
    /// </summary>
    private static AdaptationValidationIssue SemanticBaselineIssue(string message) => new(
        AdaptationBlockerKind.DisallowedSemanticChange,
        role: null,
        message,
        isNotClearable: true);

    private static IReadOnlyList<AdaptationValidationIssue> ValidateReferences(
        IReadOnlyList<SemanticRoleMapping> mappings,
        SanitizedCapabilityCatalog catalog)
    {
        var issues = new List<AdaptationValidationIssue>();

        foreach (var mapping in mappings)
        {
            var model = catalog.Models.FirstOrDefault(candidate => string.Equals(
                candidate.ModelId,
                mapping.TargetModelId,
                StringComparison.Ordinal));

            if (model is null)
            {
                issues.Add(new AdaptationValidationIssue(
                    AdaptationBlockerKind.MissingModel,
                    mapping.Role.ToString(),
                    $"Model '{mapping.TargetModelId}' not found in the current catalog."));
                continue;
            }

            if (!model.IsRoutable)
            {
                issues.Add(new AdaptationValidationIssue(
                    AdaptationBlockerKind.MissingCapability,
                    mapping.Role.ToString(),
                    $"Model '{mapping.TargetModelId}' is marked not routable in the current catalog."));
                continue;
            }

            // Present and routable is not enough: the row must also carry the capability mask the role
            // requires. The mask comes from the same closed table the session validator uses, so a
            // candidate cannot be prepared and then activated under a weaker requirement.
            issues.AddRange(AdaptationReferenceValidator.ValidateRequiredCapabilities(mapping, model));
        }

        return issues;
    }

    private string BuildScopeId()
    {
        var timestamp = _timeProvider
            .GetUtcNow()
            .ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);

        var suffix = Guid.NewGuid().ToString("N")[..ScopeIdRandomSuffixLength];

        return string.Concat(ActivationScopePrefix, timestamp, "-", suffix);
    }
}
