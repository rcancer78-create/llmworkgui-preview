using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Orchestrates the model-assisted workflow adaptation pipeline. Source versions are extracted into
/// <see cref="ScratchScope.Adaptation"/> scratch only; the source blob is never modified, candidate
/// versions are never activated, the active binding is never touched, and no repository mutation
/// happens without an explicit user command (ТЗ §6.14, ADR-0006). Every turn is fully re-validated
/// (secrets, model/capability references, semantic guard, package diff) and can be saved as a new
/// immutable <see cref="WorkflowSourceType.SyntheticDraft"/> version.
/// <para>
/// A saved candidate records no consent of any kind. The pre-send scope flag is prompt context, every
/// detected semantic change stays a blocker in the session, and clearing one requires the operator's
/// post-diff decision per exact issue through the activation gate, which revalidates the real packages.
/// </para>
/// </summary>
public sealed class WorkflowAdaptationService : IWorkflowAdaptationService
{
    private const int MaxScopeIdLength = 64;
    private const int MaxActiveSessions = 8;
    private const int MaxTurnsPerSession = 64;
    private readonly SemaphoreSlim _sessionCapacity = new(MaxActiveSessions, MaxActiveSessions);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private const int MaxPromptFileBytes = 256 * 1024;
    private const string FreshQuotaValue = "Fresh";
    private const string StaleQuotaValue = "Stale";

    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LittleEndianPreamble = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianPreamble = [0xFE, 0xFF];
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly DateTimeOffset DeterministicEntryTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IWorkflowVersionRepository _versionRepository;
    private readonly IWorkflowPackageRepository _packageRepository;
    private readonly IScratchWorkspaceManager _scratchWorkspaceManager;
    private readonly IWorkflowSecretScanner _secretScanner;
    private readonly IAdaptationPromptBuilder _promptBuilder;
    private readonly ISanitizedCatalogProvider _catalogProvider;
    private readonly IQuotaSnapshotRepository _quotaSnapshotRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly IAdaptationModelInvoker _modelInvoker;
    private readonly IAdaptationEgressPolicy? _egressPolicy;
    private readonly IAdaptationResponseParser _responseParser;
    private readonly IAdaptationReferenceValidator _referenceValidator;
    private readonly ISemanticDiffEngine _semanticDiffEngine;
    private readonly IWorkflowDiffService _diffService;
    private readonly WorkflowBlobStore _blobStore;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, AdaptationSession> _sessions = new(StringComparer.Ordinal);

    public WorkflowAdaptationService(
        IWorkflowVersionRepository versionRepository,
        IWorkflowPackageRepository packageRepository,
        IScratchWorkspaceManager scratchWorkspaceManager,
        IWorkflowSecretScanner secretScanner,
        IAdaptationPromptBuilder promptBuilder,
        ISanitizedCatalogProvider catalogProvider,
        IQuotaSnapshotRepository quotaSnapshotRepository,
        IAccountRepository accountRepository,
        IAdaptationModelInvoker modelInvoker,
        IAdaptationResponseParser responseParser,
        IAdaptationReferenceValidator referenceValidator,
        ISemanticDiffEngine semanticDiffEngine,
        IWorkflowDiffService diffService,
        WorkflowBlobStore blobStore,
        TimeProvider? timeProvider = null,
        IAdaptationEgressPolicy? egressPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(versionRepository);
        ArgumentNullException.ThrowIfNull(packageRepository);
        ArgumentNullException.ThrowIfNull(scratchWorkspaceManager);
        ArgumentNullException.ThrowIfNull(secretScanner);
        ArgumentNullException.ThrowIfNull(promptBuilder);
        ArgumentNullException.ThrowIfNull(catalogProvider);
        ArgumentNullException.ThrowIfNull(quotaSnapshotRepository);
        ArgumentNullException.ThrowIfNull(accountRepository);
        ArgumentNullException.ThrowIfNull(modelInvoker);
        ArgumentNullException.ThrowIfNull(responseParser);
        ArgumentNullException.ThrowIfNull(referenceValidator);
        ArgumentNullException.ThrowIfNull(semanticDiffEngine);
        ArgumentNullException.ThrowIfNull(diffService);
        ArgumentNullException.ThrowIfNull(blobStore);

        _versionRepository = versionRepository;
        _packageRepository = packageRepository;
        _scratchWorkspaceManager = scratchWorkspaceManager;
        _secretScanner = secretScanner;
        _promptBuilder = promptBuilder;
        _catalogProvider = catalogProvider;
        _quotaSnapshotRepository = quotaSnapshotRepository;
        _accountRepository = accountRepository;
        _modelInvoker = modelInvoker;
        _egressPolicy = egressPolicy;
        _responseParser = responseParser;
        _referenceValidator = referenceValidator;
        _semanticDiffEngine = semanticDiffEngine;
        _diffService = diffService;
        _blobStore = blobStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AdaptationPreSendPreview> PreparePreSendPreviewAsync(
        string workflowVersionId,
        string adapterRouteId,
        AdaptationGoal goal,
        IReadOnlyList<string>? userExcludedFiles = null,
        bool allowExpandedSemanticScope = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowVersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterRouteId);
        ValidateGoal(goal);

        var (version, package) = await ResolveVersionAndPackageAsync(workflowVersionId, cancellationToken)
            .ConfigureAwait(false);

        // The route is resolved and refused before any scratch workspace exists: a backend that cannot
        // run adaptation, or an account with no real backend model id, must not cause a scratch
        // extraction, a secret scan or a prompt build.
        var catalog = await _catalogProvider
            .GetSanitizedCatalogAsync(cancellationToken)
            .ConfigureAwait(false);

        var adapterModelId = await ResolveAdapterModelIdAsync(adapterRouteId, catalog, cancellationToken)
            .ConfigureAwait(false);

        var workspace = await _scratchWorkspaceManager
            .CreateWorkspaceAsync(ScratchScope.Adaptation, BuildScopeId(version.Id), cancellationToken)
            .ConfigureAwait(false);

        Exception? primaryFailure = null;
        try
        {
            await _scratchWorkspaceManager
                .ExtractBlobToWorkspaceAsync(version.BlobId, workspace, cancellationToken)
                .ConfigureAwait(false);

            var scanReport = await _secretScanner
                .ScanScratchWorkspaceAsync(workspace, cancellationToken)
                .ConfigureAwait(false);

            var excludedFiles = BuildExcludedFiles(scanReport, userExcludedFiles);

            var (includedFiles, fileContents) = await ReadIncludedFilesAsync(
                    workspace.DirectoryPath,
                    excludedFiles,
                    cancellationToken)
                .ConfigureAwait(false);

            var promptContext = new AdaptationPromptContext(
                version.Id,
                package.Name,
                goal,
                catalog,
                fileContents,
                excludedFiles,
                allowExpandedSemanticScope);

            var systemPrompt = _promptBuilder.BuildSystemPrompt(goal, allowExpandedSemanticScope);
            var userPrompt = _promptBuilder.BuildUserPrompt(promptContext);
            ValidatePromptWireBudget(new AdaptationModelRequest(adapterRouteId, adapterModelId,
                systemPrompt, [new AdaptationTurnMessage("user", userPrompt)]));
            var promptPreview = string.Concat(
                systemPrompt,
                "\n\n",
                userPrompt);

            await _scratchWorkspaceManager
                .PostOperationSourceHashCheckAsync(version.BlobId, cancellationToken)
                .ConfigureAwait(false);

            var quota = await ResolveQuotaPreviewAsync(adapterRouteId, adapterModelId, catalog, cancellationToken)
                .ConfigureAwait(false);

            return new AdaptationPreSendPreview(
                version.Id,
                version.VersionNumber,
                version.BlobId,
                adapterRouteId,
                adapterModelId,
                goal,
                scanReport,
                includedFiles,
                excludedFiles,
                catalog,
                quota.QuotaState,
                quota.QuotaFreshness,
                quota.ReserveThreshold,
                promptPreview);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            if (primaryFailure is null)
                await workspace.CleanupWorkspaceAsync(CancellationToken.None).ConfigureAwait(false);
            else
                await workspace.CleanupAfterFailureAsync(primaryFailure).ConfigureAwait(false);
        }
    }

    public async Task<AdaptationCandidateResult> StartAdaptationAsync(
        AdaptationExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await ReapExpiredSessionsAsync().ConfigureAwait(false);
        // Reserve before the first await in initialization: pending model calls also consume capacity.
        if (!await _sessionCapacity.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new WorkflowValidationException("Discard or save an existing adaptation before starting another.");
        try { return await StartAdaptationAsyncCore(request, cancellationToken).ConfigureAwait(false); }
        catch { _sessionCapacity.Release(); throw; }
    }

    private async Task ReapExpiredSessionsAsync()
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.Gate.Wait(0)) continue;
            try
            {
                if (_sessions.ContainsKey(session.SessionId) &&
                    _timeProvider.GetUtcNow() - session.UpdatedAtUtc > TimeSpan.FromHours(24))
                {
                    if (await TryCleanupSessionAsync(session).ConfigureAwait(false)) RemoveSession(session);
                }
            }
            finally { session.Gate.Release(); }
        }
    }

    private void RemoveSession(AdaptationSession session)
    {
        // Call only while holding this session's gate; its id is never reused.
        if (_sessions.TryRemove(session.SessionId, out _)) _sessionCapacity.Release();
    }

    private async Task<AdaptationCandidateResult> StartAdaptationAsyncCore(
        AdaptationExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGoal(request.Goal);

        var (version, package) = await ResolveVersionAndPackageAsync(request.WorkflowVersionId, cancellationToken)
            .ConfigureAwait(false);

        var sessionId = Guid.NewGuid().ToString();
        var scopeSuffix = sessionId.Replace("-", string.Empty, StringComparison.Ordinal);
        var session = new AdaptationSession(
            sessionId,
            candidateScopeId: "session-" + scopeSuffix,
            sourceScopeId: "source-" + scopeSuffix,
            version,
            package,
            request.AdapterRouteId,
            request.Goal,
            request.AllowExpandedSemanticScope,
            _timeProvider.GetUtcNow());
        session.ProjectId = request.ProjectId;

        try
        {
            session.Catalog = await _catalogProvider
                .GetSanitizedCatalogAsync(cancellationToken)
                .ConfigureAwait(false);
            session.SetAdapterRoute(await ResolveAdapterRouteAsync(
                request.AdapterRouteId,
                session.Catalog,
                cancellationToken).ConfigureAwait(false));
            session.SystemPrompt = _promptBuilder.BuildSystemPrompt(
                request.Goal,
                request.AllowExpandedSemanticScope);

            await CreateSessionWorkspacesAsync(session, cancellationToken).ConfigureAwait(false);

            var promptContext = await BuildPromptContextAsync(session, request.UserExcludedFiles, cancellationToken)
                .ConfigureAwait(false);

            session.TurnHistory.Add(new AdaptationTurnMessage(
                "user",
                _promptBuilder.BuildUserPrompt(promptContext)));

            var parsed = await InvokeTurnAsync(session, cancellationToken).ConfigureAwait(false);
            var applyIssues = await ApplyFileModificationsAsync(
                    session.CandidateWorkspace!,
                    parsed.FileModifications,
                    cancellationToken)
                .ConfigureAwait(false);

            session.LastParsed = parsed;

            var result = await ValidateCandidateAsync(session, applyIssues, cancellationToken)
                .ConfigureAwait(false);

            session.LastResult = result;
            session.UpdatedAtUtc = _timeProvider.GetUtcNow();
            _sessions[sessionId] = session;

            return result;
        }
        catch (Exception primaryFailure)
        {
            if (session.CandidateWorkspace is { } candidate)
                await candidate.CleanupAfterFailureAsync(primaryFailure).ConfigureAwait(false);
            if (session.SourceWorkspace is { } source)
                await source.CleanupAfterFailureAsync(primaryFailure).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AdaptationCandidateResult> SubmitFollowUpTurnAsync(
        AdaptationFollowUpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await RunSessionOperationAsync(request.SessionId,
            () => SubmitFollowUpTurnAsyncCore(request, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdaptationCandidateResult> SubmitFollowUpTurnAsyncCore(
        AdaptationFollowUpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = GetSession(request.SessionId);
        if (session.SavedResult is not null)
            throw new WorkflowValidationException("This candidate is already saved; only scratch cleanup remains.");
        if (session.PendingCandidate is not null)
            throw new WorkflowValidationException("The candidate save outcome is unresolved; retry the same save before another turn.");
        if (session.TurnHistory.Count(message => message.Role == "user") >= MaxTurnsPerSession)
            throw new WorkflowValidationException("The adaptation turn limit was reached; save or discard this session.");
        // Clear evidence before any mutation; a failed turn must never save the prior result with new bytes.
        session.LastParsed = null;
        session.LastResult = null;
        session.AllowExpandedSemanticScope = request.AllowExpandedSemanticScope;
        session.SystemPrompt = _promptBuilder.BuildSystemPrompt(session.Goal, request.AllowExpandedSemanticScope);

        await ResetCandidateWorkspaceAsync(session, cancellationToken).ConfigureAwait(false);

        var completedHistoryCount = session.TurnHistory.Count;
        try
        {
            session.TurnHistory.Add(new AdaptationTurnMessage("user", request.Prompt));
            var parsed = await InvokeTurnAsync(session, cancellationToken).ConfigureAwait(false);
            var applyIssues = await ApplyFileModificationsAsync(
                session.CandidateWorkspace!,
                parsed.FileModifications,
                cancellationToken)
            .ConfigureAwait(false);
            session.LastParsed = parsed;
            var result = await ValidateCandidateAsync(session, applyIssues, cancellationToken)
            .ConfigureAwait(false);
            session.LastResult = result;
            session.UpdatedAtUtc = _timeProvider.GetUtcNow();
            return result;
        }
        catch
        {
            session.TurnHistory.RemoveRange(completedHistoryCount, session.TurnHistory.Count - completedHistoryCount);
            session.LastParsed = null;
            session.LastResult = null;
            throw;
        }
    }

    public Task<SaveCandidateVersionResult> SaveCandidateVersionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        RunSessionOperationAsync(sessionId, () => SaveCandidateSerializedAsync(sessionId, cancellationToken), cancellationToken);

    private async Task<SaveCandidateVersionResult> SaveCandidateSerializedAsync(string sessionId, CancellationToken cancellationToken)
    {
        // Serialize local packaging/persistence; authoritative number allocation lives in the store.
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await SaveCandidateVersionAsyncCore(sessionId, cancellationToken).ConfigureAwait(false); }
        finally { _saveGate.Release(); }
    }

    private async Task<SaveCandidateVersionResult> SaveCandidateVersionAsyncCore(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = GetSession(sessionId);
        if (session.SavedResult is not null)
            return await CompleteSavedSessionAsync(session).ConfigureAwait(false);
        var candidateWorkspace = session.CandidateWorkspace
            ?? throw new WorkflowValidationException("The adaptation session has no candidate workspace.");

        if (!Directory.Exists(candidateWorkspace.DirectoryPath))
        {
            throw new WorkflowValidationException("The candidate scratch workspace no longer exists.");
        }

        var parsed = session.LastParsed
            ?? throw new WorkflowValidationException("The adaptation session has no parsed model result.");
        if (session.LastResult is null)
            throw new WorkflowValidationException("The latest adaptation turn has not completed validation.");

        if (session.PendingCandidate is null)
        {
            var blob = await PackageCandidateBlobAsync(candidateWorkspace, cancellationToken).ConfigureAwait(false);
            var createdAtUtc = _timeProvider.GetUtcNow();
            var mappedRoles = parsed.Mappings
                .Select(mapping => mapping.Role)
                .Distinct()
                .OrderBy(role => role)
                .ToArray();

            session.PendingCandidate = new WorkflowVersion(
                Guid.NewGuid().ToString(),
                session.SourceVersion.WorkflowPackageId,
                1, // Prototype only; the repository allocates the authoritative number inside its transaction.
                blob.BlobId,
                blob.BlobId,
                WorkflowSourceType.SyntheticDraft,
                session.SourceVersion.EntrypointsJson,
                mappedRoles.Length > 0
                    ? JsonSerializer.Serialize(mappedRoles.Select(role => role.ToString()).ToArray())
                    : session.SourceVersion.DeclaredRolesJson,
                JsonSerializer.Serialize(parsed.Mappings, ReportJsonOptions),
                BuildCompatibilityReportJson(session, parsed, createdAtUtc),
                BuildCreationMetadataJson(session, parsed, createdAtUtc),
                createdAtUtc,
                activatedAtUtc: null);
        }
        // Retain this exact identity/content before awaiting storage: even a post-commit exception must
        // replay one candidate. This is a retained live-session repair, not crash rehydration/outbox.
        var version = await _versionRepository.InsertCandidateAsync(session.PendingCandidate, cancellationToken).ConfigureAwait(false);

        // Record the committed identity before any cleanup that may fail. A retry must never insert again.
        session.SavedResult = new SaveCandidateVersionResult(
            version.Id,
            version.WorkflowPackageId,
            version.VersionNumber,
            version.BlobId,
            version.SourceType,
            version.CreatedAtUtc,
            version.ActivatedAtUtc,
            session.LastResult?.Blockers ?? Array.Empty<AdaptationBlockerKind>());
        return await CompleteSavedSessionAsync(session).ConfigureAwait(false);
    }

    private async Task<SaveCandidateVersionResult> CompleteSavedSessionAsync(AdaptationSession session)
    {
        var cleaned = await TryCleanupSessionAsync(session).ConfigureAwait(false);
        if (cleaned) RemoveSession(session);
        return session.SavedResult! with { CleanupPending = !cleaned };
    }

    private static async Task<bool> TryCleanupSessionAsync(AdaptationSession session)
    {
        try { await CleanupSessionWorkspacesAsync(session).ConfigureAwait(false); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Keep the session/capacity for an explicit retry; callers distinguish commit from cleanup.
            return false;
        }
    }

    public async Task DiscardSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        // Expiry or another closer may already have completed cleanup for an open dialog.
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.ContainsKey(sessionId)) return;
            await CleanupSessionWorkspacesAsync(session).ConfigureAwait(false);
            RemoveSession(session);
        }
        finally { session.Gate.Release(); }
    }

    private async Task<T> RunSessionOperationAsync<T>(string sessionId, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var session = GetSession(sessionId);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Save/discard/expiry may remove it while this caller waits for the gate.
            _ = GetSession(sessionId);
            return await operation().ConfigureAwait(false);
        }
        finally { session.Gate.Release(); }
    }

    public AdaptationSessionSnapshot GetSessionSnapshot(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var session = GetSession(sessionId);
        if (!session.Gate.Wait(0))
            throw new WorkflowValidationException("The adaptation session is busy; retry after the current operation.");
        try { return GetSessionSnapshotCore(sessionId); }
        finally { session.Gate.Release(); }
    }

    private AdaptationSessionSnapshot GetSessionSnapshotCore(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = GetSession(sessionId);

        return new AdaptationSessionSnapshot(
            session.SessionId,
            session.SourceVersion.Id,
            session.SourceVersion.VersionNumber,
            session.AdapterRouteId,
            session.AdapterModelId,
            session.Goal,
            session.AllowExpandedSemanticScope,
            session.TurnHistory.Count(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)),
            session.TurnHistory.ToArray(),
            session.LastResult?.Blockers ?? Array.Empty<AdaptationBlockerKind>(),
            session.CreatedAtUtc,
            session.UpdatedAtUtc);
    }

    private async Task<AdaptationParsedResponse> InvokeTurnAsync(
        AdaptationSession session,
        CancellationToken cancellationToken)
    {
        // The identity authorized once at start is the only identity used for invocation, on the first
        // turn and on every follow-up turn: the caller-supplied route string stays provenance, and is
        // never the route the backend is asked to resolve for itself.
        var request = new AdaptationModelRequest(
            session.AdapterRoute.RouteId,
            session.AdapterModelId,
            session.SystemPrompt,
            session.TurnHistory.ToArray()) { ProjectId = session.ProjectId, SourceVersionId = session.SourceVersion.Id };
        ValidatePromptWireBudget(request);
        if (_egressPolicy is not null)
            request = request with { AdmissionId = await _egressPolicy.PrepareAsync(request,cancellationToken).ConfigureAwait(false) };

        var response = await _modelInvoker
            .InvokeModelAsync(request, cancellationToken)
            .ConfigureAwait(false);

        session.TurnHistory.Add(new AdaptationTurnMessage("assistant", response.RawText));

        return _responseParser.Parse(response.RawText);
    }

    private static void ValidatePromptWireBudget(AdaptationModelRequest request)
    {
        // Bound the components before joining retained history, then measure the exact native JSON
        // envelope (including escaping and model metadata). The final transport repeats its guard.
        long componentBytes = EscapedPromptBytes(request.SystemPrompt);
        foreach (var message in request.Messages)
        {
            componentBytes += EscapedPromptBytes(message.Content);
            if (componentBytes > SqliteAdaptationTransportPolicy.MaxWireBytes) throw PromptLimitExceeded();
        }
        var payload = OpenCodeSessionJson.SerializePromptRequest(new OpenCodePromptRequest
        {
            Prompt = AdaptationPromptEnvelope.Format(request), Model = request.ModelId, Agent = "plan"
        });
        if (Encoding.UTF8.GetByteCount(payload) > SqliteAdaptationTransportPolicy.MaxWireBytes)
            throw PromptLimitExceeded();
    }

    private static int EscapedPromptBytes(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > SqliteAdaptationTransportPolicy.MaxWireBytes)
            throw PromptLimitExceeded();
        return JsonEncodedText.Encode(text).EncodedUtf8Bytes.Length;
    }

    private static WorkflowValidationException PromptLimitExceeded() =>
        new("The serialized adaptation prompt exceeds the transport limit; exclude content or shorten the conversation.");

    private async Task CreateSessionWorkspacesAsync(
        AdaptationSession session,
        CancellationToken cancellationToken)
    {
        var sourceWorkspace = await _scratchWorkspaceManager
            .CreateWorkspaceAsync(ScratchScope.Adaptation, session.SourceScopeId, cancellationToken)
            .ConfigureAwait(false);
        session.SourceWorkspace = sourceWorkspace;

        await _scratchWorkspaceManager
            .ExtractBlobToWorkspaceAsync(session.SourceVersion.BlobId, sourceWorkspace, cancellationToken)
            .ConfigureAwait(false);
        await _scratchWorkspaceManager
            .PostOperationSourceHashCheckAsync(session.SourceVersion.BlobId, cancellationToken)
            .ConfigureAwait(false);

        var candidateWorkspace = await _scratchWorkspaceManager
            .CreateWorkspaceAsync(ScratchScope.Adaptation, session.CandidateScopeId, cancellationToken)
            .ConfigureAwait(false);
        session.CandidateWorkspace = candidateWorkspace;

        await _scratchWorkspaceManager
            .ExtractBlobToWorkspaceAsync(session.SourceVersion.BlobId, candidateWorkspace, cancellationToken)
            .ConfigureAwait(false);
        await _scratchWorkspaceManager
            .PostOperationSourceHashCheckAsync(session.SourceVersion.BlobId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ResetCandidateWorkspaceAsync(
        AdaptationSession session,
        CancellationToken cancellationToken)
    {
        if (session.CandidateWorkspace is not null)
        {
            await session.CandidateWorkspace
                .CleanupWorkspaceAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }

        var candidateWorkspace = await _scratchWorkspaceManager
            .CreateWorkspaceAsync(ScratchScope.Adaptation, session.CandidateScopeId, cancellationToken)
            .ConfigureAwait(false);
        session.CandidateWorkspace = candidateWorkspace;

        await _scratchWorkspaceManager
            .ExtractBlobToWorkspaceAsync(session.SourceVersion.BlobId, candidateWorkspace, cancellationToken)
            .ConfigureAwait(false);
        await _scratchWorkspaceManager
            .PostOperationSourceHashCheckAsync(session.SourceVersion.BlobId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AdaptationPromptContext> BuildPromptContextAsync(
        AdaptationSession session,
        IReadOnlyList<string>? userExcludedFiles,
        CancellationToken cancellationToken)
    {
        var workspace = session.CandidateWorkspace
            ?? throw new WorkflowValidationException("The adaptation session has no candidate workspace.");

        var scanReport = await _secretScanner
            .ScanScratchWorkspaceAsync(workspace, cancellationToken)
            .ConfigureAwait(false);

        var excludedFiles = BuildExcludedFiles(scanReport, userExcludedFiles);

        var (_, fileContents) = await ReadIncludedFilesAsync(
                workspace.DirectoryPath,
                excludedFiles,
                cancellationToken)
            .ConfigureAwait(false);

        return new AdaptationPromptContext(
            session.SourceVersion.Id,
            session.SourcePackage.Name,
            session.Goal,
            session.Catalog ?? throw new WorkflowValidationException("The adaptation session has no sanitized catalog."),
            fileContents,
            excludedFiles,
            session.AllowExpandedSemanticScope);
    }

    private async Task<IReadOnlyList<AdaptationValidationIssue>> ApplyFileModificationsAsync(
        ScratchWorkspace workspace,
        IReadOnlyDictionary<string, string> fileModifications,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> writes;
        try
        {
            writes = WorkflowModificationBatch.Prepare(workspace.DirectoryPath, fileModifications, cancellationToken);
        }
        catch (Exception exception) when (exception is WorkflowValidationException or PathTraversalException
            or IOException or UnauthorizedAccessException)
        {
            return [new AdaptationValidationIssue(AdaptationBlockerKind.Other, role: null,
                "File modification batch is not allowed: invalid paths, unavailable workspace or exceeded size/file count limits.")];
        }
        foreach (var pair in writes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedPath = pair.Key;
            InputSanitizer.EnsureNoReparsePoints(workspace.DirectoryPath, resolvedPath);
            var parentDirectory = Path.GetDirectoryName(resolvedPath);

            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            await AtomicFile
                .WriteAllBytesAsync(resolvedPath, Utf8WithoutBom.GetBytes(pair.Value), cancellationToken, overwrite: true)
                .ConfigureAwait(false);
        }

        return [];
    }

    private async Task<AdaptationCandidateResult> ValidateCandidateAsync(
        AdaptationSession session,
        IReadOnlyList<AdaptationValidationIssue> applyIssues,
        CancellationToken cancellationToken)
    {
        var parsed = session.LastParsed
            ?? throw new WorkflowValidationException("The adaptation session has no parsed model result.");
        var candidateWorkspace = session.CandidateWorkspace
            ?? throw new WorkflowValidationException("The adaptation session has no candidate workspace.");
        var sourceWorkspace = session.SourceWorkspace
            ?? throw new WorkflowValidationException("The adaptation session has no source workspace.");

        var scanReport = await _secretScanner
            .ScanScratchWorkspaceAsync(candidateWorkspace, cancellationToken)
            .ConfigureAwait(false);

        var catalog = await _catalogProvider
            .GetSanitizedCatalogAsync(cancellationToken)
            .ConfigureAwait(false);
        session.Catalog = catalog;

        var referenceValidation = _referenceValidator.Validate(parsed.Mappings, catalog);

        var packageDiff = await _diffService
            .CompareAsync(sourceWorkspace.DirectoryPath, candidateWorkspace.DirectoryPath, cancellationToken)
            .ConfigureAwait(false);

        // The semantic guard reads the real packages, not the model-declared isSemanticChange flag: a
        // stage, quality-gate, escalation or role-prose rewrite is a blocker even when the model denies it.
        var semanticDiff = _semanticDiffEngine.Compare(new SemanticDiffRequest(
            session.SourceVersion.DeclaredRolesJson,
            parsed.Mappings,
            session.AllowExpandedSemanticScope,
            SemanticPackageAnalyzer.Read(sourceWorkspace.DirectoryPath),
            SemanticPackageAnalyzer.Read(candidateWorkspace.DirectoryPath)));

        var blockingIssues = new List<AdaptationValidationIssue>();

        blockingIssues.AddRange(applyIssues);
        blockingIssues.AddRange(referenceValidation.Issues);
        blockingIssues.AddRange(semanticDiff.Issues);

        foreach (var finding in scanReport.Findings)
        {
            blockingIssues.Add(new AdaptationValidationIssue(
                AdaptationBlockerKind.DetectedSecret,
                role: null,
                $"Secret '{finding.RuleName}' detected at {finding.RelativePath}:{finding.LineNumber}."));
        }

        var blockers = CollectBlockers(parsed, blockingIssues);

        var candidateFiles = EnumerateWorkspaceFiles(candidateWorkspace.DirectoryPath)
            .Select(file => file.RelativePath)
            .ToArray();

        return new AdaptationCandidateResult(
            session.SessionId,
            session.SourceVersion.Id,
            session.SourceVersion.VersionNumber,
            session.AdapterRouteId,
            session.AdapterModelId,
            session.Goal,
            session.AllowExpandedSemanticScope,
            candidateWorkspace.DirectoryPath,
            parsed.Mappings,
            parsed.Rationale,
            parsed.Warnings,
            blockers,
            blockingIssues,
            scanReport,
            referenceValidation,
            semanticDiff,
            packageDiff,
            parsed.FileModifications,
            candidateFiles,
            parsed.ParseError);
    }

    private async Task<WorkflowBlob> PackageCandidateBlobAsync(
        ScratchWorkspace candidateWorkspace,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(
            candidateWorkspace.DirectoryPath,
            "llmworkgui-adaptation-" + Guid.NewGuid().ToString("N") + ".zip");
        InputSanitizer.EnsureNoReparsePoints(Path.GetPathRoot(tempPath)!, tempPath);

        try
        {
            await using var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            await WriteDeterministicZipAsync(candidateWorkspace.DirectoryPath, tempPath, stream, cancellationToken)
                .ConfigureAwait(false);

            stream.Position = 0;

            return await _blobStore.SaveBlobAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static async Task WriteDeterministicZipAsync(
        string directory,
        string containerPath,
        Stream destination,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var (relativePath, fullPath) in EnumerateWorkspaceFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Exclude only this exact owned output. Other candidate ZIP files are still real inputs.
            if (string.Equals(Path.GetFullPath(fullPath), Path.GetFullPath(containerPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                continue;

            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = DeterministicEntryTimestamp;

            await using var entryStream = entry.Open();
            await using var source = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        }
    }

    private string BuildCompatibilityReportJson(
        AdaptationSession session,
        AdaptationParsedResponse parsed,
        DateTimeOffset generatedAtUtc)
    {
        var result = session.LastResult
            ?? throw new WorkflowValidationException("The adaptation session has no validated candidate.");

        var report = new
        {
            sessionId = session.SessionId,
            sourceVersionId = session.SourceVersion.Id,
            goal = session.Goal.ToWireName(),
            adapterRouteId = session.AdapterRouteId,
            adapterModelId = session.AdapterModelId,
            adapterAccountId = session.AdapterRoute.AccountId,
            adapterProviderProfileId = session.AdapterRoute.ProviderProfileId,
            adapterBackend = session.AdapterRoute.Backend.ToString(),
            rationale = parsed.Rationale,
            warnings = parsed.Warnings,
            blockers = result.Blockers.Select(blocker => blocker.ToString()).ToArray(),
            blockingIssues = result.BlockingIssues
                .Select(issue => new
                {
                    blockerKind = issue.Kind.ToString(),
                    role = issue.Role,
                    message = issue.Message,
                    isNotClearable = issue.IsNotClearable
                })
                .ToArray(),
            referenceIssues = result.ReferenceValidation.Issues
                .Select(issue => new
                {
                    blockerKind = issue.Kind.ToString(),
                    role = issue.Role,
                    message = issue.Message
                })
                .ToArray(),
            semanticChanges = result.SemanticDiff.Changes,
            // The pre-send scope flag is recorded as the prompt context it is, and never as an approval:
            // the candidate carries no recorded consent at all, so activation revalidates the real packages
            // and the operator acknowledges each freshly reported issue in the decision UI.
            expandedSemanticScopeRequested = session.AllowExpandedSemanticScope,
            secretFindings = result.SecretScanReport.Findings
                .Select(finding => new
                {
                    relativePath = finding.RelativePath,
                    lineNumber = finding.LineNumber,
                    ruleName = finding.RuleName
                })
                .ToArray(),
            diff = new
            {
                filesAdded = result.PackageDiff.TotalFilesAdded,
                filesModified = result.PackageDiff.TotalFilesModified,
                filesDeleted = result.PackageDiff.TotalFilesDeleted,
                linesAdded = result.PackageDiff.TotalLinesAdded,
                linesDeleted = result.PackageDiff.TotalLinesDeleted
            },
            generatedAtUtc
        };

        return JsonSerializer.Serialize(report, ReportJsonOptions);
    }

    private static string BuildCreationMetadataJson(
        AdaptationSession session,
        AdaptationParsedResponse parsed,
        DateTimeOffset createdAtUtc)
    {
        var metadata = new
        {
            goal = session.Goal.ToWireName(),
            adapterRouteId = session.AdapterRouteId,
            adapterModelId = session.AdapterModelId,
            adapterAccountId = session.AdapterRoute.AccountId,
            adapterProviderProfileId = session.AdapterRoute.ProviderProfileId,
            adapterBackend = session.AdapterRoute.Backend.ToString(),
            rationale = parsed.Rationale,
            sourceVersionId = session.SourceVersion.Id,
            sessionId = session.SessionId,
            createdAtUtc
        };

        return JsonSerializer.Serialize(metadata, ReportJsonOptions);
    }

    private static IReadOnlyList<AdaptationBlockerKind> CollectBlockers(
        AdaptationParsedResponse parsed,
        IReadOnlyList<AdaptationValidationIssue> issues)
    {
        var blockers = new List<AdaptationBlockerKind>();

        foreach (var blockerKind in parsed.BlockerKinds)
        {
            AddBlocker(blockers, blockerKind);
        }

        foreach (var issue in issues)
        {
            AddBlocker(blockers, issue.Kind);
        }

        return blockers;
    }

    private static void AddBlocker(List<AdaptationBlockerKind> blockers, AdaptationBlockerKind blockerKind)
    {
        if (!blockers.Contains(blockerKind))
        {
            blockers.Add(blockerKind);
        }
    }

    private async Task<(WorkflowVersion Version, WorkflowPackage Package)> ResolveVersionAndPackageAsync(
        string workflowVersionId,
        CancellationToken cancellationToken)
    {
        var version = await _versionRepository
            .GetByIdAsync(workflowVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WorkflowValidationException(
                $"The workflow version '{workflowVersionId}' does not exist.");

        var package = await _packageRepository
            .GetByIdAsync(version.WorkflowPackageId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WorkflowValidationException(
                $"The workflow package '{version.WorkflowPackageId}' does not exist.");

        return (version, package);
    }

    private AdaptationSession GetSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            return session;
        }

        throw new WorkflowValidationException($"The adaptation session '{sessionId}' does not exist.");
    }

    private static async Task CleanupSessionWorkspacesAsync(AdaptationSession session)
    {
        Exception? cleanupFailure = null;
        if (session.CandidateWorkspace is not null)
        {
            try { await session.CandidateWorkspace.CleanupWorkspaceAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { cleanupFailure = error; }
        }

        if (session.SourceWorkspace is not null)
        {
            try { await session.SourceWorkspace.CleanupWorkspaceAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { cleanupFailure ??= error; }
        }
        if (cleanupFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private static void ValidateGoal(AdaptationGoal goal)
    {
        if (!Enum.IsDefined(goal))
        {
            throw new ArgumentOutOfRangeException(nameof(goal), goal, "Unknown adaptation goal.");
        }
    }

    private static void TryDeleteFile(string path)
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

    private static IReadOnlyList<string> BuildExcludedFiles(
        WorkflowSecretScanReport scanReport,
        IReadOnlyList<string>? userExcludedFiles)
    {
        var excludedFiles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in scanReport.RecommendedExcludedFiles)
        {
            excludedFiles.Add(NormalizeRelativePath(path));
        }

        if (userExcludedFiles is not null)
        {
            foreach (var path in userExcludedFiles)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    excludedFiles.Add(NormalizeRelativePath(path));
                }
            }
        }

        return excludedFiles
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<(IReadOnlyList<string> IncludedFiles, IReadOnlyDictionary<string, string> FileContents)>
        ReadIncludedFilesAsync(
            string workspaceDirectory,
            IReadOnlyList<string> excludedFiles,
            CancellationToken cancellationToken)
    {
        var excluded = new HashSet<string>(excludedFiles, StringComparer.Ordinal);
        var includedFiles = new List<string>();
        var fileContents = new Dictionary<string, string>(StringComparer.Ordinal);
        long escapedContentBytes = 0;

        foreach (var (relativePath, fullPath) in EnumerateWorkspaceFiles(workspaceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (excluded.Contains(relativePath))
            {
                continue;
            }

            var content = await TryReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false);

            if (content is not null)
            {
                escapedContentBytes += EscapedPromptBytes(relativePath) + EscapedPromptBytes(content);
                if (escapedContentBytes > SqliteAdaptationTransportPolicy.MaxWireBytes) throw PromptLimitExceeded();
                includedFiles.Add(relativePath);
                fileContents[relativePath] = content;
            }
        }

        return (includedFiles, fileContents);
    }

    private static IReadOnlyList<(string RelativePath, string FullPath)> EnumerateWorkspaceFiles(
        string workspaceDirectory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        var files = new List<(string RelativePath, string FullPath)>();

        try
        {
            foreach (var fullPath in Directory.EnumerateFiles(workspaceDirectory, "*", options))
            {
                if (files.Count >= WorkflowImportLimits.MaxFileCount)
                    throw new WorkflowValidationException("The adaptation workspace exceeds the allowed file count.");
                files.Add((
                    NormalizeRelativePath(Path.GetRelativePath(workspaceDirectory, fullPath)),
                    fullPath));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new WorkflowValidationException("Part of the adaptation workspace could not be enumerated.", error);
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

        return files;
    }

    private static async Task<string?> TryReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[MaxPromptFileBytes + 1];
            var total = 0;

            while (total < buffer.Length)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            var hasUtf16Bom = buffer.AsSpan(0, total).StartsWith(Utf16LittleEndianPreamble)
                || buffer.AsSpan(0, total).StartsWith(Utf16BigEndianPreamble);
            // The scanner considers NUL anywhere in the complete bounded body. Never decode
            // a late-NUL body as UTF-8 after it was scanned under a different interpretation.
            if (!hasUtf16Bom && ContainsNullByte(buffer.AsSpan(0, total)))
            {
                return null;
            }

            if (total > MaxPromptFileBytes)
                throw new WorkflowValidationException("A source text file exceeds the adaptation prompt limit; exclude it explicitly.");

            return DecodeText(buffer.AsSpan(0, total));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new WorkflowValidationException("A source file could not be read for adaptation; exclude it explicitly.", error);
        }
    }

    private async Task<(string QuotaState, string QuotaFreshness, double? ReserveThreshold)>
        ResolveQuotaPreviewAsync(
            string adapterRouteId,
            string adapterModelId,
            SanitizedCapabilityCatalog catalog,
            CancellationToken cancellationToken)
    {
        var accountId = await ResolveRouteAccountIdAsync(adapterRouteId, catalog, cancellationToken)
            .ConfigureAwait(false);

        var snapshot = string.Equals(adapterModelId, accountId, StringComparison.Ordinal)
            ? null
            : await _quotaSnapshotRepository
                .GetLatestForAccountAsync(accountId, adapterModelId, cancellationToken)
                .ConfigureAwait(false);

        if (snapshot is null)
        {
            snapshot = await _quotaSnapshotRepository
                .GetLatestForAccountAsync(accountId, modelId: null, cancellationToken)
                .ConfigureAwait(false);
        }

        var account = await _accountRepository
            .GetByIdAsync(accountId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return (
                AdaptationPreSendPreview.UnknownQuotaValue,
                AdaptationPreSendPreview.UnknownQuotaValue,
                account?.ReserveThreshold);
        }

        var freshness = snapshot.IsFresh(_timeProvider.GetUtcNow())
            ? FreshQuotaValue
            : StaleQuotaValue;

        return (snapshot.Provenance.ToString(), freshness, account?.ReserveThreshold);
    }

    /// <summary>
    /// Resolves the route to its account, provider profile, backend and backend-native model id.
    /// The account and profile always come from the catalog rows, which the provider builds from the
    /// local repositories; the route string itself is never classified. A backend that cannot run
    /// adaptation is refused first, before any scratch workspace is created or any model is invoked.
    /// The model a route id carries is honoured only when the catalog row really has it, so a crafted
    /// route cannot name a model the account does not have; a route whose account has no real backend
    /// model id is refused as well, because an account id is never returned as a model id. This method
    /// is the authorization point: the identity it returns always carries a backend-native model id and
    /// is the only identity model invocation uses.
    /// </summary>
    private async Task<AdaptationRouteIdentity> ResolveAdapterRouteAsync(
        string adapterRouteId,
        SanitizedCapabilityCatalog catalog,
        CancellationToken cancellationToken)
    {
        // A route id carries the model the operator selected, so it is authoritative when it is present
        // — but only as a selection among the models the account really has.
        var routeCarriedModelId = AdaptationRouteIdentity.TryParse(adapterRouteId, out var parsedRoute)
            ? parsedRoute.BackendModelId
            : null;

        foreach (var accountId in AdaptationRouteIdentity.EnumerateCandidateAccountIds(adapterRouteId))
        {
            var row = catalog.Models.FirstOrDefault(model => string.Equals(
                model.AccountId ?? model.ModelId,
                accountId,
                StringComparison.Ordinal));

            if (row is null)
            {
                continue;
            }

            if (!row.IsRoutable)
                throw new WorkflowValidationException($"Catalog account '{accountId}' is not currently routable.");

            if (!AdaptationRouteIdentity.TryCreate(row, out var identity))
            {
                throw new WorkflowValidationException(
                    $"Catalog account '{accountId}' carries no complete route identity "
                    + "(account, provider profile and backend are all required).");
            }

            if (!identity.IsAdaptationCapable)
            {
                throw new WorkflowValidationException(
                    $"Adaptation is not supported for backend '{identity.Backend}'. Route '{adapterRouteId}' resolves to account "
                    + $"'{identity.AccountId}' on provider profile '{identity.ProviderProfileId}'; only the OpenCode backend can run "
                    + "adaptation, so the route is refused before any scratch workspace is created or any model is invoked.");
            }

            if (routeCarriedModelId is not null)
            {
                if (!BackendModelIdPolicy.TryNormalize(routeCarriedModelId, out var selectedModelId))
                {
                    throw new WorkflowValidationException(
                        $"Adaptation route '{adapterRouteId}' carries a model id that is not a model id. A filesystem path, a "
                        + "profile name or a secret reference is never sent as a model, so the route is refused.");
                }

                if (!row.SelectableBackendModelIds.Contains(selectedModelId, StringComparer.Ordinal))
                {
                    throw new WorkflowValidationException(
                        $"Adaptation route '{adapterRouteId}' names model '{selectedModelId}', which is not a model of account "
                        + $"'{accountId}'. A route may only select a model the account really has, so the route is refused.");
                }

                return identity.WithBackendModelId(selectedModelId);
            }

            if (identity.HasBackendModelId)
            {
                return identity;
            }

            var account = await LookupAccountAsync(accountId, cancellationToken).ConfigureAwait(false);

            if (account is { ProviderNativeId: { } nativeId }
                && BackendModelIdPolicy.TryNormalize(nativeId, out var recordModelId))
            {
                return identity.WithBackendModelId(recordModelId);
            }

            // A model-less identity is never returned: preview and start both refuse here, so no scratch
            // workspace, extraction or backend turn can ever happen without an authorized model id.
            throw MissingBackendModelId(adapterRouteId, identity.AccountId);
        }

        throw new WorkflowValidationException(
            $"Adaptation route '{adapterRouteId}' could not be resolved to a catalog account.");
    }

    private async Task<string> ResolveAdapterModelIdAsync(
        string adapterRouteId,
        SanitizedCapabilityCatalog catalog,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveAdapterRouteAsync(adapterRouteId, catalog, cancellationToken)
            .ConfigureAwait(false);

        if (!identity.HasBackendModelId)
        {
            throw MissingBackendModelId(adapterRouteId, identity.AccountId);
        }

        return identity.BackendModelId!;
    }

    private static WorkflowValidationException MissingBackendModelId(string adapterRouteId, string accountId) =>
        new($"Adaptation route '{adapterRouteId}' resolves to account '{accountId}', which has no "
            + "backend-native model id. An account id is never sent as a model id, so the route is refused.");

    private async Task<string> ResolveRouteAccountIdAsync(
        string adapterRouteId,
        SanitizedCapabilityCatalog catalog,
        CancellationToken cancellationToken)
    {
        foreach (var accountId in AdaptationRouteIdentity.EnumerateCandidateAccountIds(adapterRouteId))
        {
            var inCatalog = catalog.Models.Any(model => string.Equals(
                model.AccountId ?? model.ModelId,
                accountId,
                StringComparison.Ordinal));

            if (inCatalog || await LookupAccountAsync(accountId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return accountId;
            }
        }

        return AdaptationRouteIdentity.TryParse(adapterRouteId, out var identity)
            ? identity.AccountId
            : adapterRouteId;
    }

    private async Task<Account?> LookupAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        if (_accountRepository is null)
        {
            return null;
        }

        return await _accountRepository
            .GetByIdAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildScopeId(string versionId)
    {
        var builder = new StringBuilder(Math.Min(versionId.Length, MaxScopeIdLength));

        foreach (var character in versionId)
        {
            if (builder.Length == MaxScopeIdLength)
            {
                break;
            }

            builder.Append(IsAllowedScopeIdCharacter(character) ? character : '-');
        }

        return builder.ToString();
    }

    private static bool IsAllowedScopeIdCharacter(char character)
    {
        return character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-'
            or '_'
            or '.';
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static bool ContainsNullByte(ReadOnlySpan<byte> bytes)
    {
        return bytes.IndexOf((byte)0) >= 0;
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

    private sealed class AdaptationSession
    {
        public string? ProjectId { get; set; }
        // Do not dispose while queued callers can still hold a reference to this session.
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public AdaptationSession(
            string sessionId,
            string candidateScopeId,
            string sourceScopeId,
            WorkflowVersion sourceVersion,
            WorkflowPackage sourcePackage,
            string adapterRouteId,
            AdaptationGoal goal,
            bool allowExpandedSemanticScope,
            DateTimeOffset createdAtUtc)
        {
            SessionId = sessionId;
            CandidateScopeId = candidateScopeId;
            SourceScopeId = sourceScopeId;
            SourceVersion = sourceVersion;
            SourcePackage = sourcePackage;
            AdapterRouteId = adapterRouteId;
            Goal = goal;
            AllowExpandedSemanticScope = allowExpandedSemanticScope;
            CreatedAtUtc = createdAtUtc;
            UpdatedAtUtc = createdAtUtc;
        }

        public string SessionId { get; }

        public string CandidateScopeId { get; }

        public string SourceScopeId { get; }

        public WorkflowVersion SourceVersion { get; }

        public WorkflowPackage SourcePackage { get; }

        public string AdapterRouteId { get; }

        public AdaptationGoal Goal { get; }

        public bool AllowExpandedSemanticScope { get; set; }

        public DateTimeOffset CreatedAtUtc { get; }

        public DateTimeOffset UpdatedAtUtc { get; set; }

        /// <summary>
        /// Resolved route provenance: account, provider profile, backend and backend-native model id.
        /// Assigned from the live catalog before the first turn and never derived from the route string.
        /// </summary>
        public AdaptationRouteIdentity AdapterRoute => _adapterRoute
            ?? throw new WorkflowValidationException(
                $"Adaptation route '{AdapterRouteId}' has not been resolved to a route identity.");

        /// <summary>
        /// The backend-native model id sent to the backend. The account id is never substituted for a
        /// missing native model id: a route without one is refused.
        /// </summary>
        public string AdapterModelId => AdapterRoute.BackendModelId
            ?? throw new WorkflowValidationException(
                $"Adaptation route '{AdapterRouteId}' has no backend-native model id; "
                + "an account id is never sent as a model id.");

        public void SetAdapterRoute(AdaptationRouteIdentity identity) => _adapterRoute = identity;

        private AdaptationRouteIdentity? _adapterRoute;

        public string SystemPrompt { get; set; } = string.Empty;

        public SanitizedCapabilityCatalog? Catalog { get; set; }

        public ScratchWorkspace? SourceWorkspace { get; set; }

        public ScratchWorkspace? CandidateWorkspace { get; set; }

        public List<AdaptationTurnMessage> TurnHistory { get; } = new();

        public AdaptationParsedResponse? LastParsed { get; set; }

        public AdaptationCandidateResult? LastResult { get; set; }

        public SaveCandidateVersionResult? SavedResult { get; set; }

        public WorkflowVersion? PendingCandidate { get; set; }
    }
}
