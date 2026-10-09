using System.IO.Compression;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// Direct service evidence for the early capability refusal and for the authorized identity handoff.
/// <see cref="WorkflowAdaptationService"/> is called directly, with a real
/// <see cref="SanitizedCatalogProvider"/>, a real <see cref="BackendAdaptationModelInvoker"/> and a
/// recording OpenCode lifecycle, on accounts of Cursor, Mirasim, StarCliProxy and Agy that do carry a
/// non-null native id. A backend that cannot run adaptation must be refused before a scratch workspace
/// exists and before any OpenCode session is created — not merely hidden by the UI — and a StarCli
/// home path must never be sent as a model. The same harness shows the other half: a bare OpenCode
/// account id that resolved a catalog model must reach the wire as that model on the first turn and
/// on every follow-up turn, and an account with no model at all is refused on start exactly as it is
/// on preview, before any scratch work.
/// </summary>
public sealed class WorkflowAdaptationCapabilityRefusalTests : IDisposable
{
    private const string StarCliHomePath = @"C:\Users\tester\.codex\home";
    private const string AgyProfileName = "work";

    private readonly TestDatabase _database = new();
    private readonly WorkflowBlobStore _blobStore;
    private readonly SqliteWorkflowPackageRepository _packageRepository;
    private readonly SqliteWorkflowVersionRepository _versionRepository;
    private readonly SqliteProviderProfileRepository _providerRepository;
    private readonly SqliteAccountRepository _accountRepository;
    private readonly SqliteHealthStateRepository _healthStateRepository;
    private readonly SqliteQuotaSnapshotRepository _quotaSnapshotRepository;
    private readonly SqliteApplicationSettingsRepository _settingsRepository;
    private readonly RecordingScratchWorkspaceManager _scratchManager;
    private readonly RecordingOpenCodeLifecycle _lifecycle = new();
    private readonly ApplicationSettingsProviderModelCatalog _modelCatalog;
    private readonly WorkflowAdaptationService _service;

    public WorkflowAdaptationCapabilityRefusalTests()
    {
        _blobStore = new WorkflowBlobStore(_database.Root);
        _packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        _versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);
        _providerRepository = new SqliteProviderProfileRepository(_database.Factory);
        _accountRepository = new SqliteAccountRepository(_database.Factory);
        _healthStateRepository = new SqliteHealthStateRepository(_database.Factory);
        _quotaSnapshotRepository = new SqliteQuotaSnapshotRepository(_database.Factory);
        _settingsRepository = new SqliteApplicationSettingsRepository(_database.Factory);
        _modelCatalog = new ApplicationSettingsProviderModelCatalog(_settingsRepository, new SensitiveDataFilter());

        _scratchManager = new RecordingScratchWorkspaceManager(
            new ScratchWorkspaceManager(_blobStore, new SafeArchiveValidator()));

        _service = new WorkflowAdaptationService(
            _versionRepository,
            _packageRepository,
            _scratchManager,
            new WorkflowSecretScanner(new SensitiveDataFilter()),
            new WorkflowAdaptationPromptBuilder(),
            new SanitizedCatalogProvider(
                _providerRepository,
                _accountRepository,
                _healthStateRepository,
                _modelCatalog),
            _quotaSnapshotRepository,
            _accountRepository,
            new AdaptationProtocolFixtureInvoker(
                _lifecycle,
                new RecordingOpenCodeClient(),
                _providerRepository,
                _accountRepository,
                // Synthetic model/route protocol fixture; real material authority is tested separately.
                egressPolicy:new BackendAdaptationModelInvokerTests.FixtureProtocolPolicy()),
            new AdaptationResponseParser(),
            new AdaptationReferenceValidator(),
            new SemanticDiffEngine(),
            new WorkflowDiffService(),
            _blobStore,
            TimeProvider.System);
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    public static TheoryData<string, BackendType, string> NonAdaptationBackends => new()
    {
        { "acct-cursor", BackendType.CursorAcp, "opencode/space-bunny-free" },
        { "acct-mirasim", BackendType.Mirasim, "mirasim/local-model" },
        { "acct-starcli", BackendType.StarCliProxy, StarCliHomePath },
        { "acct-agy", BackendType.Agy, AgyProfileName }
    };

    [Theory]
    [MemberData(nameof(NonAdaptationBackends))]
    public async Task PreparePreSendPreviewAsync_RefusesNonAdaptationBackendBeforeScratchWork(
        string accountId,
        BackendType backend,
        string nativeId)
    {
        await _database.InitializeAsync();
        await SeedAsync(accountId, backend, nativeId);
        var (_, version) = await SeedWorkflowAsync("version-1");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.PreparePreSendPreviewAsync(version.Id, accountId, AdaptationGoal.Balanced));

        Assert.Contains(backend.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("not supported for backend", exception.Message, StringComparison.Ordinal);

        // The refusal happens before any scratch workspace is created or extracted.
        Assert.Empty(_scratchManager.CreatedScopes);
        Assert.Empty(_scratchManager.CreatedScopeIds);
        Assert.Empty(_scratchManager.ExtractedBlobIds);
        Assert.Equal(0, _scratchManager.PostOperationHashCheckCount);

        // And before any OpenCode request object exists.
        Assert.Empty(_lifecycle.CreateRequests);
        Assert.Empty(_lifecycle.TurnRequests);
    }

    [Theory]
    [MemberData(nameof(NonAdaptationBackends))]
    public async Task StartAdaptationAsync_RefusesNonAdaptationBackendBeforeScratchWorkAndInvocation(
        string accountId,
        BackendType backend,
        string nativeId)
    {
        await _database.InitializeAsync();
        await SeedAsync(accountId, backend, nativeId);
        var (_, version) = await SeedWorkflowAsync("version-1");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.StartAdaptationAsync(new AdaptationExecutionRequest(
                version.Id,
                accountId,
                AdaptationGoal.Balanced)));

        Assert.Contains(backend.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("not supported for backend", exception.Message, StringComparison.Ordinal);

        Assert.Empty(_scratchManager.CreatedScopes);
        Assert.Empty(_scratchManager.ExtractedBlobIds);
        Assert.Empty(_lifecycle.CreateRequests);
        Assert.Empty(_lifecycle.TurnRequests);
    }

    [Fact]
    public async Task NonAdaptationBackend_IsRefusedEvenWithARealConfiguredModel()
    {
        await _database.InitializeAsync();
        await SeedAsync("acct-cursor", BackendType.CursorAcp, "opencode/space-bunny-free");

        // A real, locally configured model id exists for this profile and is still refused: a capable
        // model does not make an incapable backend runnable.
        await _modelCatalog.SaveModelsAsync(
            new ProviderModelCatalogQuery("prov-1", BackendType.CursorAcp),
            new[] { new ProviderModelDescriptor("opencode/space-bunny-free") });

        var (_, version) = await SeedWorkflowAsync("version-1");

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.PreparePreSendPreviewAsync(version.Id, "acct-cursor", AdaptationGoal.Balanced));

        Assert.Empty(_scratchManager.CreatedScopes);
        Assert.Empty(_lifecycle.CreateRequests);
    }

    [Fact]
    public async Task StarCliHomePathAndAgyProfileName_NeverBecomeSelectableModelIds()
    {
        await _database.InitializeAsync();
        await SeedAsync("acct-starcli", BackendType.StarCliProxy, StarCliHomePath);
        await SeedAsync("acct-agy", BackendType.Agy, AgyProfileName);

        var catalog = await new SanitizedCatalogProvider(
                _providerRepository,
                _accountRepository,
                _healthStateRepository,
                _modelCatalog)
            .GetSanitizedCatalogAsync();

        var starCli = Assert.Single(catalog.Models, model => model.AccountId == "acct-starcli");
        var agy = Assert.Single(catalog.Models, model => model.AccountId == "acct-agy");

        // A filesystem path is rejected lexically, and a bare profile name cannot be told apart from a
        // model id, so on a backend that cannot run adaptation the account record is structurally not a
        // model source. Neither value is ever offered as a route model.
        Assert.False(BackendModelIdPolicy.IsSelectableModelId(StarCliHomePath));
        Assert.Empty(starCli.SelectableBackendModelIds);
        Assert.Empty(agy.SelectableBackendModelIds);
    }

    [Fact]
    public async Task OpenCodeAccountWithoutNativeId_IsUsableThroughItsConfiguredModel()
    {
        await _database.InitializeAsync();
        await SeedAsync("acct-oc", BackendType.OpenCode, providerNativeId: null);

        // The production normal OpenCode account shape carries no ProviderNativeId at all.
        var before = await new SanitizedCatalogProvider(
                _providerRepository,
                _accountRepository,
                _healthStateRepository,
                _modelCatalog)
            .GetSanitizedCatalogAsync();

        Assert.Empty(Assert.Single(before.Models).SelectableBackendModelIds);

        await _modelCatalog.SaveModelsAsync(
            new ProviderModelCatalogQuery("prov-1", BackendType.OpenCode),
            new[] { new ProviderModelDescriptor("opencode/space-bunny-free", "Space Bunny Free") });

        var after = await new SanitizedCatalogProvider(
                _providerRepository,
                _accountRepository,
                _healthStateRepository,
                _modelCatalog)
            .GetSanitizedCatalogAsync();

        var row = Assert.Single(after.Models);
        Assert.Equal(new[] { "opencode/space-bunny-free" }, row.SelectableBackendModelIds);
        Assert.Equal("opencode/space-bunny-free", row.BackendModelId);
        Assert.Equal(BackendType.OpenCode, row.Backend);
        Assert.Equal("prov-1", row.ProviderProfileId);
    }

    [Fact]
    public async Task OpenCodeAccountWithoutNativeIdAndWithoutConfiguredModel_IsRefused()
    {
        await _database.InitializeAsync();
        await SeedAsync("acct-oc", BackendType.OpenCode, providerNativeId: null);
        var (_, version) = await SeedWorkflowAsync("version-1");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.PreparePreSendPreviewAsync(version.Id, "acct-oc", AdaptationGoal.Balanced));

        Assert.Contains("no backend-native model id", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_lifecycle.CreateRequests);
    }

    [Fact]
    public async Task StartAdaptationAsync_BareAccountId_SendsTheCatalogAuthorizedModelOnCreateAndPrompt()
    {
        await _database.InitializeAsync();
        await SeedAsync("acct-oc", BackendType.OpenCode, providerNativeId: null);
        await _modelCatalog.SaveModelsAsync(
            new ProviderModelCatalogQuery("prov-1", BackendType.OpenCode),
            new[] { new ProviderModelDescriptor("opencode/space-bunny-free", "Space Bunny Free") });

        var (_, version) = await SeedWorkflowAsync("version-1");

        // The production start path sends the bare account id: the caller picked an account, not a
        // route. The service already resolved the model at that point, so invocation must carry the
        // identity it authorized rather than the string the caller happened to type.
        var result = await _service.StartAdaptationAsync(new AdaptationExecutionRequest(
            version.Id,
            "acct-oc",
            AdaptationGoal.Balanced));

        // The caller's string stays provenance on the result.
        Assert.Equal("acct-oc", result.AdapterRouteId);
        Assert.Equal("opencode/space-bunny-free", result.AdapterModelId);

        var createRequest = Assert.Single(_lifecycle.CreateRequests);
        var turn = Assert.Single(_lifecycle.TurnRequests);

        Assert.Equal("opencode/space-bunny-free", createRequest.Model);
        Assert.Equal("opencode/space-bunny-free", turn.Request.Model);
        Assert.NotEqual("acct-oc", createRequest.Model);
        Assert.NotEqual("acct-oc", turn.Request.Model);
    }

    [Fact]
    public async Task SubmitFollowUpTurnAsync_KeepsTheCatalogAuthorizedModelOnTheWire()
    {
        await _database.InitializeAsync();
        await SeedAsync("acct-oc", BackendType.OpenCode, providerNativeId: null);
        await _modelCatalog.SaveModelsAsync(
            new ProviderModelCatalogQuery("prov-1", BackendType.OpenCode),
            new[] { new ProviderModelDescriptor("opencode/space-bunny-free", "Space Bunny Free") });

        var (_, version) = await SeedWorkflowAsync("version-1");

        var first = await _service.StartAdaptationAsync(new AdaptationExecutionRequest(
            version.Id,
            "acct-oc",
            AdaptationGoal.Balanced));

        await _service.SubmitFollowUpTurnAsync(
            new AdaptationFollowUpRequest(first.SessionId, "Please re-check the README."));

        // The follow-up turn resolves the same authorized identity, so every turn of the session names
        // the same real model — never the account id the operator selected.
        Assert.Equal(2, _lifecycle.CreateRequests.Count);
        Assert.Equal(2, _lifecycle.TurnRequests.Count);
        Assert.All(_lifecycle.CreateRequests, request => Assert.Equal("opencode/space-bunny-free", request.Model));
        Assert.All(_lifecycle.TurnRequests, turn => Assert.Equal("opencode/space-bunny-free", turn.Request.Model));
        Assert.All(
            _lifecycle.CreateRequests,
            request => Assert.NotEqual("acct-oc", request.Model));
    }

    [Fact]
    public async Task StartAdaptationAsync_AccountWithoutConfiguredModel_RefusesBeforeScratchWorkAndInvocation()
    {
        await _database.InitializeAsync();
        await SeedAsync("acct-oc", BackendType.OpenCode, providerNativeId: null);
        var (_, version) = await SeedWorkflowAsync("version-1");

        var startException = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.StartAdaptationAsync(new AdaptationExecutionRequest(
                version.Id,
                "acct-oc",
                AdaptationGoal.Balanced)));

        var previewException = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.PreparePreSendPreviewAsync(version.Id, "acct-oc", AdaptationGoal.Balanced));

        // Start and preview share the one refusal, and it happens before a scratch workspace exists.
        Assert.Equal(previewException.Message, startException.Message);
        Assert.Contains("no backend-native model id", startException.Message, StringComparison.Ordinal);

        Assert.Empty(_scratchManager.CreatedScopes);
        Assert.Empty(_scratchManager.CreatedScopeIds);
        Assert.Empty(_scratchManager.ExtractedBlobIds);
        Assert.Equal(0, _scratchManager.PostOperationHashCheckCount);

        Assert.Empty(_lifecycle.CreateRequests);
        Assert.Empty(_lifecycle.TurnRequests);
    }

    [Fact]
    public async Task ConfiguredModelIsReachedOnTheOpenCodeRequestObjects()
    {
        await _database.InitializeAsync();
        await SeedAsync("acct-oc", BackendType.OpenCode, providerNativeId: null);
        await _modelCatalog.SaveModelsAsync(
            new ProviderModelCatalogQuery("prov-1", BackendType.OpenCode),
            new[] { new ProviderModelDescriptor("opencode/space-bunny-free", "Space Bunny Free") });

        var (_, version) = await SeedWorkflowAsync("version-1");

        var identity = new AdaptationRouteIdentity("acct-oc", "prov-1", BackendType.OpenCode, "opencode/space-bunny-free");

        var preview = await _service.PreparePreSendPreviewAsync(version.Id, identity.RouteId, AdaptationGoal.Balanced);

        Assert.Equal("opencode/space-bunny-free", preview.AdapterModelId);

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, identity.RouteId, AdaptationGoal.Balanced));

        Assert.Equal("opencode/space-bunny-free", result.AdapterModelId);

        var createRequest = Assert.Single(_lifecycle.CreateRequests);
        var turn = Assert.Single(_lifecycle.TurnRequests);

        Assert.Equal("opencode/space-bunny-free", createRequest.Model);
        Assert.Equal("opencode/space-bunny-free", turn.Request.Model);
        Assert.NotEqual("acct-oc", createRequest.Model);
        Assert.NotEqual("acct-oc", turn.Request.Model);
    }

    [Fact]
    public async Task RouteCarryingAModelTheAccountDoesNotHave_IsRefused()
    {
        await _database.InitializeAsync();
        await SeedAsync("acct-oc", BackendType.OpenCode, providerNativeId: null);
        await _modelCatalog.SaveModelsAsync(
            new ProviderModelCatalogQuery("prov-1", BackendType.OpenCode),
            new[] { new ProviderModelDescriptor("opencode/space-bunny-free") });

        var (_, version) = await SeedWorkflowAsync("version-1");

        // A crafted route id may select among the account's own models, never invent one.
        var crafted = new AdaptationRouteIdentity("acct-oc", "prov-1", BackendType.OpenCode, "some/other-model");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.PreparePreSendPreviewAsync(version.Id, crafted.RouteId, AdaptationGoal.Balanced));

        Assert.Contains("not a model of account", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_lifecycle.CreateRequests);
    }

    [Fact]
    public async Task RouteCarryingAFilesystemPath_IsRefused()
    {
        await _database.InitializeAsync();
        await SeedAsync("acct-oc", BackendType.OpenCode, providerNativeId: null);
        await _modelCatalog.SaveModelsAsync(
            new ProviderModelCatalogQuery("prov-1", BackendType.OpenCode),
            new[] { new ProviderModelDescriptor("opencode/space-bunny-free") });

        var (_, version) = await SeedWorkflowAsync("version-1");

        Assert.Throws<ArgumentException>(() => new AdaptationRouteIdentity(
            "acct-oc", "prov-1", BackendType.OpenCode, @"C:\Users\tester\.codex\home"));

        Assert.Empty(_lifecycle.CreateRequests);
    }

    private async Task SeedAsync(string accountId, BackendType backend, string? providerNativeId)
    {
        await _providerRepository.UpsertAsync(new ProviderProfile(
            "prov-1",
            $"{backend} Local",
            backend,
            "http://127.0.0.1:11434/v1",
            executablePath: null,
            DataClassification.PrivateSource,
            isEnabled: true));

        await _accountRepository.SaveAsync(new Account(
            accountId,
            "prov-1",
            $"Account {accountId}",
            providerNativeId,
            AuthState.Valid,
            manualPriority: 0,
            isEnabled: true,
            HealthState.Healthy,
            cooldownUntil: null,
            disabledUntil: null,
            maxConcurrentExecutions: 1,
            reserveThreshold: 0.25,
            sessionBindings: null,
            secretReference: null));
    }

    private async Task<(WorkflowPackage Package, WorkflowVersion Version)> SeedWorkflowAsync(string versionId)
    {
        var blob = await _blobStore.SaveBlobAsync(new MemoryStream(CreateArchive(("README.md", "# Workflow"))));
        var now = DateTimeOffset.UtcNow;

        var package = new WorkflowPackage(
            $"pkg-{versionId}",
            "Release Workflow",
            "Adaptation test workflow",
            new[] { "test" },
            WorkflowSourceType.ZipArchive,
            blob.BlobId,
            blob.BlobId,
            now,
            now);

        var version = new WorkflowVersion(
            versionId,
            package.Id,
            1,
            blob.BlobId,
            blob.BlobId,
            WorkflowSourceType.ZipArchive,
            entrypointsJson: null,
            declaredRolesJson: null,
            bindingsJson: null,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            now,
            activatedAtUtc: null);

        await _packageRepository.UpsertAsync(package);
        await _versionRepository.UpsertAsync(version);

        return (package, version);
    }

    private static byte[] CreateArchive(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private sealed class RecordingScratchWorkspaceManager : IScratchWorkspaceManager
    {
        private readonly IScratchWorkspaceManager _inner;

        public RecordingScratchWorkspaceManager(IScratchWorkspaceManager inner)
        {
            _inner = inner;
        }

        public List<ScratchScope> CreatedScopes { get; } = new();

        public List<string> CreatedScopeIds { get; } = new();

        public List<string> ExtractedBlobIds { get; } = new();

        public int PostOperationHashCheckCount { get; private set; }

        public Task<ScratchWorkspace> CreateWorkspaceAsync(
            ScratchScope scope,
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            CreatedScopes.Add(scope);
            CreatedScopeIds.Add(scopeId);

            return _inner.CreateWorkspaceAsync(scope, scopeId, cancellationToken);
        }

        public async Task ExtractBlobToWorkspaceAsync(
            string blobId,
            ScratchWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            ExtractedBlobIds.Add(blobId);

            await _inner.ExtractBlobToWorkspaceAsync(blobId, workspace, cancellationToken).ConfigureAwait(false);
        }

        public Task PostOperationSourceHashCheckAsync(
            string blobId,
            CancellationToken cancellationToken = default)
        {
            PostOperationHashCheckCount++;

            return _inner.PostOperationSourceHashCheckAsync(blobId, cancellationToken);
        }
    }

    private sealed class RecordingOpenCodeLifecycle : IOpenCodeSessionLifecycleService
    {
        public List<OpenCodeCreateSessionRequest> CreateRequests { get; } = new();

        public List<(string SessionId, OpenCodePromptRequest Request)> TurnRequests { get; } = new();

        public Task<OpenCodeSessionResponse> CreateAndConfirmSessionAsync(
            OpenCodeCreateSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateRequests.Add(request);
            return Task.FromResult(new OpenCodeSessionResponse { Id = "ses_123" });
        }

        public Task<OpenCodeSessionResponse> ContinueSessionAsync(
            string sessionId,
            SessionBinding binding,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TurnResult> ExecuteTurnAsync(
            string sessionId,
            OpenCodePromptRequest request,
            CancellationToken cancellationToken = default)
        {
            TurnRequests.Add((sessionId, request));

            return Task.FromResult(new TurnResult
            {
                SessionId = sessionId,
                OutputText = """{"mappings":[],"rationale":"","warnings":[],"blockers":[]}""",
                Status = TurnResult.CompletedStatus
            });
        }

        public Task<bool> CancelTurnAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<OpenCodeSessionResponse> ResetSessionAsync(
            string oldSessionId,
            OpenCodeCreateSessionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IReadOnlyList<string> GetAncestry(string sessionId) => Array.Empty<string>();
    }

    private sealed class RecordingOpenCodeClient : IOpenCodeClient
    {
        public Uri BaseUrl { get; } = new("http://127.0.0.1:4096/");

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<OpenCodeDocResponse> GetDocAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OpenCodeProviderInfo>> ListProvidersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OpenCodeProviderInfo>>(Array.Empty<OpenCodeProviderInfo>());

        public Task<IReadOnlyList<OpenCodeModelInfo>> ListModelsAsync(
            string? providerId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OpenCodeModelInfo>>(Array.Empty<OpenCodeModelInfo>());

        public Task<OpenCodeConfiguredProvidersResponse> ListConfiguredProvidersAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OpenCodeSessionResponse> CreateSessionAsync(
            OpenCodeCreateSessionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OpenCodeSessionResponse?> GetSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default) => Task.FromResult<OpenCodeSessionResponse?>(null);

        public Task<IReadOnlyList<OpenCodeSessionResponse>> ListSessionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OpenCodeSessionResponse>>(Array.Empty<OpenCodeSessionResponse>());

        public Task<OpenCodeSessionResponse> ForkSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> AbortSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendPromptAsync(
            string sessionId,
            OpenCodePromptRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ReplyPermissionAsync(
            string permissionId,
            OpenCodePermissionReply reply,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeEventsAsync(
            string? sessionId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
