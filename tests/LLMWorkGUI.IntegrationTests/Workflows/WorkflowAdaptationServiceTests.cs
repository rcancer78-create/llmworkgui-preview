using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowAdaptationServiceTests : IDisposable
{
    private const string SecretValue = "sk-abcdefgh12345678";

    /// <summary>Backend-native model id seeded on every account; it differs from every account id.</summary>
    private const string NativeModelId = "opencode/space-bunny-free";

    private readonly TestDatabase _database = new();
    private readonly WorkflowBlobStore _blobStore;
    private readonly SqliteWorkflowPackageRepository _packageRepository;
    private readonly SqliteWorkflowVersionRepository _versionRepository;
    private readonly SqliteWorkflowBindingRepository _bindingRepository;
    private readonly SqliteProviderProfileRepository _providerRepository;
    private readonly SqliteAccountRepository _accountRepository;
    private readonly SqliteHealthStateRepository _healthStateRepository;
    private readonly SqliteQuotaSnapshotRepository _quotaSnapshotRepository;
    private readonly RecordingScratchWorkspaceManager _scratchManager;
    private readonly MockAdaptationModelInvoker _modelInvoker = new();
    private readonly WorkflowAdaptationService _service;

    public WorkflowAdaptationServiceTests()
    {
        _blobStore = new WorkflowBlobStore(_database.Root);
        _packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        _versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);
        _bindingRepository = new SqliteWorkflowBindingRepository(_database.Factory);
        _providerRepository = new SqliteProviderProfileRepository(_database.Factory);
        _accountRepository = new SqliteAccountRepository(_database.Factory);
        _healthStateRepository = new SqliteHealthStateRepository(_database.Factory);
        _quotaSnapshotRepository = new SqliteQuotaSnapshotRepository(_database.Factory);

        _scratchManager = new RecordingScratchWorkspaceManager(
            new ScratchWorkspaceManager(_blobStore, new SafeArchiveValidator()));

        _service = CreateService(_versionRepository);
    }

    private WorkflowAdaptationService CreateService(IWorkflowVersionRepository versionRepository)
    {
        return CreateService(
            versionRepository,
            new ChatCapabilityFixtureCatalog(new SanitizedCatalogProvider(
                _providerRepository,
                _accountRepository,
                _healthStateRepository)));
    }

    private WorkflowAdaptationService CreateService(
        IWorkflowVersionRepository versionRepository,
        ISanitizedCatalogProvider catalogProvider,
        IAdaptationModelInvoker? modelInvoker = null,
        TimeProvider? timeProvider = null)
    {
        return new WorkflowAdaptationService(
            versionRepository,
            _packageRepository,
            _scratchManager,
            new WorkflowSecretScanner(new SensitiveDataFilter()),
            new WorkflowAdaptationPromptBuilder(),
            catalogProvider,
            _quotaSnapshotRepository,
            _accountRepository,
            modelInvoker ?? _modelInvoker,
            new AdaptationResponseParser(),
            new AdaptationReferenceValidator(),
            new SemanticDiffEngine(),
            new WorkflowDiffService(),
            _blobStore,
            timeProvider ?? TimeProvider.System);
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public void AddWorkflowServices_RegistersAdaptationContractsAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_database.Root);
        services.AddWorkflowServices();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var catalogProvider = provider.GetRequiredService<ISanitizedCatalogProvider>();
        var secretScanner = provider.GetRequiredService<IWorkflowSecretScanner>();
        var promptBuilder = provider.GetRequiredService<IAdaptationPromptBuilder>();
        var modelInvoker = provider.GetRequiredService<IAdaptationModelInvoker>();
        var responseParser = provider.GetRequiredService<IAdaptationResponseParser>();
        var referenceValidator = provider.GetRequiredService<IAdaptationReferenceValidator>();
        var semanticDiffEngine = provider.GetRequiredService<ISemanticDiffEngine>();
        var diffService = provider.GetRequiredService<IWorkflowDiffService>();
        var adaptationService = provider.GetRequiredService<IWorkflowAdaptationService>();

        Assert.IsType<SanitizedCatalogProvider>(catalogProvider);
        Assert.IsType<WorkflowSecretScanner>(secretScanner);
        Assert.IsType<WorkflowAdaptationPromptBuilder>(promptBuilder);
        Assert.IsType<BackendAdaptationModelInvoker>(modelInvoker);
        Assert.IsType<AdaptationResponseParser>(responseParser);
        Assert.IsType<AdaptationReferenceValidator>(referenceValidator);
        Assert.IsType<SemanticDiffEngine>(semanticDiffEngine);
        Assert.IsType<WorkflowDiffService>(diffService);
        Assert.IsType<WorkflowAdaptationService>(adaptationService);

        Assert.Same(catalogProvider, provider.GetRequiredService<ISanitizedCatalogProvider>());
        Assert.Same(secretScanner, provider.GetRequiredService<IWorkflowSecretScanner>());
        Assert.Same(promptBuilder, provider.GetRequiredService<IAdaptationPromptBuilder>());
        Assert.Same(modelInvoker, provider.GetRequiredService<IAdaptationModelInvoker>());
        Assert.Same(responseParser, provider.GetRequiredService<IAdaptationResponseParser>());
        Assert.Same(referenceValidator, provider.GetRequiredService<IAdaptationReferenceValidator>());
        Assert.Same(semanticDiffEngine, provider.GetRequiredService<ISemanticDiffEngine>());
        Assert.Same(diffService, provider.GetRequiredService<IWorkflowDiffService>());
        Assert.Same(adaptationService, provider.GetRequiredService<IWorkflowAdaptationService>());
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_ReturnsReadOnlyPreviewOfSourceVersion()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow"),
            ("prompts/executor.md", "You are the executor.")));

        var preview = await _service.PreparePreSendPreviewAsync(
            version.Id,
            "acct-1",
            AdaptationGoal.Balanced);

        Assert.Equal("version-1", preview.SourceVersionId);
        Assert.Equal(1, preview.SourceVersionNumber);
        Assert.Equal(version.BlobId, preview.SourceBlobId);
        Assert.Equal("acct-1", preview.AdapterRouteId);
        Assert.Equal(NativeModelId, preview.AdapterModelId);
        Assert.Equal(AdaptationGoal.Balanced, preview.Goal);
        Assert.Equal(new[] { "README.md", "prompts/executor.md" }, preview.IncludedFiles);
        Assert.Empty(preview.ExcludedFiles);
        Assert.False(preview.ScanReport.HasFindings);
        Assert.Equal("Unknown", preview.QuotaState);
        Assert.Equal("Unknown", preview.QuotaFreshness);
        Assert.Equal(0.25, preview.ReserveThreshold);

        var model = Assert.Single(preview.SanitizedCatalog.Models);
        Assert.Equal("acct-1", model.ModelId);
        Assert.True(model.IsRoutable);

        Assert.Contains("Сохранить назначение и структуру workflow", preview.PromptPreview, StringComparison.Ordinal);
        Assert.Contains("# Workflow", preview.PromptPreview, StringComparison.Ordinal);
        Assert.Contains("You are the executor.", preview.PromptPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_DoesNotMutateWorkflowStateOrBlob()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        var blobPath = _blobStore.GetBlobPath(version.BlobId);
        var blobBytesBefore = await File.ReadAllBytesAsync(blobPath);

        await _service.PreparePreSendPreviewAsync(version.Id, "acct-1", AdaptationGoal.Quality);

        var storedVersion = await _versionRepository.GetByIdAsync(version.Id);

        Assert.NotNull(storedVersion);
        Assert.Equal(version.BlobId, storedVersion!.BlobId);
        Assert.Null(storedVersion.ActivatedAtUtc);
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
        Assert.Equal(1, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await _database.CountAsync("WorkflowBindings"));
        Assert.Equal(0, await _database.CountAsync("ProjectLocks"));
        Assert.True(await _blobStore.VerifyBlobAsync(version.BlobId));
        Assert.Equal(blobBytesBefore, await File.ReadAllBytesAsync(blobPath));
        Assert.Equal(1, _scratchManager.PostOperationHashCheckCount);
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_ExcludesSecretBearingFilesFromPayload()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow"),
            ("config/secrets.env", $"api_key={SecretValue}")));

        var preview = await _service.PreparePreSendPreviewAsync(
            version.Id,
            "acct-1",
            AdaptationGoal.Balanced);

        Assert.True(preview.ScanReport.HasFindings);
        Assert.Equal(new[] { "config/secrets.env" }, preview.ExcludedFiles);
        Assert.Equal(new[] { "README.md" }, preview.IncludedFiles);
        Assert.Equal(new[] { "config/secrets.env" }, preview.ScanReport.RecommendedExcludedFiles);
        Assert.Contains("config/secrets.env", preview.PromptPreview, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, preview.PromptPreview, StringComparison.Ordinal);
        Assert.Contains("# Workflow", preview.PromptPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_UnionsUserExcludedFilesWithScannerFindings()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow"),
            ("notes.txt", "internal notes")));

        var preview = await _service.PreparePreSendPreviewAsync(
            version.Id,
            "acct-1",
            AdaptationGoal.Speed,
            new[] { "notes.txt" });

        Assert.Equal(new[] { "notes.txt" }, preview.ExcludedFiles);
        Assert.Equal(new[] { "README.md" }, preview.IncludedFiles);
        Assert.Contains("notes.txt", preview.PromptPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("internal notes", preview.PromptPreview, StringComparison.Ordinal);
        Assert.Contains("# Workflow", preview.PromptPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_UsesAdaptationScratchScopeAndCleansUp()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        await _service.PreparePreSendPreviewAsync(version.Id, "acct-1", AdaptationGoal.Balanced);

        Assert.Equal(new[] { ScratchScope.Adaptation }, _scratchManager.CreatedScopes);
        Assert.Equal(new[] { "version-1" }, _scratchManager.CreatedScopeIds);

        var scratchPath = Path.Combine(_database.Root, "scratch", "adaptation", "version-1");

        Assert.False(Directory.Exists(scratchPath));
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_ReportsQuotaSnapshotStateAndFreshness()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();

        var capturedAt = DateTimeOffset.UtcNow;

        await _quotaSnapshotRepository.SaveAsync(new QuotaSnapshot(
            "snap-1",
            "acct-1",
            QuotaProvenance.ExactProviderReported,
            capturedAt,
            buckets: new[]
            {
                new QuotaBucket(
                    "requests",
                    QuotaLimitUnit.Requests,
                    QuotaLimitWindow.PerDay,
                    limitValue: 1000,
                    usedValue: 100,
                    remainingValue: 900,
                    resetAt: capturedAt.AddDays(1))
            },
            providerProfileId: "prov-1",
            modelId: "acct-1",
            expiresAt: capturedAt.AddMinutes(5)));

        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        var preview = await _service.PreparePreSendPreviewAsync(
            version.Id,
            "acct-1",
            AdaptationGoal.CostSaving);

        Assert.Equal("ExactProviderReported", preview.QuotaState);
        Assert.Equal("Fresh", preview.QuotaFreshness);
        Assert.Equal(0.25, preview.ReserveThreshold);
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_ReportsStaleQuotaSnapshot()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();

        await _quotaSnapshotRepository.SaveAsync(new QuotaSnapshot(
            "snap-stale",
            "acct-1",
            QuotaProvenance.Estimated,
            DateTimeOffset.UtcNow.AddHours(-1),
            modelId: "acct-1"));

        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        var preview = await _service.PreparePreSendPreviewAsync(
            version.Id,
            "acct-1",
            AdaptationGoal.CostSaving);

        Assert.Equal("Estimated", preview.QuotaState);
        Assert.Equal("Stale", preview.QuotaFreshness);
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_ResolvesModelIdAndQuotaFromRouteId()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        await _database.InsertModelAsync(
            "other-model",
            providerProfileId: "prov-1",
            displayName: "Other Model");

        var capturedAt = DateTimeOffset.UtcNow;

        await _quotaSnapshotRepository.SaveAsync(new QuotaSnapshot(
            "snap-route",
            "acct-1",
            QuotaProvenance.PluginReported,
            capturedAt,
            providerProfileId: "prov-1",
            modelId: "other-model",
            expiresAt: capturedAt.AddMinutes(5)));

        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        var preview = await _service.PreparePreSendPreviewAsync(
            version.Id,
            "acct-1:other-model",
            AdaptationGoal.Balanced);

        Assert.Equal("acct-1:other-model", preview.AdapterRouteId);
        Assert.Equal(NativeModelId, preview.AdapterModelId);
        Assert.Equal("PluginReported", preview.QuotaState);
        Assert.Equal("Fresh", preview.QuotaFreshness);
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_ThrowsForMissingVersion()
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.PreparePreSendPreviewAsync("missing-version", "acct-1", AdaptationGoal.Balanced));
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_ThrowsForMissingPackage()
    {
        await _database.InitializeAsync();

        var archiveBytes = CreateArchive(("README.md", "# Workflow"));
        var blobId = WorkflowBlobStore.ComputeBlobId(archiveBytes);

        var orphanVersion = new WorkflowVersion(
            "version-orphan",
            "pkg-missing",
            1,
            blobId,
            blobId,
            WorkflowSourceType.ZipArchive,
            entrypointsJson: null,
            declaredRolesJson: null,
            bindingsJson: null,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            DateTimeOffset.UtcNow,
            activatedAtUtc: null);

        var service = CreateService(new StubWorkflowVersionRepository(orphanVersion));

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => service.PreparePreSendPreviewAsync("version-orphan", "acct-1", AdaptationGoal.Balanced));
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_FailsClosedWhenSourceBlobIsTampered()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _scratchManager.AfterExtractAsync = async blobId =>
        {
            await File.WriteAllTextAsync(_blobStore.GetBlobPath(blobId), "tampered");
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _service.PreparePreSendPreviewAsync(version.Id, "acct-1", AdaptationGoal.Balanced));

        var scratchPath = Path.Combine(_database.Root, "scratch", "adaptation", "version-1");

        Assert.False(Directory.Exists(scratchPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PreparePreSendPreviewAsync_RejectsBlankVersionId(string versionId)
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.PreparePreSendPreviewAsync(versionId, "acct-1", AdaptationGoal.Balanced));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PreparePreSendPreviewAsync_RejectsBlankRouteId(string routeId)
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.PreparePreSendPreviewAsync("version-1", routeId, AdaptationGoal.Balanced));
    }

    [Fact]
    public async Task PreparePreSendPreviewAsync_RejectsUndefinedGoal()
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.PreparePreSendPreviewAsync("version-1", "acct-1", (AdaptationGoal)42));
    }

    [Fact]
    public async Task StartAdaptationAsync_AppliesModelFileModificationsIntoScratchCandidate()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow"),
            ("prompts/executor.md", "old executor prompt")));

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping() },
            fileModifications: new Dictionary<string, string>
            {
                ["prompts/executor.md"] = "new executor prompt"
            }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Equal(version.Id, result.SourceVersionId);
        Assert.Equal(1, result.SourceVersionNumber);
        Assert.Equal("acct-1", result.AdapterRouteId);
        Assert.Equal(NativeModelId, result.AdapterModelId);
        Assert.Null(result.ParseError);

        // The rewrite of a role prompt is a semantic change found by comparing the real source and candidate
        // packages. The model declared isSemanticChange = false and an unchanged role set, which used to
        // clear it; the guard now blocks on the evidence regardless of the model's own claim.
        Assert.True(result.HasBlockers);
        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, result.Blockers);
        Assert.Contains(
            result.SemanticDiff.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("prompts/executor.md", StringComparison.Ordinal));
        Assert.True(result.SemanticDiff.HasSemanticChanges);
        Assert.Equal(
            SemanticChangeKind.SemanticDocument,
            Assert.Single(result.SemanticDiff.DetectedChanges).Kind);
        Assert.Equal(
            "prompts/executor.md",
            Assert.Single(result.SemanticDiff.DetectedChanges).Subject);

        var mapping = Assert.Single(result.Mappings);
        Assert.Equal(WorkflowRole.Executor, mapping.Role);

        Assert.Equal("new executor prompt", result.FileModifications["prompts/executor.md"]);
        Assert.Equal(1, result.PackageDiff.TotalFilesModified);
        Assert.Equal(1, result.PackageDiff.TotalFilesUnchanged);
        Assert.Equal(1, result.PackageDiff.TotalLinesAdded);
        Assert.Equal(1, result.PackageDiff.TotalLinesDeleted);
        Assert.Contains("prompts/executor.md", result.CandidateFiles);
        Assert.Equal("new executor prompt", ReadCandidateFile(result, "prompts/executor.md"));
        Assert.Equal("# Workflow", ReadCandidateFile(result, "README.md"));

        Assert.All(_scratchManager.CreatedScopes, scope => Assert.Equal(ScratchScope.Adaptation, scope));
        Assert.StartsWith(
            Path.Combine(_database.Root, "scratch", "adaptation"),
            result.CandidateWorkspacePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await _database.CountAsync("WorkflowVersions", "Id = 'candidate'"));
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
        Assert.Equal(0, await _database.CountAsync("WorkflowBindings"));
        Assert.Equal(0, await _database.CountAsync("ProjectLocks"));

        // The invoker is handed the identity the service authorized from the catalog, not the caller's
        // route string: "acct-1" is a catalog key, and the real backend can resolve nothing from it.
        var authorizedRoute = new AdaptationRouteIdentity("acct-1", "prov-1", BackendType.OpenCode, NativeModelId);

        var request = Assert.Single(_modelInvoker.Requests);
        Assert.Equal(authorizedRoute.RouteId, request.RouteId);
        Assert.NotEqual("acct-1", request.RouteId);
        Assert.Equal(NativeModelId, request.ModelId);
        Assert.NotEqual("acct-1", request.ModelId);
        Assert.Contains("Сохранить назначение и структуру workflow", request.SystemPrompt, StringComparison.Ordinal);
        var message = Assert.Single(request.Messages);
        Assert.Equal("user", message.Role);
        Assert.Contains("old executor prompt", message.Content, StringComparison.Ordinal);
        Assert.Contains("\"fileModifications\"", message.Content, StringComparison.Ordinal);
        Assert.Contains("\"mappings\"", message.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAdaptationAsync_KeepsSourceBlobAndActiveBindingUntouched()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        var binding = CreateBinding("project-1", package.Id, version.Id);
        await _bindingRepository.UpsertAsync(binding);

        var blobPath = _blobStore.GetBlobPath(version.BlobId);
        var blobBytesBefore = await File.ReadAllBytesAsync(blobPath);

        _modelInvoker.EnqueueResponse(CreateResponse(
            fileModifications: new Dictionary<string, string> { ["README.md"] = "# Adapted" }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        // Rewriting the package documentation is a semantic change found in the real packages, so the run is
        // blocked by default even though the model reported the same role set and no semantic change.
        Assert.True(result.HasBlockers);
        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, result.Blockers);
        Assert.Equal(blobBytesBefore, await File.ReadAllBytesAsync(blobPath));
        Assert.True(await _blobStore.VerifyBlobAsync(version.BlobId));

        var storedVersion = await _versionRepository.GetByIdAsync(version.Id);

        Assert.NotNull(storedVersion);
        Assert.Equal(version.BlobId, storedVersion!.BlobId);
        Assert.Null(storedVersion.ActivatedAtUtc);

        var storedBinding = await _bindingRepository.GetByIdAsync(binding.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(version.Id, storedBinding!.ActiveVersionId);
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
    }

    [Fact]
    public async Task StartAdaptationAsync_KeepsScannerHitsOutOfThePromptEvenWhenTheRequestOmitsThem()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow"),
            ("config/secrets.env", $"api_key={SecretValue}")));

        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));

        // Simulates the broken UI gesture: the exclusion list the dialog sends omits the scanner hit.
        await _service.StartAdaptationAsync(new AdaptationExecutionRequest(
            version.Id,
            "acct-1",
            AdaptationGoal.Balanced,
            allowExpandedSemanticScope: false,
            userExcludedFiles: Array.Empty<string>()));

        var request = Assert.Single(_modelInvoker.Requests);
        var message = Assert.Single(request.Messages);

        Assert.DoesNotContain(SecretValue, message.Content, StringComparison.Ordinal);
        Assert.Contains("config/secrets.env", message.Content, StringComparison.Ordinal);
        Assert.Contains("# Workflow", message.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAdaptationAsync_DetectsSecretInCandidateAndBlocks()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _modelInvoker.EnqueueResponse(CreateResponse(
            fileModifications: new Dictionary<string, string>
            {
                ["README.md"] = $"api_key={SecretValue}"
            }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.True(result.HasBlockers);
        Assert.Contains(AdaptationBlockerKind.DetectedSecret, result.Blockers);
        Assert.True(result.SecretScanReport.HasFindings);
        Assert.DoesNotContain(SecretValue, result.SecretScanReport.Findings[0].RedactedSnippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAdaptationAsync_FlagsMissingModelReference()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(targetModelId: "model-missing") }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.MissingModel, result.Blockers);
        Assert.False(result.ReferenceValidation.IsValid);
        Assert.Contains(
            result.ReferenceValidation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.MissingModel && issue.Role == "Executor");
    }

    [Fact]
    public async Task StartAdaptationAsync_FlagsNonRoutableModelReference()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        await SeedDisabledAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(targetModelId: "acct-disabled") }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.MissingCapability, result.Blockers);
        Assert.DoesNotContain(AdaptationBlockerKind.MissingModel, result.Blockers);
    }

    [Fact]
    public async Task StartAdaptationAsync_InventoryOnlyCatalogCannotAuthorizeRequiredChatCapability()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        var service = CreateService(
            _versionRepository,
            new SanitizedCatalogProvider(_providerRepository, _accountRepository, _healthStateRepository));

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(targetModelId: "acct-1") }));

        var result = await service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        // The target row is present and routable; only the role's required Chat flag is absent.
        Assert.Contains(AdaptationBlockerKind.MissingCapability, result.Blockers);
        Assert.DoesNotContain(AdaptationBlockerKind.MissingModel, result.Blockers);
        Assert.False(result.ReferenceValidation.IsValid);
        Assert.Contains(
            result.ReferenceValidation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.MissingCapability
                && issue.Role == "Executor"
                && issue.Message.Contains("acct-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAdaptationAsync_ChatRouteReferenceStaysEligible()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(targetModelId: "acct-1") }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.True(result.ReferenceValidation.IsValid);
        Assert.DoesNotContain(AdaptationBlockerKind.MissingCapability, result.Blockers);
        Assert.DoesNotContain(AdaptationBlockerKind.MissingModel, result.Blockers);
    }

    [Fact]
    public async Task StartAdaptationAsync_AccountWithoutNativeModelId_RefusesTheRoute()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync(providerNativeId: null);
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.StartAdaptationAsync(
                new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced)));

        Assert.Contains("no backend-native model id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("acct-1", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_modelInvoker.Requests);
    }

    [Fact]
    public async Task StartAdaptationAsync_FlagsDisallowedSemanticChangeByDefault()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            declaredRolesJson: """["Executor"]""");

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(role: WorkflowRole.Coordinator) }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, result.Blockers);
        Assert.True(result.SemanticDiff.HasBlockers);
    }

    /// <summary>
    /// The pre-send scope flag is prompt context: it tells the model a wider scope is intended and it
    /// reaches the prompt builder, but it is not consent to changes the operator has not seen, so the role
    /// drift stays a blocker.
    /// </summary>
    [Fact]
    public async Task StartAdaptationAsync_AllowExpandedSemanticScopeKeepsTheSemanticBlocker()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            declaredRolesJson: """["Executor"]""");

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(role: WorkflowRole.Coordinator) }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(
                version.Id,
                "acct-1",
                AdaptationGoal.Balanced,
                allowExpandedSemanticScope: true));

        Assert.True(result.AllowExpandedSemanticScope);
        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, result.Blockers);
        Assert.NotEmpty(result.SemanticDiff.Issues);
        Assert.True(result.SemanticDiff.HasSemanticChanges);
    }

    [Fact]
    public async Task StartAdaptationAsync_InvalidModelJsonFlagsInvalidSchemaAndAppliesNoFiles()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("prompts/executor.md", "old executor prompt")));

        _modelInvoker.EnqueueResponse("this is not json");

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.InvalidSchema, result.Blockers);
        Assert.NotNull(result.ParseError);
        Assert.Empty(result.FileModifications);
        Assert.Empty(result.Mappings);
        Assert.Equal(1, result.PackageDiff.TotalFilesUnchanged);
        Assert.Equal("old executor prompt", ReadCandidateFile(result, "prompts/executor.md"));
    }

    [Fact]
    public async Task StartAdaptationAsync_RejectsPathTraversalFileModifications()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _modelInvoker.EnqueueResponse(CreateResponse(
            fileModifications: new Dictionary<string, string>
            {
                ["../escape.txt"] = "evil",
                ["C:/windows/escape.txt"] = "evil",
                ["prompts/../../escape.txt"] = "evil",
                ["README.md"] = "must not be partially applied"
            }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.Other, result.Blockers);
        Assert.Single(result.BlockingIssues.Where(issue => issue.Kind == AdaptationBlockerKind.Other));
        Assert.Equal("# Workflow", ReadCandidateFile(result, "README.md"));

        var adaptationRoot = Path.Combine(_database.Root, "scratch", "adaptation");

        Assert.False(File.Exists(Path.Combine(adaptationRoot, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_database.Root, "scratch", "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_database.Root, "escape.txt")));
        Assert.DoesNotContain(
            result.CandidateFiles,
            path => path.Contains("escape", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartAdaptationAsync_PropagatesModelFailureAndCleansUpScratch()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _modelInvoker.FailureFactory = _ => new InvalidOperationException("model offline");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartAdaptationAsync(
                new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced)));

        Assert.Empty(GetAdaptationScratchDirectories());
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
    }

    [Fact]
    public async Task StartAdaptationAsync_FailsClosedWhenSourceBlobIsTampered()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _scratchManager.AfterExtractAsync = async blobId =>
        {
            await File.WriteAllTextAsync(_blobStore.GetBlobPath(blobId), "tampered");
        };

        _modelInvoker.EnqueueResponse(CreateResponse());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _service.StartAdaptationAsync(
                new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced)));

        Assert.Empty(GetAdaptationScratchDirectories());
    }

    [Fact]
    public async Task StartAdaptationAsync_ThrowsForMissingVersion()
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.StartAdaptationAsync(
                new AdaptationExecutionRequest("missing-version", "acct-1", AdaptationGoal.Balanced)));
    }

    [Fact]
    public async Task StartAdaptationAsync_RejectsUndefinedGoal()
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.StartAdaptationAsync(
                new AdaptationExecutionRequest("version-1", "acct-1", (AdaptationGoal)42)));
    }

    [Fact]
    public async Task SubmitFollowUpTurnAsync_ReextractsSourceAndDropsStaleModifications()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow"),
            ("prompts/executor.md", "old executor prompt")));

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping() },
            fileModifications: new Dictionary<string, string>
            {
                ["prompts/executor.md"] = "first turn prompt"
            }));

        var first = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Equal("first turn prompt", ReadCandidateFile(first, "prompts/executor.md"));

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(targetModelId: "acct-1") },
            fileModifications: new Dictionary<string, string>
            {
                ["README.md"] = "# Follow-up workflow"
            },
            rationale: "Follow-up rationale."));

        var second = await _service.SubmitFollowUpTurnAsync(
            new AdaptationFollowUpRequest(first.SessionId, "Please also update the README."));

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal("Follow-up rationale.", second.Rationale);
        Assert.Equal("old executor prompt", ReadCandidateFile(second, "prompts/executor.md"));
        Assert.Equal("# Follow-up workflow", ReadCandidateFile(second, "README.md"));
        Assert.Equal(new[] { "README.md" }, second.FileModifications.Keys);

        var snapshot = _service.GetSessionSnapshot(first.SessionId);

        Assert.Equal(2, snapshot.TurnCount);
        Assert.Equal(4, snapshot.TurnHistory.Count);
        Assert.Equal("Please also update the README.", snapshot.TurnHistory[2].Content);
        Assert.Equal("assistant", snapshot.TurnHistory[3].Role);

        var secondRequest = _modelInvoker.Requests[1];

        Assert.Equal(3, secondRequest.Messages.Count);
        Assert.Equal("user", secondRequest.Messages[0].Role);
        Assert.Equal("assistant", secondRequest.Messages[1].Role);
        Assert.Equal("Please also update the README.", secondRequest.Messages[2].Content);
    }

    [Fact]
    public async Task SubmitFollowUpTurnAsync_KeepsTheScopeFlagInTheSessionWithoutClearingTheBlocker()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            declaredRolesJson: """["Executor"]""");

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(role: WorkflowRole.Coordinator) }));

        var first = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, first.Blockers);

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(role: WorkflowRole.Coordinator) }));

        var second = await _service.SubmitFollowUpTurnAsync(
            new AdaptationFollowUpRequest(
                first.SessionId,
                "Keep the coordinator role.",
                allowExpandedSemanticScope: true));

        Assert.True(second.AllowExpandedSemanticScope);
        Assert.True(_service.GetSessionSnapshot(first.SessionId).AllowExpandedSemanticScope);
        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, second.Blockers);
    }

    [Fact]
    public async Task SubmitFollowUpTurnAsync_RevalidatesAgainstCurrentCatalog()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));

        var first = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.False(first.HasBlockers);

        await _accountRepository.SaveAsync(new Account(
            "acct-1",
            "prov-1",
            "Primary Account",
            providerNativeId: NativeModelId,
            AuthState.Valid,
            manualPriority: 0,
            isEnabled: false,
            HealthState.Healthy,
            cooldownUntil: null,
            disabledUntil: null,
            maxConcurrentExecutions: 1,
            reserveThreshold: 0.25,
            sessionBindings: null,
            secretReference: "urn:llmworkgui:secret:acct-1-token"));

        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));

        var second = await _service.SubmitFollowUpTurnAsync(
            new AdaptationFollowUpRequest(first.SessionId, "Keep the executor binding."));

        Assert.Contains(AdaptationBlockerKind.MissingCapability, second.Blockers);
        Assert.DoesNotContain(AdaptationBlockerKind.MissingModel, second.Blockers);
    }

    [Fact]
    public async Task SubmitFollowUpTurnAsync_ThrowsForUnknownSession()
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.SubmitFollowUpTurnAsync(
                new AdaptationFollowUpRequest("missing-session", "hello")));
    }

    [Fact]
    public async Task SaveCandidateVersionAsync_CreatesNonActivatedSyntheticDraft()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("prompts/executor.md", "old executor prompt")),
            entrypointsJson: """["run.ps1"]""",
            declaredRolesJson: """["Executor"]""");

        var binding = CreateBinding("project-1", package.Id, version.Id);
        await _bindingRepository.UpsertAsync(binding);

        var sourceBlobBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(version.BlobId));

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping() },
            fileModifications: new Dictionary<string, string>
            {
                ["prompts/executor.md"] = "new executor prompt"
            }));

        // The pre-send scope flag is prompt context only: the role-prompt rewrite stays a blocker that the
        // operator has to acknowledge per issue after seeing the diff, and no approval is persisted.
        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(
                version.Id,
                "acct-1",
                AdaptationGoal.Balanced,
                allowExpandedSemanticScope: true));

        var saved = await _service.SaveCandidateVersionAsync(result.SessionId);

        Assert.Equal(2, saved.VersionNumber);
        Assert.Equal(WorkflowSourceType.SyntheticDraft, saved.SourceType);
        Assert.Null(saved.ActivatedAtUtc);
        Assert.True(saved.HasBlockers);
        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, saved.Blockers);
        Assert.Equal(package.Id, saved.WorkflowPackageId);

        var stored = await _versionRepository.GetByIdAsync(saved.VersionId);

        Assert.NotNull(stored);
        Assert.Equal(package.Id, stored!.WorkflowPackageId);
        Assert.Equal(saved.BlobId, stored.BlobId);
        Assert.Equal(saved.BlobId, stored.OriginalHash);
        Assert.Equal(WorkflowSourceType.SyntheticDraft, stored.SourceType);
        Assert.Null(stored.ActivatedAtUtc);
        Assert.Equal("""["run.ps1"]""", stored.EntrypointsJson);
        Assert.Equal("""["Executor"]""", stored.DeclaredRolesJson);
        Assert.NotNull(stored.BindingsJson);
        Assert.NotNull(stored.CompatibilityReportJson);
        Assert.NotNull(stored.CreationMetadataJson);
        Assert.Contains(version.Id, stored.CreationMetadataJson!, StringComparison.Ordinal);
        Assert.Contains("Adaptation plan.", stored.CompatibilityReportJson!, StringComparison.Ordinal);

        // No recorded consent exists at all: the report carries the scope flag as prompt context and
        // nothing that any later consent path could read as an approval.
        Assert.DoesNotContain("\"approved\"", stored.CompatibilityReportJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("approvedChanges", stored.CompatibilityReportJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceBlobId", stored.CompatibilityReportJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("candidateBlobId", stored.CompatibilityReportJson!, StringComparison.Ordinal);
        Assert.Contains("expandedSemanticScopeRequested", stored.CompatibilityReportJson!, StringComparison.Ordinal);
        var reported = Assert.Single(result.SemanticDiff.DetectedChanges);
        Assert.Equal(SemanticChangeKind.SemanticDocument, reported.Kind);
        Assert.Equal("prompts/executor.md", reported.Subject);

        Assert.Equal(2, await _database.CountAsync("WorkflowVersions"));

        var storedBinding = await _bindingRepository.GetByIdAsync(binding.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(version.Id, storedBinding!.ActiveVersionId);

        Assert.True(await _blobStore.VerifyBlobAsync(version.BlobId));
        Assert.True(await _blobStore.VerifyBlobAsync(saved.BlobId));
        Assert.Equal(sourceBlobBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(version.BlobId)));
        Assert.Equal("new executor prompt", await ReadZipEntryAsync(saved.BlobId, "prompts/executor.md"));

        await using var blobStream = await _blobStore.GetBlobStreamAsync(saved.BlobId);
        using var archive = new ZipArchive(blobStream, ZipArchiveMode.Read);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.Equal(
            new[] { "README.md", "prompts/executor.md" },
            entryNames);
        Assert.All(
            archive.Entries,
            entry => Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0), entry.LastWriteTime.DateTime));

        Assert.Throws<WorkflowValidationException>(() => _service.GetSessionSnapshot(result.SessionId));
        Assert.Empty(GetAdaptationScratchDirectories());
    }

    [Fact]
    public async Task SaveCandidateVersionAsync_ProducesDeterministicBlobAcrossSessions()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow"),
            ("prompts/executor.md", "old executor prompt")));

        var modifications = new Dictionary<string, string>
        {
            ["prompts/executor.md"] = "deterministic content"
        };

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping() },
            fileModifications: modifications));

        var firstSession = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));
        var first = await _service.SaveCandidateVersionAsync(firstSession.SessionId);

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping() },
            fileModifications: modifications));

        var secondSession = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));
        var second = await _service.SaveCandidateVersionAsync(secondSession.SessionId);

        Assert.Equal(first.BlobId, second.BlobId);
        Assert.Equal(2, first.VersionNumber);
        Assert.Equal(3, second.VersionNumber);
    }

    [Fact]
    public async Task SaveCandidateVersionAsync_IsAllowedWhileBlockersExist()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(targetModelId: "model-missing", blockerKind: "MissingModel") },
            fileModifications: new Dictionary<string, string>
            {
                ["README.md"] = $"api_key={SecretValue}"
            }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.MissingModel, result.Blockers);
        Assert.Contains(AdaptationBlockerKind.DetectedSecret, result.Blockers);

        var saved = await _service.SaveCandidateVersionAsync(result.SessionId);

        Assert.True(saved.HasBlockers);
        Assert.Contains(AdaptationBlockerKind.MissingModel, saved.Blockers);
        Assert.Contains(AdaptationBlockerKind.DetectedSecret, saved.Blockers);
        Assert.NotNull(await _versionRepository.GetByIdAsync(saved.VersionId));
    }

    [Fact]
    public async Task SaveCandidateVersionAsync_IncrementsFromHighestExistingVersionNumber()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        await _versionRepository.UpsertAsync(new WorkflowVersion(
            "version-5",
            package.Id,
            5,
            version.BlobId,
            version.BlobId,
            WorkflowSourceType.ZipArchive,
            entrypointsJson: null,
            declaredRolesJson: null,
            bindingsJson: null,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            DateTimeOffset.UtcNow,
            activatedAtUtc: null));

        _modelInvoker.EnqueueResponse(CreateResponse(fileModifications: new Dictionary<string, string>
        {
            ["README.md"] = "# Adapted"
        }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        var saved = await _service.SaveCandidateVersionAsync(result.SessionId);

        Assert.Equal(6, saved.VersionNumber);
    }

    [Fact]
    public async Task SaveCandidateVersionAsync_ThrowsForUnknownSession()
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.SaveCandidateVersionAsync("missing-session"));
    }

    [Fact]
    public async Task DiscardSessionAsync_CleansScratchAndRemovesSession()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        _modelInvoker.EnqueueResponse(CreateResponse(fileModifications: new Dictionary<string, string>
        {
            ["README.md"] = "# Adapted"
        }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.NotEmpty(GetAdaptationScratchDirectories());
        Assert.True(Directory.Exists(result.CandidateWorkspacePath));

        await _service.DiscardSessionAsync(result.SessionId);

        Assert.Empty(GetAdaptationScratchDirectories());
        Assert.Throws<WorkflowValidationException>(() => _service.GetSessionSnapshot(result.SessionId));
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
    }

    [Fact]
    public async Task StartAdaptationAsync_StageOnlyFileModification_BlocksDespiteSemanticChangeFalse()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build the package.")),
            declaredRolesJson: """["Executor"]""");

        // Same role set, isSemanticChange = false, and only the stage definition is rewritten.
        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping() },
            fileModifications: new Dictionary<string, string>
            {
                ["stages/build.md"] = "Stage 1: build. Stage 2: silently ship."
            }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, result.Blockers);
        var issue = Assert.Single(result.SemanticDiff.Issues);
        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Contains("stages/build.md", issue.Message, StringComparison.Ordinal);
        var change = Assert.Single(result.SemanticDiff.DetectedChanges);
        Assert.Equal(SemanticChangeKind.SemanticDocument, change.Kind);
        Assert.Equal("stages/build.md", change.Subject);

        // The user still sees the file/package diff and the rationale, so the blocker is explainable.
        Assert.Equal(1, result.PackageDiff.TotalFilesModified);
        Assert.Equal("Adaptation plan.", result.Rationale);
        Assert.Contains(
            result.PackageDiff.FileDiffs,
            diff => diff.RelativePath == "stages/build.md" && diff.LinesAdded > 0);
    }

    [Fact]
    public async Task StartAdaptationAsync_QualityGateOnlyFileModification_BlocksDespiteSemanticChangeFalse()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("quality/gates.md", "Gate: the unit test suite must pass.")),
            declaredRolesJson: """["Executor"]""");

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping() },
            fileModifications: new Dictionary<string, string>
            {
                ["quality/gates.md"] = "Gate: the unit test suite is advisory."
            }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, result.Blockers);
        Assert.Contains(
            result.SemanticDiff.Issues,
            issue => issue.Message.Contains("quality/gates.md", StringComparison.Ordinal));
        Assert.Equal(
            "quality/gates.md",
            Assert.Single(result.SemanticDiff.DetectedChanges).Subject);
        Assert.Equal(1, result.PackageDiff.TotalFilesModified);
    }

    [Fact]
    public async Task StartAdaptationAsync_EscalationOnlyFileModification_BlocksDespiteSemanticChangeFalse()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("escalation/paths.md", "Escalate to the reviewer after two failed attempts.")),
            declaredRolesJson: """["Executor"]""");

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping() },
            fileModifications: new Dictionary<string, string>
            {
                ["escalation/paths.md"] = "Never escalate to the reviewer."
            }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, result.Blockers);
        Assert.Contains(
            result.SemanticDiff.Issues,
            issue => issue.Message.Contains("escalation/paths.md", StringComparison.Ordinal));
        Assert.Equal(
            "escalation/paths.md",
            Assert.Single(result.SemanticDiff.DetectedChanges).Subject);
        Assert.Equal(1, result.PackageDiff.TotalFilesModified);
    }

    /// <summary>
    /// A manifest rewrite of the stage, quality-gate or escalation section is caught by the structured tier
    /// even though the model reports the same role set and no semantic change.
    /// </summary>
    [Fact]
    public async Task StartAdaptationAsync_ManifestStageRewrite_BlocksDespiteSemanticChangeFalse()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive((
                "workflow.json",
                """{"declaredRoles":["Executor"],"bindings":{"Executor":"acct-1"},"stages":["build"]}""")),
            declaredRolesJson: """["Executor"]""");

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping(targetModelId: "acct-1") },
            fileModifications: new Dictionary<string, string>
            {
                ["workflow.json"] =
                    """{"declaredRoles":["Executor"],"bindings":{"Executor":"acct-1"},"stages":[]}"""
            }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, result.Blockers);
        Assert.Equal(
            SemanticChangeKind.ManifestStructure,
            Assert.Single(result.SemanticDiff.DetectedChanges).Kind);
    }

    /// <summary>
    /// A saved candidate records no consent at all, with or without the pre-send scope flag, so nothing in
    /// its stored report can later stand in for the per-issue decision the operator still has to make.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveCandidateVersionAsync_RecordsNoApprovalForAnyScopeFlag(bool expandedSemanticScope)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build.")),
            declaredRolesJson: """["Executor"]""");

        _modelInvoker.EnqueueResponse(CreateResponse(
            mappings: new[] { CreateMapping() },
            fileModifications: new Dictionary<string, string>
            {
                ["stages/build.md"] = "Stage 1: build. Stage 2: ship."
            }));

        var result = await _service.StartAdaptationAsync(
            new AdaptationExecutionRequest(
                version.Id,
                "acct-1",
                AdaptationGoal.Balanced,
                allowExpandedSemanticScope: expandedSemanticScope));

        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, result.Blockers);

        var saved = await _service.SaveCandidateVersionAsync(result.SessionId);
        var stored = await _versionRepository.GetByIdAsync(saved.VersionId);

        Assert.NotNull(stored);
        Assert.NotNull(stored!.CompatibilityReportJson);
        Assert.DoesNotContain("\"approved\"", stored.CompatibilityReportJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("approvedChanges", stored.CompatibilityReportJson!, StringComparison.Ordinal);
        Assert.NotEmpty(result.SemanticDiff.DetectedChanges);
    }

    [Fact]
    public void GetSessionSnapshot_ThrowsForUnknownSession()
    {
        Assert.Throws<WorkflowValidationException>(() => _service.GetSessionSnapshot("missing-session"));
    }

    private static byte[] CreateArchive(params (string Path, string Content)[] entries)
    {
        return WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            foreach (var (path, content) in entries)
            {
                WorkflowTestArchiveFactory.AddEntry(archive, path, content);
            }
        });
    }

    private static string CreateResponse(
        IReadOnlyList<MappingPayload>? mappings = null,
        IReadOnlyDictionary<string, string>? fileModifications = null,
        string rationale = "Adaptation plan.",
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? blockers = null)
    {
        return JsonSerializer.Serialize(new ResponsePayload(
            mappings ?? Array.Empty<MappingPayload>(),
            rationale,
            warnings ?? Array.Empty<string>(),
            blockers ?? Array.Empty<string>(),
            fileModifications));
    }

    private static MappingPayload CreateMapping(
        WorkflowRole role = WorkflowRole.Executor,
        string targetModelId = "acct-1",
        string? originalRoute = null,
        string? targetRoute = null,
        bool isSemanticChange = false,
        string? blockerKind = null,
        string rationale = "Rebinding an existing role.")
    {
        return new MappingPayload(
            role.ToString(),
            originalRoute ?? "acct-1",
            targetRoute ?? "acct-1:" + targetModelId,
            targetModelId,
            rationale,
            isSemanticChange,
            blockerKind);
    }

    private static WorkflowBinding CreateBinding(
        string projectId,
        string packageId,
        string activeVersionId)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowBinding(
            Guid.NewGuid().ToString(),
            projectId,
            packageId,
            activeVersionId,
            routePolicyId: null,
            now,
            now);
    }

    private static string ReadCandidateFile(AdaptationCandidateResult result, string relativePath)
    {
        return File.ReadAllText(Path.Combine(
            result.CandidateWorkspacePath,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private async Task<string> ReadZipEntryAsync(string blobId, string entryName)
    {
        await using var blobStream = await _blobStore.GetBlobStreamAsync(blobId);
        using var archive = new ZipArchive(blobStream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"ZIP entry '{entryName}' was not found.");

        using var reader = new StreamReader(entry.Open());

        return await reader.ReadToEndAsync();
    }

    private IReadOnlyList<string> GetAdaptationScratchDirectories()
    {
        var root = Path.Combine(_database.Root, "scratch", "adaptation");

        return Directory.Exists(root)
            ? Directory.GetDirectories(root)
            : Array.Empty<string>();
    }

    private async Task SeedProviderAndAccountAsync(
        string accountId = "acct-1",
        double? reserveThreshold = 0.25,
        string? providerNativeId = NativeModelId)
    {
        await _providerRepository.UpsertAsync(new ProviderProfile(
            "prov-1",
            "OpenCode Local",
            BackendType.OpenCode,
            "http://127.0.0.1:11434/v1",
            executablePath: null,
            DataClassification.PrivateSource,
            isEnabled: true));

        await _database.InsertModelAsync(
            accountId,
            providerProfileId: "prov-1",
            displayName: "Primary Account");

        await _accountRepository.SaveAsync(new Account(
            accountId,
            "prov-1",
            "Primary Account",
            providerNativeId,
            AuthState.Valid,
            manualPriority: 0,
            isEnabled: true,
            HealthState.Healthy,
            cooldownUntil: null,
            disabledUntil: null,
            maxConcurrentExecutions: 1,
            reserveThreshold,
            sessionBindings: null,
            secretReference: $"urn:llmworkgui:secret:{accountId}-token"));
    }

    private async Task SeedDisabledAccountAsync(string accountId = "acct-disabled")
    {
        await _accountRepository.SaveAsync(new Account(
            accountId,
            "prov-1",
            "Disabled Account",
            providerNativeId: NativeModelId,
            AuthState.Valid,
            manualPriority: 0,
            isEnabled: false,
            HealthState.Healthy,
            cooldownUntil: null,
            disabledUntil: null,
            maxConcurrentExecutions: 1,
            reserveThreshold: 0.25,
            sessionBindings: null,
            secretReference: $"urn:llmworkgui:secret:{accountId}-token"));
    }

    private async Task<(WorkflowPackage Package, WorkflowVersion Version)> SeedWorkflowAsync(
        string versionId,
        byte[] archiveBytes,
        string packageName = "Release Workflow",
        string? declaredRolesJson = null,
        string? entrypointsJson = null)
    {
        var blob = await _blobStore.SaveBlobAsync(new MemoryStream(archiveBytes));
        var now = DateTimeOffset.UtcNow;
        var package = new WorkflowPackage(
            $"pkg-{versionId}",
            packageName,
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
            entrypointsJson,
            declaredRolesJson,
            bindingsJson: null,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            now,
            activatedAtUtc: null);

        await _packageRepository.UpsertAsync(package);
        await _versionRepository.UpsertAsync(version);

        return (package, version);
    }

    private sealed record MappingPayload(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("originalRoute")] string OriginalRoute,
        [property: JsonPropertyName("targetRoute")] string TargetRoute,
        [property: JsonPropertyName("targetModelId")] string TargetModelId,
        [property: JsonPropertyName("rationale")] string Rationale,
        [property: JsonPropertyName("isSemanticChange")] bool IsSemanticChange,
        [property: JsonPropertyName("blockerKind")] string? BlockerKind);

    private sealed record ResponsePayload(
        [property: JsonPropertyName("mappings")] IReadOnlyList<MappingPayload> Mappings,
        [property: JsonPropertyName("rationale")] string Rationale,
        [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
        [property: JsonPropertyName("blockers")] IReadOnlyList<string> Blockers,
        [property: JsonPropertyName("fileModifications")] IReadOnlyDictionary<string, string>? FileModifications);

    private sealed class StubWorkflowVersionRepository : IWorkflowVersionRepository
    {
        private readonly WorkflowVersion? _version;

        public StubWorkflowVersionRepository(WorkflowVersion? version)
        {
            _version = version;
        }

        public Task UpsertAsync(WorkflowVersion version, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<WorkflowVersion?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_version);
        }

        public Task<WorkflowVersion?> GetByPackageAndVersionAsync(
            string packageId,
            int versionNumber,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<WorkflowVersion?>(null);
        }

        public Task<IReadOnlyList<WorkflowVersion>> ListByPackageIdAsync(
            string packageId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WorkflowVersion>>(Array.Empty<WorkflowVersion>());
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
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

        public Func<string, Task>? AfterExtractAsync { get; set; }

        public int PostOperationHashCheckCount { get; private set; }

        public Task<ScratchWorkspace> CreateWorkspaceAsync(
            ScratchScope scope,
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            lock (CreatedScopes)
            {
                CreatedScopes.Add(scope);
                CreatedScopeIds.Add(scopeId);
            }

            return _inner.CreateWorkspaceAsync(scope, scopeId, cancellationToken);
        }

        public async Task ExtractBlobToWorkspaceAsync(
            string blobId,
            ScratchWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            await _inner.ExtractBlobToWorkspaceAsync(blobId, workspace, cancellationToken);

            if (AfterExtractAsync is not null)
            {
                await AfterExtractAsync(blobId);
            }
        }

        public Task PostOperationSourceHashCheckAsync(
            string blobId,
            CancellationToken cancellationToken = default)
        {
            PostOperationHashCheckCount++;

            return _inner.PostOperationSourceHashCheckAsync(blobId, cancellationToken);
        }
    }
}
