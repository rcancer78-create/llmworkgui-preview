using System.Text.Json;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// Activation and rollback engine evidence (РўР— В§6.14, ADR-0006 В§1.7). The tests assert the fail-closed
/// activation gate, the mandatory live-catalog re-validation and, above all, that only the binding row
/// ever moves: blobs and version records stay byte-identical.
/// </summary>
public sealed partial class WorkflowActivationServiceTests : IDisposable
{
    private const string SecretValue = "sk-abcdefgh12345678";

    /// <summary>Backend-native model id seeded on every account; it differs from every account id.</summary>
    private const string NativeModelId = "opencode/space-bunny-free";

    private static readonly DateTimeOffset Timestamp = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();
    private readonly WorkflowBlobStore _blobStore;
    private readonly SqliteWorkflowPackageRepository _packageRepository;
    private readonly SqliteWorkflowVersionRepository _versionRepository;
    private readonly SqliteWorkflowBindingRepository _bindingRepository;
    private readonly SqliteProviderProfileRepository _providerRepository;
    private readonly SqliteAccountRepository _accountRepository;
    private readonly SqliteHealthStateRepository _healthStateRepository;
    private readonly MockAdaptationModelInvoker _modelInvoker = new();
    private readonly MutableTimeProvider _time = new(Timestamp);
    private readonly RecordingScratchWorkspaceManager _scratchManager;
    private readonly WorkflowActivationService _service;

    public WorkflowActivationServiceTests()
    {
        _blobStore = new WorkflowBlobStore(_database.Root);
        _packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        _versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);
        _bindingRepository = new SqliteWorkflowBindingRepository(_database.Factory);
        _providerRepository = new SqliteProviderProfileRepository(_database.Factory);
        _accountRepository = new SqliteAccountRepository(_database.Factory);
        _healthStateRepository = new SqliteHealthStateRepository(_database.Factory);

        _scratchManager = new RecordingScratchWorkspaceManager(
            new ScratchWorkspaceManager(_blobStore, new SafeArchiveValidator()));

        _service = CreateService(_scratchManager);
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    private WorkflowActivationService CreateService(IScratchWorkspaceManager scratchManager)
    {
        return CreateService(scratchManager, CreateCatalogProvider());
    }

    private WorkflowActivationService CreateService(
        IScratchWorkspaceManager scratchManager,
        ISanitizedCatalogProvider catalogProvider)
    {
        return new WorkflowActivationService(
            _versionRepository,
            _bindingRepository,
            new WorkflowBindingService(
                _bindingRepository,
                _packageRepository,
                _versionRepository,
                _time),
            catalogProvider,
            new WorkflowSecretScanner(new SensitiveDataFilter()),
            scratchManager,
            new WorkflowManifestParser(),
            new SemanticDiffEngine(),
            _time);
    }

    private ISanitizedCatalogProvider CreateCatalogProvider() => new ChatCapabilityFixtureCatalog(new SanitizedCatalogProvider(
        _providerRepository,
        _accountRepository,
        _healthStateRepository));

    [Fact]
    public void AddWorkflowServices_RegistersActivationEngineAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_database.Root);
        services.AddWorkflowServices();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var activationService = provider.GetRequiredService<IWorkflowActivationService>();

        Assert.IsType<WorkflowActivationService>(activationService);
        Assert.Same(activationService, provider.GetRequiredService<IWorkflowActivationService>());
    }

    [Fact]
    public async Task ValidateForActivationAsync_ValidImportedVersion_IsValidAndCleansScratch()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow"),
            ("prompts/executor.md", "You are the executor.")));

        var validation = await _service.ValidateForActivationAsync(version.Id);

        Assert.True(validation.IsValid);
        Assert.Empty(validation.Issues);
        Assert.Empty(validation.Blockers);
        Assert.False(validation.HasBlockers);
        Assert.Equal(new[] { ScratchScope.Adaptation }, _scratchManager.CreatedScopes);
        Assert.Empty(GetAdaptationScratchDirectories());
    }

    [Fact]
    public async Task ValidateForActivationAsync_DoesNotTouchBlobsOrVersionRecords()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        var blobPath = _blobStore.GetBlobPath(version.BlobId);
        var blobBytesBefore = await File.ReadAllBytesAsync(blobPath);

        await _service.ValidateForActivationAsync(version.Id);

        var storedVersion = await _versionRepository.GetByIdAsync(version.Id);

        Assert.NotNull(storedVersion);
        Assert.Equal(version.BlobId, storedVersion!.BlobId);
        Assert.Null(storedVersion.ActivatedAtUtc);
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
        Assert.Equal(1, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await _database.CountAsync("WorkflowBindings"));
        Assert.True(await _blobStore.VerifyBlobAsync(version.BlobId));
        Assert.Equal(blobBytesBefore, await File.ReadAllBytesAsync(blobPath));
    }

    [Fact]
    public async Task ValidateForActivationAsync_DetectsSecretInVersion()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow"),
            ("config/secrets.env", $"api_key={SecretValue}")));

        var validation = await _service.ValidateForActivationAsync(version.Id);

        Assert.False(validation.IsValid);
        Assert.Contains(AdaptationBlockerKind.DetectedSecret, validation.Blockers);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DetectedSecret
                && issue.Message.Contains("config/secrets.env", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateForActivationAsync_FlagsMissingModelFromSyntheticBindings()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("model-missing"));

        var validation = await _service.ValidateForActivationAsync(version.Id);

        Assert.Contains(AdaptationBlockerKind.MissingModel, validation.Blockers);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.MissingModel
                && issue.Role == "Executor"
                && issue.Message.Contains("model-missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateForActivationAsync_FlagsNonRoutableModel()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        await SeedDisabledAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            bindingsJson: CreateBindingsJson("acct-disabled"));

        var validation = await _service.ValidateForActivationAsync(version.Id);

        Assert.Contains(AdaptationBlockerKind.MissingCapability, validation.Blockers);
        Assert.DoesNotContain(AdaptationBlockerKind.MissingModel, validation.Blockers);
    }

    [Fact]
    public async Task ValidateForActivationAsync_FlagsRoutableModelMissingRequiredChatCapability()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        // The row is present in the catalog and routable; only the required Chat flag is missing.
        var service = CreateService(_scratchManager, new ChatlessCatalogProvider(
            CreateCatalogProvider(),
            "acct-1"));

        var validation = await service.ValidateForActivationAsync(version.Id);

        Assert.False(validation.IsValid);
        Assert.Contains(AdaptationBlockerKind.MissingCapability, validation.Blockers);
        Assert.DoesNotContain(AdaptationBlockerKind.MissingModel, validation.Blockers);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.MissingCapability
                && issue.Role == "Executor"
                && issue.Message.Contains("acct-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActivateVersionAsync_BlocksRoutableModelMissingRequiredChatCapability()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));
        var (_, candidate) = await SeedWorkflowAsync(
            "version-2",
            CreateArchive(("README.md", "# Workflow")),
            packageName: package.Name,
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, version.Id);

        var service = CreateService(_scratchManager, new ChatlessCatalogProvider(
            CreateCatalogProvider(),
            "acct-1"));

        var result = await service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: true,
            routePolicyId: null,
            acknowledgedBlockerKinds: new[] { AdaptationBlockerKind.MissingCapability }));

        Assert.False(result.IsSuccess);
        Assert.True(result.IsBlocked);
        Assert.Contains(AdaptationBlockerKind.MissingCapability, result.Validation!.Blockers);
        Assert.DoesNotContain(AdaptationBlockerKind.MissingModel, result.Validation!.Blockers);

        var storedBinding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(version.Id, storedBinding!.ActiveVersionId);

        var storedCandidate = await _versionRepository.GetByIdAsync(candidate.Id);

        Assert.NotNull(storedCandidate);
        Assert.Null(storedCandidate!.ActivatedAtUtc);
    }

    [Fact]
    public async Task ValidateForActivationAsync_ChatRouteStaysEligible()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var validation = await _service.ValidateForActivationAsync(version.Id);

        Assert.True(validation.IsValid);
        Assert.DoesNotContain(AdaptationBlockerKind.MissingCapability, validation.Blockers);
        Assert.DoesNotContain(AdaptationBlockerKind.MissingModel, validation.Blockers);
    }

    [Fact]
    public async Task ChatlessCatalogProvider_OnlyRemovesChatFromTheTargetedRow()
    {
        var inner = new StubSanitizedCatalogProvider(
            new SanitizedCapabilityCatalog(
                Array.Empty<SanitizedProviderInfo>(),
                new[]
                {
                    CreateCatalogModel("acct-1", ModelCapabilityFlags.Chat | ModelCapabilityFlags.Vision),
                    CreateCatalogModel("acct-2", ModelCapabilityFlags.Chat)
                },
                Timestamp));

        var provider = new ChatlessCatalogProvider(inner, "acct-1");

        var catalog = await provider.GetSanitizedCatalogAsync();
        var targeted = catalog.Models.Single(model => model.ModelId == "acct-1");
        var untouched = catalog.Models.Single(model => model.ModelId == "acct-2");

        Assert.Equal(ModelCapabilityFlags.Vision, targeted.Capabilities);
        Assert.Equal(ModelCapabilityFlags.Chat, untouched.Capabilities);
    }

    private static SanitizedModelInfo CreateCatalogModel(string modelId, ModelCapabilityFlags capabilities)
    {
        return new SanitizedModelInfo(
            modelId,
            modelId,
            capabilities,
            Array.Empty<string>(),
            Array.Empty<string>(),
            ContextWindow: null,
            HealthState.Healthy,
            IsRoutable: true);
    }

    private sealed class StubSanitizedCatalogProvider : ISanitizedCatalogProvider
    {
        private readonly SanitizedCapabilityCatalog _catalog;

        public StubSanitizedCatalogProvider(SanitizedCapabilityCatalog catalog) => _catalog = catalog;

        public Task<SanitizedCapabilityCatalog> GetSanitizedCatalogAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_catalog);
        }
    }

    /// <summary>
    /// Removes the Chat flag from one catalog row so the "present, routable, but the role's required
    /// capability is absent" case can be produced: the production detector always sets Chat, so it
    /// cannot produce that row on its own.
    /// </summary>
    private sealed class ChatlessCatalogProvider : ISanitizedCatalogProvider
    {
        private readonly ISanitizedCatalogProvider _inner;
        private readonly string _modelId;

        public ChatlessCatalogProvider(ISanitizedCatalogProvider inner, string modelId)
        {
            _inner = inner;
            _modelId = modelId;
        }

        public async Task<SanitizedCapabilityCatalog> GetSanitizedCatalogAsync(
            CancellationToken cancellationToken = default)
        {
            var catalog = await _inner.GetSanitizedCatalogAsync(cancellationToken).ConfigureAwait(false);

            var models = catalog.Models
                .Select(model => string.Equals(model.ModelId, _modelId, StringComparison.Ordinal)
                    ? model with { Capabilities = model.Capabilities & ~ModelCapabilityFlags.Chat }
                    : model)
                .ToArray();

            return new SanitizedCapabilityCatalog(catalog.Providers, models, catalog.GeneratedAtUtc);
        }
    }

    [Fact]
    public async Task ValidateForActivationAsync_FlagsDisallowedSemanticChange()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            bindingsJson: CreateBindingsJson("acct-1", isSemanticChange: true));

        var validation = await _service.ValidateForActivationAsync(version.Id);

        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, validation.Blockers);
    }

    [Fact]
    public async Task ValidateForActivationAsync_FlagsRoleDriftAgainstDeclaredRoles()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            declaredRolesJson: """["Executor","Reviewer"]""",
            bindingsJson: CreateBindingsJson("acct-1", role: "Coordinator"));

        var validation = await _service.ValidateForActivationAsync(version.Id);

        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, validation.Blockers);
        Assert.Contains(
            validation.Issues,
            issue => issue.Role == "Coordinator"
                && issue.Message.Contains("added", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            validation.Issues,
            issue => issue.Role == "Executor"
                && issue.Message.Contains("no longer mapped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateForActivationAsync_ReadsRoleBindingsFromFormalManifest()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("workflow.json", """{"bindings":{"Executor":"model-missing"}}"""),
                ("README.md", "# Workflow")),
            declaredRolesJson: """["Executor"]""");

        var validation = await _service.ValidateForActivationAsync(version.Id);

        Assert.Contains(AdaptationBlockerKind.MissingModel, validation.Blockers);
        Assert.Contains(
            validation.Issues,
            issue => issue.Role == "Executor"
                && issue.Message.Contains("model-missing", StringComparison.Ordinal));
    }

    /// <summary>
    /// An empty mapping list against a non-empty declared role baseline removes every role. The previous
    /// activation path short-circuited on an empty mapping list, so a candidate could drop all roles and
    /// still be activated.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_EmptyMappingsAgainstNonEmptyDeclaredRoles_BlocksRoleRemoval()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: "[]");

        var validation = await _service.ValidateForActivationAsync(version.Id);

        var issue = Assert.Single(validation.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Equal("Executor", issue.Role);
        Assert.Contains("no longer mapped", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateForActivationAsync_InvalidMappingsAgainstNonEmptyDeclaredRoles_BlockEveryRole()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(("README.md", "# Workflow")),
            declaredRolesJson: """["Executor","Reviewer","Escalation"]""",
            bindingsJson: "{ not json");

        var validation = await _service.ValidateForActivationAsync(version.Id);

        Assert.Equal(4, validation.Issues.Count);
        Assert.Contains(validation.Issues, issue => issue.IsNotClearable);
        Assert.All(
            validation.Issues,
            issue => Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind));
        Assert.Equal(
            new[] { "Escalation", "Executor", "Reviewer" },
            validation.Issues.Where(issue => issue.Role is not null).Select(issue => issue.Role).OrderBy(role => role, StringComparer.Ordinal));
    }

    /// <summary>
    /// A candidate can keep or add a byte-identical <c>manifest.json</c>, put the rewrite into the
    /// <c>workflow.json</c> the activation parser reads first, and declare <c>isSemanticChange: false</c>.
    /// Both root manifests are compared on their own path, so the file that is actually read is the file
    /// that is checked.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_RewrittenWorkflowJsonBesideUnchangedManifestJson_Blocks()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();

        const string SourceManifest = """{"declaredRoles":["Executor"],"stages":["build"]}""";

        var (package, source) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("manifest.json", SourceManifest),
                ("workflow.json", SourceManifest),
                ("README.md", "# Workflow")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateArchive(
                ("manifest.json", SourceManifest),
                ("workflow.json", """{"declaredRoles":["Executor"],"stages":[]}"""),
                ("README.md", "# Workflow")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = source.Id }));

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("workflow.json", StringComparison.Ordinal)
                && !issue.IsNotClearable);
    }

    /// <summary>
    /// The whole <c>bindings</c> object used to be removed before the comparison, so a routine rebinding
    /// could carry a new escalation policy and still activate. Only the routing leaves are ignored now, so
    /// the nested policy is reported even though the model mapping itself is untouched, and the refusal is
    /// proved on the activation path itself: no decision recorded, pointer and blob unchanged.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_NestedEscalationPolicyUnderBindings_Blocks()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();

        var (package, source) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive((
                "workflow.json",
                """
                {"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-1","escalation":{"onFailure":true}}}}
                """)),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateArchive((
                "workflow.json",
                """
                {"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-1","escalation":{"onFailure":false}}}}
                """)),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = source.Id }));

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, source.Id);

        var candidateBlobPath = _blobStore.GetBlobPath(candidate.BlobId);
        var candidateBlobBefore = await File.ReadAllBytesAsync(candidateBlobPath);

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("workflow.json", StringComparison.Ordinal));

        var refused = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id));

        Assert.True(refused.IsBlocked);
        Assert.Null(refused.Binding);
        Assert.NotNull(refused.Validation);

        var binding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(binding);
        Assert.Equal(source.Id, binding!.ActiveVersionId);
        Assert.Equal(candidateBlobBefore, await File.ReadAllBytesAsync(candidateBlobPath));
    }

    /// <summary>
    /// The bounded file budget drops the tail of a package, so a rewritten late-sorting stage file used to
    /// disappear from the comparison and leave only a generic finding an operator decision could clear. The
    /// dropped path is now named and never clearable, on the real activation path.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_ChangedFilePastTheFileCountCutoff_NeverClears()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();

        var changedTail = SemanticPackageAnalyzer.MaxAnalyzedFiles + 4;

        var (package, source) = await SeedWorkflowAsync(
            "version-1",
            CreateStageArchive(rewriteTail: false),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateStageArchive(rewriteTail: true),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = source.Id }));

        var dropped = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"stages/step-{changedTail:D4}.md");

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        Assert.False(validation.IsValid);
        Assert.True(validation.HasUnclearableBlockers);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.IsNotClearable
                && issue.Message.Contains(dropped, StringComparison.Ordinal));

        var refused = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: validation.Issues));

        Assert.True(refused.IsBlocked);
        Assert.Null(await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id));
    }

    /// <summary>
    /// The same rewrite without a confirmed scope is saved with blockers and is refused again at activation,
    /// so the guard does not depend on the session state that produced the candidate.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_UnconfirmedExpandedScope_StillBlocksTheStageRewrite()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        await _database.SeedRouteChainAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build the package.")),
            declaredRolesJson: """["Executor"]""");

        var candidate = await SaveCandidateWithRewrittenStageAsync(version, expandedSemanticScope: false);

        var validation = await _service.ValidateForActivationAsync(candidate);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("stages/build.md", StringComparison.Ordinal));
    }

    /// <summary>
    /// End-to-end on the real service path: a stage rewrite stays a blocker at activation even though the
    /// pre-send scope flag was on, because no durable approval is recorded any more. It only moves the
    /// pointer after the operator acknowledges exactly the issue the fresh revalidation reports.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_PreSendScopeFlagNeverAuthorisesAStageRewrite()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        await _database.SeedRouteChainAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build the package.")),
            declaredRolesJson: """["Executor"]""");

        var candidate = await SaveCandidateWithRewrittenStageAsync(version, expandedSemanticScope: true);

        var validation = await _service.ValidateForActivationAsync(candidate);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("stages/build.md", StringComparison.Ordinal));

        // The recorded decision covers exactly the reported issue and is then honoured by the gate.
        var decision = validation.Issues;
        var activation = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            version.WorkflowPackageId,
            candidate,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: decision));

        Assert.True(activation.IsSuccess, activation.ErrorMessage);
        Assert.Equal(candidate, activation.Binding!.ActiveVersionId);
    }

    /// <summary>
    /// A decision set that does not match the freshly revalidated issues authorises nothing, so a saved
    /// candidate still needs its own per-issue acknowledgement after every reload.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_SavedCandidateNeedsAFreshIssueAcknowledgement()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        await _database.SeedRouteChainAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build the package.")),
            declaredRolesJson: """["Executor"]""");

        var candidate = await SaveCandidateWithRewrittenStageAsync(version, expandedSemanticScope: true);

        var refused = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            version.WorkflowPackageId,
            candidate));

        Assert.True(refused.IsBlocked);
        Assert.NotNull(refused.Validation);
        Assert.Contains("not acknowledged", refused.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A truncated or unreadable package makes the comparison inconclusive, and that gap is never
    /// acknowledgeable: the gate stays closed even for the exact reported issue list.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_UnreadableSourceContentCannotBeAcknowledged()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        await _database.SeedRouteChainAsync();

        // The source stage file is binary, so the bounded analysis can list it but never compare it.
        var (package, version) = await SeedWorkflowAsync(
            "version-1",
            WorkflowTestArchiveFactory.CreateArchive(archive =>
            {
                WorkflowTestArchiveFactory.AddEntry(archive, "README.md", "# Workflow");
                WorkflowTestArchiveFactory.AddZeroFilledEntry(archive, "stages/build.md", 64);
            }),
            declaredRolesJson: """["Executor"]""");

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build the package.")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = version.Id }));

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        var issues = string.Join(" | ", validation.Issues.Select(issue => issue.Message));

        Assert.False(validation.IsValid, issues);
        Assert.True(validation.HasUnclearableBlockers, issues);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.IsNotClearable
                && issue.Message.Contains("stages/build.md", StringComparison.Ordinal));

        var refused = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: validation.Issues));

        Assert.True(refused.IsBlocked);
        Assert.Contains("cannot be acknowledged", refused.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var stored = await _versionRepository.GetByIdAsync(candidate.Id);

        Assert.NotNull(stored);
        Assert.Null(stored!.ActivatedAtUtc);
        Assert.Null(await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id));
    }

    /// <summary>
    /// Nothing is recorded for the candidate, so a tampered candidate is simply revalidated as a fresh
    /// issue: the rewrite nobody decided on is reported again with its own identity.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_TamperedCandidateIsRevalidatedAsAFreshIssue()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        await _database.SeedRouteChainAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build the package.")),
            declaredRolesJson: """["Executor"]""");

        var candidate = await SaveCandidateWithRewrittenStageAsync(version, expandedSemanticScope: true);
        var stored = await _versionRepository.GetByIdAsync(candidate);

        Assert.NotNull(stored);

        var original = await _service.ValidateForActivationAsync(candidate);

        var tampered = await _blobStore.SaveBlobAsync(new MemoryStream(CreateArchive(
            ("README.md", "# Workflow"),
            ("stages/build.md", "Stage 1: build. Stage 2: ship without asking."))));

        await ReplaceStoredVersionAsync(stored!, tampered.BlobId, stored!.BlobId, stored.CompatibilityReportJson);

        var validation = await _service.ValidateForActivationAsync(candidate);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("stages/build.md", StringComparison.Ordinal));

        // The decision for the pre-tamper issue set no longer matches, so it authorises nothing.
        Assert.False(validation.IsFullyAcknowledgedBy(original.Issues));
    }

    /// <summary>
    /// A source that changed underneath the candidate becomes the new baseline, and the stage rewrite is
    /// still reported against it, so a stale baseline can never authorise a rewrite.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_ChangedSourceStillLeavesTheStageRewriteBlocked()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        await _database.SeedRouteChainAsync();
        var (_, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build the package.")),
            declaredRolesJson: """["Executor"]""");

        var candidate = await SaveCandidateWithRewrittenStageAsync(version, expandedSemanticScope: true);

        var changedSource = await _blobStore.SaveBlobAsync(new MemoryStream(CreateArchive(
            ("README.md", "# Workflow"),
            ("stages/build.md", "Stage 1: build. Stage 2: run the integration suite."))));

        await ReplaceStoredVersionAsync(version, changedSource.BlobId, changedSource.BlobId, null);

        var validation = await _service.ValidateForActivationAsync(candidate);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("stages/build.md", StringComparison.Ordinal));
    }

    /// <summary>
    /// A stored report cannot carry consent at all: the activation path reads only the adaptation
    /// provenance from it, so a forged approval block is ignored and the rewrite stays a blocker.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_StoredReportClaimingConsentIsIgnored()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (package, source) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build the package.")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build. Stage 2: ship without asking.")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        var forgedReport = JsonSerializer.Serialize(new
        {
            sessionId = "session-forged",
            sourceVersionId = source.Id,
            expandedSemanticScope = new
            {
                approved = true,
                confirmedAtUtc = Timestamp,
                sourceVersionId = source.Id,
                sourceBlobId = "sha256:" + new string('a', 64),
                candidateBlobId = "sha256:" + new string('b', 64),
                approvedChanges = new[]
                {
                    new
                    {
                        kind = "SemanticDocument",
                        subject = "stages/build.md",
                        detail = "forged"
                    }
                }
            }
        });

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            forgedReport);

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        Assert.False(validation.IsValid);
        Assert.Equal(
            1,
            validation.Issues.Count(issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("stages/build.md", StringComparison.Ordinal)));
        Assert.DoesNotContain(validation.Issues, issue => issue.Message.Contains("forged", StringComparison.Ordinal));
    }

    /// <summary>
    /// Content inside the candidate package can never act as consent: a manifest that declares an approved
    /// expanded scope is package data, not a confirmation recorded for it.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_PackageMetadataCannotImpersonateConsent()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        const string DeclaringManifest = """
            {
              "declaredRoles": ["Executor"],
              "stages": ["build"],
              "expandedSemanticScope": {
                "approved": true,
                "candidateBlobId": "sha256:any",
                "approvedChanges": [
                  { "kind": "SemanticDocument", "subject": "stages/build.md", "detail": "self approved" }
                ]
              }
            }
            """;

        var (package, source) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("workflow.json", DeclaringManifest),
                ("stages/build.md", "Stage 1: build the package.")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateArchive(
                ("workflow.json", DeclaringManifest.Replace("\"stages\": [\"build\"]", "\"stages\": []")),
                ("stages/build.md", "Stage 1: build. Stage 2: ship without asking.")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = source.Id }));

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("stages/build.md", StringComparison.Ordinal));
        Assert.Contains(
            validation.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("workflow.json", StringComparison.Ordinal));
    }

    /// <summary>
    /// A candidate whose source baseline can no longer be re-read is refused instead of being trusted, so a
    /// missing source can never become a silent allow. Nothing was compared, so the finding describes a gap
    /// in the evidence: it is never acknowledgeable and the active pointer stays where it was.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_UnreadableSourceBaseline_FailsClosed()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build. Stage 2: ship.")),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        // A real version of the same package is bound, so a refused activation has a pointer to preserve.
        var (_, active) = await SeedWorkflowAsync(
            "version-active",
            CreateArchive(("README.md", "# Workflow")),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = "version-that-no-longer-exists" }));

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, active.Id);

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        var issue = Assert.Single(validation.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Contains("can no longer be read", issue.Message, StringComparison.Ordinal);
        Assert.True(issue.IsNotClearable);
        Assert.True(validation.HasUnclearableBlockers);
        Assert.False(validation.IsFullyAcknowledgedBy(validation.Issues));

        var candidateBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(candidate.BlobId));
        var activeBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(active.BlobId));

        // Handing the gate exactly the issue it reported still authorises nothing.
        var refused = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: validation.Issues));

        Assert.True(refused.IsBlocked);
        Assert.NotNull(refused.Validation);
        Assert.True(refused.Validation!.HasUnclearableBlockers);
        Assert.Contains("cannot be acknowledged", refused.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var binding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(binding);
        Assert.Equal(active.Id, binding!.ActiveVersionId);
        Assert.Equal(candidateBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(candidate.BlobId)));
        Assert.Equal(activeBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(active.BlobId)));
    }

    /// <summary>
    /// A source blob that fails its integrity check cannot be re-read, so the candidate is refused with an
    /// issue instead of the validation throwing, and that issue stays unacknowledgeable.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_TamperedSourceBlob_FailsClosedInsteadOfThrowing()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        await _database.SeedRouteChainAsync();
        var (package, version) = await SeedWorkflowAsync(
            "version-1",
            CreateArchive(
                ("README.md", "# Workflow"),
                ("stages/build.md", "Stage 1: build the package.")),
            declaredRolesJson: """["Executor"]""");

        var candidate = await SaveCandidateWithRewrittenStageAsync(version, expandedSemanticScope: true);
        var stored = await _versionRepository.GetByIdAsync(candidate);

        Assert.NotNull(stored);

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, version.Id);

        var blobPath = _blobStore.GetBlobPath(version.BlobId);

        await File.WriteAllBytesAsync(blobPath, CreateArchive(("README.md", "# Tampered")));

        var validation = await _service.ValidateForActivationAsync(candidate);

        Assert.False(validation.IsValid);
        var issue = Assert.Single(validation.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Contains("can no longer be extracted", issue.Message, StringComparison.Ordinal);
        Assert.True(issue.IsNotClearable);
        Assert.True(validation.HasUnclearableBlockers);
        Assert.False(validation.IsFullyAcknowledgedBy(validation.Issues));

        var candidateBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(stored!.BlobId));

        var refused = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: validation.Issues));

        Assert.True(refused.IsBlocked);
        Assert.Contains("cannot be acknowledged", refused.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var binding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(binding);
        Assert.Equal(version.Id, binding!.ActiveVersionId);
        Assert.Equal(candidateBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(stored.BlobId)));
    }

    // ---- The acknowledgement is bound to the content it was decided on ------------------------------

    /// <summary>
    /// The gate matches the reported message, so a decision has to name the structure form it was decided
    /// on. Without that, a decision taken for <c>["review"]</c> authorises a later <c>["ship"]</c> and the
    /// pointer moves on a rewrite nobody ever saw.
    /// </summary>
    [Fact]
    public async Task ActivateVersionAsync_ConfirmedOneStageRewrite_DoesNotAuthoriseTheNext()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();

        var (package, source) = await SeedWorkflowAsync(
            "version-1",
            CreateManifestPackage(
                """{"declaredRoles":["Executor"],"stages":["build"]}""",
                includeManifestJson: false),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateManifestPackage(
                """{"declaredRoles":["Executor"],"stages":["review"]}""",
                includeManifestJson: false),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = source.Id }));

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, source.Id);

        var reported = await _service.ValidateForActivationAsync(candidate.Id);

        var issue = Assert.Single(reported.Issues);

        Assert.False(issue.IsNotClearable);
        Assert.Contains("source sha256:", issue.Message, StringComparison.Ordinal);
        Assert.Contains("candidate sha256:", issue.Message, StringComparison.Ordinal);

        // The very same version is then rewritten again, to a stage list the operator never decided on. The
        // stored row is re-read so the drift under test is the manifest content and nothing else.
        var stored = (await _versionRepository.GetByIdAsync(candidate.Id))!;
        var rewritten = await _blobStore.SaveBlobAsync(new MemoryStream(
            CreateManifestPackage(
                """{"declaredRoles":["Executor"],"stages":["ship"]}""",
                includeManifestJson: false)));

        await ReplaceStoredVersionAsync(
            candidate,
            rewritten.BlobId,
            stored.BlobId,
            stored.CompatibilityReportJson);

        var refused = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: reported.Issues));

        Assert.True(refused.IsBlocked);
        Assert.NotNull(refused.Validation);
        Assert.False(refused.Validation!.IsFullyAcknowledgedBy(reported.Issues));

        var binding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(binding);
        Assert.Equal(source.Id, binding!.ActiveVersionId);
    }

    /// <summary>
    /// The same holds for a root manifest that was added: the decision names the structure form of the file
    /// that was added, so rewriting that file afterwards invalidates it.
    /// </summary>
    [Fact]
    public async Task ActivateVersionAsync_ConfirmedAddedManifest_DoesNotAuthoriseARewrittenManifest()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();

        const string Manifest = """{"declaredRoles":["Executor"],"stages":["build"]}""";

        var (package, source) = await SeedWorkflowAsync(
            "version-1",
            CreateManifestPackage(Manifest, includeManifestJson: false),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateManifestPackage(Manifest),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = source.Id }));

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, source.Id);

        var reported = await _service.ValidateForActivationAsync(candidate.Id);

        var added = Assert.Single(reported.Issues);

        Assert.Contains("manifest.json", added.Message, StringComparison.Ordinal);
        Assert.Contains("source sha256:absent", added.Message, StringComparison.Ordinal);

        var stored = (await _versionRepository.GetByIdAsync(candidate.Id))!;
        var rewritten = await _blobStore.SaveBlobAsync(new MemoryStream(
            CreateManifestPackage("""{"declaredRoles":["Executor"],"stages":[]}""")));

        await ReplaceStoredVersionAsync(
            candidate,
            rewritten.BlobId,
            stored.BlobId,
            stored.CompatibilityReportJson);

        var refused = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: reported.Issues));

        Assert.True(refused.IsBlocked);
        Assert.False(refused.Validation!.IsFullyAcknowledgedBy(reported.Issues));

        var binding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(binding);
        Assert.Equal(source.Id, binding!.ActiveVersionId);
    }

    /// <summary>
    /// A pure rebinding of the routing map is not a semantic change, so it must not produce a blocker at
    /// all on the real service path either: otherwise the guard would train the operator to acknowledge
    /// everything.
    /// </summary>
    [Fact]
    public async Task ActivateVersionAsync_APureRebinding_StaysValidThroughTheWholeServicePath()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();

        var (package, source) = await SeedWorkflowAsync(
            "version-1",
            CreateManifestPackage(
                """
                {"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-1"}},"stages":["build"]}
                """),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateManifestPackage(
                """
                {"stages":["build"],"bindings":{"Executor":{"modelId":"acct-1"}},"declaredRoles":["Executor"]}
                """),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = source.Id }));

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        Assert.True(validation.IsValid, string.Join(" | ", validation.Issues.Select(issue => issue.Message)));

        var activated = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id));

        Assert.True(activated.IsSuccess, activated.ErrorMessage);
        Assert.Equal(candidate.Id, activated.Binding!.ActiveVersionId);
    }

    /// <summary>
    /// The routing ignore used to travel with the container name down the whole subtree, so a nested policy
    /// whose own field is called <c>agent</c> or <c>Agent</c> stayed invisible while the model mapping was
    /// untouched. Every parked shape the packet names has to be proved on the real activation path, not only
    /// by a validation call: with no decision recorded, activation is refused, the active pointer stays on the
    /// source version and the candidate blob stays byte-identical.
    /// </summary>
    [Theory]
    [InlineData(
        ""","bindings":{"Executor":{"modelId":"acct-1"}},"metadata":{"stages":[{"id":"build","agent":"executor"}]}""",
        ""","bindings":{"Executor":{"modelId":"acct-1"}},"metadata":{"stages":[{"id":"build","agent":"skip review"}]}""")]
    [InlineData(
        ""","bindings":{"Executor":{"modelId":"acct-1"}},"metadata":{"stages":[{"id":"build","Agent":"executor"}]}""",
        ""","bindings":{"Executor":{"modelId":"acct-1"}},"metadata":{"stages":[{"id":"build","Agent":"skip review"}]}""")]
    [InlineData(
        ""","bindings":{"Executor":{"modelId":"acct-1"}},"metadata":{"bindings":{"stages":[{"agent":"executor"}]}}""",
        ""","bindings":{"Executor":{"modelId":"acct-1"}},"metadata":{"bindings":{"stages":[{"agent":"skip review"}]}}""")]
    [InlineData(
        ""","bindings":{"Executor":{"modelId":"acct-1"}},"creationMetadata":{"roleBindings":{"stages":[{"Agent":"executor"}]}}""",
        ""","bindings":{"Executor":{"modelId":"acct-1"}},"creationMetadata":{"roleBindings":{"stages":[{"Agent":"skip review"}]}}""")]
    [InlineData(
        ""","bindings":{"Executor":{"modelId":"acct-1","escalation":{"source":"review"}}}""",
        ""","bindings":{"Executor":{"modelId":"acct-1","escalation":{"source":"skip review"}}}""")]
    public async Task ActivateVersionAsync_APolicyParkedBelowANonRoutingPosition_RefusesAndHoldsThePointer(
        string sourceTail,
        string candidateTail)
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();

        var (package, source) = await SeedWorkflowAsync(
            "version-1",
            CreateManifestPackage(
                """{"declaredRoles":["Executor"]""" + sourceTail + "}",
                includeManifestJson: false),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateManifestPackage(
                """{"declaredRoles":["Executor"]""" + candidateTail + "}",
                includeManifestJson: false),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = source.Id }));

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, source.Id);

        var candidateBlobPath = _blobStore.GetBlobPath(candidate.BlobId);
        var candidateBlobBefore = await File.ReadAllBytesAsync(candidateBlobPath);

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        var issue = Assert.Single(validation.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Contains("workflow.json", issue.Message, StringComparison.Ordinal);
        Assert.False(issue.IsNotClearable);

        // No decision is passed at all, so the pointer may not move even for a clearable issue.
        var refused = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id));

        Assert.True(refused.IsBlocked);
        Assert.False(refused.IsSuccess);
        Assert.Null(refused.Binding);
        Assert.NotNull(refused.Validation);
        Assert.Equal(
            new[] { AdaptationBlockerKind.DisallowedSemanticChange },
            refused.Validation!.Blockers);
        Assert.False(refused.Validation.IsFullyAcknowledgedBy(Array.Empty<AdaptationValidationIssue>()));

        var binding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(binding);
        Assert.Equal(source.Id, binding!.ActiveVersionId);
        Assert.Null((await _versionRepository.GetByIdAsync(candidate.Id))!.ActivatedAtUtc);
        Assert.Equal(candidateBlobBefore, await File.ReadAllBytesAsync(candidateBlobPath));
        Assert.True(await _blobStore.VerifyBlobAsync(candidate.BlobId));
    }

    /// <summary>
    /// A declared-name list longer than the bound is only analysed as a prefix, so the entries past the cap
    /// cannot be compared: that is a gap in the evidence, and acknowledging the reported issue does not
    /// clear it. The fixture writes 65 <b>unique</b> names and rewrites the 65th, so the kept prefix and the
    /// structure form are byte-identical on both sides and only the census can see the rewrite.
    /// </summary>
    [Fact]
    public async Task ValidateForActivationAsync_DeclaredNamesBeyondTheBoundedCap_NeverClears()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();

        var cap = SemanticPackageAnalyzer.MaxDeclaredNames;
        var (package, source) = await SeedWorkflowAsync(
            "version-1",
            CreateEntryPointPackage(cap + 1, rewriteLast: false),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"));

        var (_, candidate) = await SeedWorkflowAsync(
            "version-candidate",
            CreateEntryPointPackage(cap + 1, rewriteLast: true),
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("acct-1"),
            package: package,
            versionNumber: 2);

        await ReplaceStoredVersionAsync(
            candidate,
            candidate.BlobId,
            candidate.OriginalHash,
            JsonSerializer.Serialize(new { sourceVersionId = source.Id }));

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, source.Id);

        var candidateBlobPath = _blobStore.GetBlobPath(candidate.BlobId);
        var candidateBlobBefore = await File.ReadAllBytesAsync(candidateBlobPath);

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        Assert.False(validation.IsValid);
        Assert.True(validation.HasUnclearableBlockers);

        // The census alone: the only reported difference is the unverifiable truncation, so no clearable
        // decision exists that could authorise the rewrite of the 65th name.
        var issue = Assert.Single(validation.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.True(issue.IsNotClearable);
        Assert.Contains("entrypoints", issue.Message, StringComparison.Ordinal);
        Assert.Contains("workflow.json", issue.Message, StringComparison.Ordinal);
        Assert.Contains($"limit of {cap} entries", issue.Message, StringComparison.Ordinal);

        var refused = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: true,
            routePolicyId: null,
            acknowledgedBlockerKinds: validation.Blockers,
            acknowledgedBlockerIssues: validation.Issues));

        Assert.True(refused.IsBlocked);
        Assert.False(refused.IsSuccess);
        Assert.Null(refused.Binding);
        Assert.NotNull(refused.Validation);
        Assert.False(refused.Validation!.IsFullyAcknowledgedBy(validation.Issues));

        var binding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(binding);
        Assert.Equal(source.Id, binding!.ActiveVersionId);
        Assert.Null((await _versionRepository.GetByIdAsync(candidate.Id))!.ActivatedAtUtc);
        Assert.Equal(candidateBlobBefore, await File.ReadAllBytesAsync(candidateBlobPath));
        Assert.True(await _blobStore.VerifyBlobAsync(candidate.BlobId));
    }

    /// <summary>
    /// A package whose only formal manifest is <c>workflow.json</c>, optionally with an identical
    /// <c>manifest.json</c> beside it, plus the one document the tier hashes.
    /// </summary>
    private static byte[] CreateManifestPackage(string manifestJson, bool includeManifestJson = true) =>
        includeManifestJson
            ? CreateArchive(
                ("manifest.json", manifestJson),
                ("workflow.json", manifestJson),
                ("README.md", "# Workflow"))
            : CreateArchive(
                ("workflow.json", manifestJson),
                ("README.md", "# Workflow"));

    /// <summary>
    /// More entrypoints than the bounded declared-name cap keeps, with the last one optionally rewritten, so
    /// the only difference sits past the prefix the comparison can see.
    /// </summary>
    private static byte[] CreateEntryPointPackage(int count, bool rewriteLast)
    {
        var entrypoints = Enumerable
            .Range(0, count)
            .Select(index => rewriteLast && index == count - 1
                ? $"\"entry-{index:D4}-rewritten\""
                : $"\"entry-{index:D4}\"")
            .ToArray();

        return CreateArchive(
            ("workflow.json", "{\"entrypoints\":[" + string.Join(",", entrypoints) + "]}"),
            ("README.md", "# Workflow"));
    }

    private async Task<string> SaveCandidateWithRewrittenStageAsync(
        WorkflowVersion source,
        bool expandedSemanticScope)
    {
        var response = JsonSerializer.Serialize(new
        {
            mappings = new[]
            {
                new
                {
                    role = "Executor",
                    originalRoute = "acct-1",
                    targetRoute = "acct-1",
                    targetModelId = "acct-1",
                    rationale = "Rebinding the executor.",
                    isSemanticChange = false,
                    blockerKind = (string?)null
                }
            },
            rationale = "Rewrites the stage definition on user-confirmed scope.",
            warnings = Array.Empty<string>(),
            blockers = Array.Empty<string>(),
            fileModifications = new Dictionary<string, string>
            {
                ["stages/build.md"] = "Stage 1: build the package. Stage 2: publish the release."
            }
        });

        _modelInvoker.EnqueueResponse(response);

        var adaptation = CreateAdaptationService();

        var result = await adaptation.StartAdaptationAsync(new AdaptationExecutionRequest(
            source.Id,
            "acct-1",
            AdaptationGoal.Balanced,
            allowExpandedSemanticScope: expandedSemanticScope));

        // The scope flag is prompt context: the stage rewrite is a blocker either way, and exactly one
        // semantic change is detected, so the session shows the operator one issue to decide on.
        Assert.Equal(expandedSemanticScope, result.AllowExpandedSemanticScope);
        Assert.Contains(AdaptationBlockerKind.DisallowedSemanticChange, result.Blockers);
        Assert.Single(result.SemanticDiff.DetectedChanges);

        var saved = await adaptation.SaveCandidateVersionAsync(result.SessionId);

        return saved.VersionId;
    }

    private WorkflowAdaptationService CreateAdaptationService() => new(
        _versionRepository,
        _packageRepository,
        _scratchManager,
        new WorkflowSecretScanner(new SensitiveDataFilter()),
        new WorkflowAdaptationPromptBuilder(),
        new ChatCapabilityFixtureCatalog(new SanitizedCatalogProvider(
            _providerRepository,
            _accountRepository,
            _healthStateRepository)),
        new SqliteQuotaSnapshotRepository(_database.Factory),
        _accountRepository,
        _modelInvoker,
        new AdaptationResponseParser(),
        new AdaptationReferenceValidator(),
        new SemanticDiffEngine(),
        new WorkflowDiffService(),
        _blobStore,
        _time);

    private async Task ReplaceStoredVersionAsync(
        WorkflowVersion version,
        string blobId,
        string originalHash,
        string? compatibilityReportJson)
    {
        await _versionRepository.DeleteAsync(version.Id);

        await _versionRepository.UpsertAsync(new WorkflowVersion(
            version.Id,
            version.WorkflowPackageId,
            version.VersionNumber,
            blobId,
            originalHash,
            version.SourceType,
            version.EntrypointsJson,
            version.DeclaredRolesJson,
            version.BindingsJson,
            compatibilityReportJson,
            version.CreationMetadataJson,
            version.CreatedAtUtc,
            version.ActivatedAtUtc));
    }

    [Fact]
    public async Task ValidateForActivationAsync_ThrowsForMissingVersion()
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.ValidateForActivationAsync("missing-version"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateForActivationAsync_RejectsBlankVersionId(string versionId)
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ValidateForActivationAsync(versionId));
    }

    [Fact]
    public async Task ValidateForActivationAsync_CleansScratchWhenSourceBlobIsTampered()
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
            () => _service.ValidateForActivationAsync(version.Id));

        Assert.Empty(GetAdaptationScratchDirectories());
    }

    [Fact]
    public async Task ActivateVersionAsync_BlocksUnacknowledgedBlockersAndLeavesBindingUntouched()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));
        var (_, candidate) = await SeedWorkflowAsync(
            "version-2",
            CreateArchive(("README.md", $"api_key={SecretValue}")),
            packageName: package.Name,
            package: package,
            versionNumber: 2);

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, version.Id);

        var candidateBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(candidate.BlobId));

        var result = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id));

        Assert.False(result.IsSuccess);
        Assert.True(result.IsBlocked);
        Assert.NotNull(result.Validation);
        Assert.Contains(AdaptationBlockerKind.DetectedSecret, result.Validation!.Blockers);
        Assert.Contains("not acknowledged", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var storedBinding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(version.Id, storedBinding!.ActiveVersionId);

        var storedCandidate = await _versionRepository.GetByIdAsync(candidate.Id);

        Assert.NotNull(storedCandidate);
        Assert.Null(storedCandidate!.ActivatedAtUtc);
        Assert.Equal(candidateBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(candidate.BlobId)));
    }

    [Fact]
    public async Task ActivateVersionAsync_LegacyGlobalFlagDoesNotAuthorizeMultipleBlockerKinds()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));
        var (_, candidate) = await SeedWorkflowAsync(
            "version-2",
            CreateArchive(("README.md", $"api_key={SecretValue}")),
            packageName: package.Name,
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("model-missing"),
            package: package,
            versionNumber: 2);

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, version.Id);

        // Neither the legacy blanket flag nor the legacy per-kind set may move the pointer on its own.
        var result = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: true,
            routePolicyId: null,
            acknowledgedBlockerKinds: new[]
            {
                AdaptationBlockerKind.DetectedSecret,
                AdaptationBlockerKind.MissingModel
            }));

        Assert.False(result.IsSuccess);
        Assert.True(result.IsBlocked);
        Assert.Contains("not acknowledged", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var storedBinding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(version.Id, storedBinding!.ActiveVersionId);
    }

    [Fact]
    public async Task ActivateVersionAsync_ConfirmedEveryReportedIssue_ActivatesAndKeepsRecordsImmutable()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));
        var (_, candidate) = await SeedWorkflowAsync(
            "version-2",
            CreateArchive(("README.md", $"api_key={SecretValue}")),
            packageName: package.Name,
            package: package,
            versionNumber: 2);

        var binding = await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, version.Id);

        // The first attempt shows the fresh issue list and moves nothing.
        var blocked = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id));

        Assert.True(blocked.IsBlocked);
        Assert.NotNull(blocked.Validation);
        Assert.Contains(AdaptationBlockerKind.DetectedSecret, blocked.Validation!.Blockers);

        var sourceBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(version.BlobId));
        var candidateBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(candidate.BlobId));
        var versionsBefore = await _database.CountAsync("WorkflowVersions");

        _time.Advance(TimeSpan.FromMinutes(15));

        // The operator decides on exactly the issues that were shown.
        var result = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: blocked.Validation.Issues));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Binding);
        Assert.Equal(candidate.Id, result.Binding!.ActiveVersionId);
        Assert.Equal(binding.Binding.CreatedAtUtc, result.Binding.CreatedAtUtc);
        Assert.Equal(Timestamp.AddMinutes(15), result.Binding.UpdatedAtUtc);

        var storedBinding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(candidate.Id, storedBinding!.ActiveVersionId);

        var storedVersion = await _versionRepository.GetByIdAsync(version.Id);
        var storedCandidate = await _versionRepository.GetByIdAsync(candidate.Id);

        Assert.NotNull(storedVersion);
        Assert.NotNull(storedCandidate);
        Assert.Null(storedVersion!.ActivatedAtUtc);
        Assert.Null(storedCandidate!.ActivatedAtUtc);
        Assert.Equal(versionsBefore, await _database.CountAsync("WorkflowVersions"));
        Assert.Equal(sourceBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(version.BlobId)));
        Assert.Equal(candidateBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(candidate.BlobId)));
        Assert.Equal(0, await _database.CountAsync("ProjectLocks"));
    }

    [Fact]
    public async Task ActivateVersionAsync_CreatesBindingWhenNoneExists()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        var result = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            version.Id));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Binding);
        Assert.Equal(version.Id, result.Binding!.ActiveVersionId);
        Assert.Equal(Timestamp, result.Binding.CreatedAtUtc);
        Assert.Equal(1, await _database.CountAsync("WorkflowBindings"));
    }

    [Fact]
    public async Task ActivateVersionAsync_RequiresADecisionForEveryReportedIssue()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));
        var (_, candidate) = await SeedWorkflowAsync(
            "version-2",
            CreateArchive(("README.md", $"api_key={SecretValue}")),
            packageName: package.Name,
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("model-missing"),
            package: package,
            versionNumber: 2);

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, version.Id);

        var candidateBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(candidate.BlobId));

        var withoutDecision = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id));

        Assert.True(withoutDecision.IsBlocked);
        Assert.NotNull(withoutDecision.Validation);
        Assert.Contains(AdaptationBlockerKind.DetectedSecret, withoutDecision.Validation!.Blockers);
        Assert.Contains(AdaptationBlockerKind.MissingModel, withoutDecision.Validation.Blockers);

        var reported = withoutDecision.Validation.Issues;

        // One recorded decision does not cover the second reported issue.
        var partialDecision = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: reported.Take(1).ToArray()));

        Assert.True(partialDecision.IsBlocked);

        var storedBinding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(version.Id, storedBinding!.ActiveVersionId);

        var fullDecision = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: reported));

        Assert.True(fullDecision.IsSuccess);
        Assert.NotNull(fullDecision.Binding);
        Assert.Equal(candidate.Id, fullDecision.Binding!.ActiveVersionId);
        Assert.Equal(candidateBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(candidate.BlobId)));
    }

    [Fact]
    public async Task ActivateVersionAsync_ConfirmedMissingModelForOneModel_DoesNotAuthorizeAnother()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));
        var (_, candidate) = await SeedWorkflowAsync(
            "version-2",
            CreateArchive(("README.md", "# Workflow v2")),
            packageName: package.Name,
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("model-a"),
            package: package,
            versionNumber: 2);

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, version.Id);

        // The first validation reports the missing model of this candidate and nothing else.
        var first = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id));

        Assert.True(first.IsBlocked);
        Assert.NotNull(first.Validation);
        var confirmedForModelA = first.Validation!.Issues;

        Assert.Contains(
            confirmedForModelA,
            issue => issue.Kind == AdaptationBlockerKind.MissingModel
                && issue.Message.Contains("model-a", StringComparison.Ordinal));

        // The revalidation now reports a different missing model for the same role.
        await ReplaceStoredVersionBindingsAsync(candidate, CreateBindingsJson("model-b"));

        var staleDecision = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: confirmedForModelA));

        Assert.True(staleDecision.IsBlocked, staleDecision.ErrorMessage);
        Assert.NotNull(staleDecision.Validation);
        Assert.Contains(
            staleDecision.Validation!.Issues,
            issue => issue.Kind == AdaptationBlockerKind.MissingModel
                && issue.Message.Contains("model-b", StringComparison.Ordinal));
        Assert.DoesNotContain(
            staleDecision.Validation.Issues,
            issue => issue.Message.Contains("model-a", StringComparison.Ordinal));

        var storedBinding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(version.Id, storedBinding!.ActiveVersionId);

        // Only after the newly reported issue is shown and confirmed on its own does the pointer move.
        var accepted = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: staleDecision.Validation.Issues));

        Assert.True(accepted.IsSuccess);
        Assert.Equal(candidate.Id, accepted.Binding!.ActiveVersionId);
    }

    [Fact]
    public async Task ActivateVersionAsync_FreshlyAddedIssueOfAConfirmedKind_StillBlocks()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));
        var (_, candidate) = await SeedWorkflowAsync(
            "version-2",
            CreateArchive(("README.md", "# Workflow v2")),
            packageName: package.Name,
            declaredRolesJson: """["Executor"]""",
            bindingsJson: CreateBindingsJson("model-a"),
            package: package,
            versionNumber: 2);

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, version.Id);

        var first = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id));

        Assert.True(first.IsBlocked);
        Assert.NotNull(first.Validation);
        var confirmed = first.Validation!.Issues;
        Assert.Single(confirmed);

        // A second issue of the very same kind appears in the revalidation.
        await ReplaceStoredVersionBindingsAsync(candidate, CreateBindingsJsonFor("model-a", "model-b"));

        var staleDecision = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: confirmed));

        Assert.True(staleDecision.IsBlocked);
        Assert.NotNull(staleDecision.Validation);
        Assert.Equal(2, staleDecision.Validation!.Issues.Count);
        Assert.Equal(
            new[] { AdaptationBlockerKind.MissingModel },
            staleDecision.Validation.Blockers);

        var storedBinding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(version.Id, storedBinding!.ActiveVersionId);

        var accepted = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            candidate.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: staleDecision.Validation.Issues));

        Assert.True(accepted.IsSuccess);
        Assert.Equal(candidate.Id, accepted.Binding!.ActiveVersionId);
    }

    [Fact]
    public async Task ActivateVersionAsync_CancelledCommand_LeavesThePointerUntouched()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));
        var (_, candidate) = await SeedWorkflowAsync(
            "version-2",
            CreateArchive(("README.md", "# Workflow v2")),
            packageName: package.Name,
            package: package,
            versionNumber: 2);

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, version.Id);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.ActivateVersionAsync(
                new WorkflowActivationRequest("project-1", package.Id, candidate.Id),
                cancellation.Token));

        var storedBinding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(version.Id, storedBinding!.ActiveVersionId);
    }

    [Fact]
    public async Task ActivateVersionAsync_ReturnsFailureForMissingVersion()
    {
        await _database.InitializeAsync();

        var result = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            "pkg-missing",
            "version-missing"));

        Assert.False(result.IsSuccess);
        Assert.False(result.IsBlocked);
        Assert.Contains("does not exist", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(0, await _database.CountAsync("WorkflowBindings"));
    }

    [Fact]
    public async Task RollbackToVersionAsync_RepointsBindingAndReportsPreviousVersion()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, first) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow v1")));
        var (_, second) = await SeedWorkflowAsync(
            "version-2",
            CreateArchive(("README.md", "# Workflow v2")),
            packageName: package.Name,
            package: package,
            versionNumber: 2);

        await CreateBindingService().BindWorkflowToProjectAsync("project-1", package.Id, second.Id);

        var firstBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(first.BlobId));
        var secondBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(second.BlobId));
        var versionsBefore = await _database.CountAsync("WorkflowVersions");

        _time.Advance(TimeSpan.FromHours(1));

        var result = await _service.RollbackToVersionAsync("project-1", package.Id, first.Id);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Binding);
        Assert.Equal(first.Id, result.Binding!.ActiveVersionId);
        Assert.Equal(second.Id, result.PreviousVersionId);
        Assert.Equal(first.Id, result.TargetVersionId);

        var storedBinding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(first.Id, storedBinding!.ActiveVersionId);
        Assert.Equal(Timestamp.AddHours(1), storedBinding.UpdatedAtUtc);

        var storedFirst = await _versionRepository.GetByIdAsync(first.Id);
        var storedSecond = await _versionRepository.GetByIdAsync(second.Id);

        Assert.NotNull(storedFirst);
        Assert.NotNull(storedSecond);
        Assert.Null(storedFirst!.ActivatedAtUtc);
        Assert.Null(storedSecond!.ActivatedAtUtc);
        Assert.Equal(versionsBefore, await _database.CountAsync("WorkflowVersions"));
        Assert.Equal(firstBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(first.BlobId)));
        Assert.Equal(secondBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(second.BlobId)));
    }

    [Fact]
    public async Task RollbackToVersionAsync_RefusesABlockedTargetUntilItsBlockersAreAcknowledged()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await SeedProviderAndAccountAsync();
        var (package, first) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow v1")));
        var (_, blocked) = await SeedWorkflowAsync(
            "version-2",
            CreateArchive(("README.md", $"api_key={SecretValue}")),
            packageName: package.Name,
            package: package,
            versionNumber: 2);

        // The blocked version becomes active once with a recorded decision, then the clean version is activated.
        var blockedWithoutDecision = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            blocked.Id));

        Assert.True(blockedWithoutDecision.IsBlocked);
        Assert.NotNull(blockedWithoutDecision.Validation);

        var activateBlocked = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            blocked.Id,
            acknowledgeBlockers: false,
            routePolicyId: null,
            acknowledgedBlockerIssues: blockedWithoutDecision.Validation!.Issues));

        Assert.True(activateBlocked.IsSuccess);

        var activateFirst = await _service.ActivateVersionAsync(new WorkflowActivationRequest(
            "project-1",
            package.Id,
            first.Id));

        Assert.True(activateFirst.IsSuccess);

        var firstBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(first.BlobId));
        var blockedBytesBefore = await File.ReadAllBytesAsync(_blobStore.GetBlobPath(blocked.BlobId));

        // A rollback to an earlier version must not silently accept the blockers that activation refuses.
        var refused = await _service.RollbackToVersionAsync("project-1", package.Id, blocked.Id);

        Assert.False(refused.IsSuccess);
        Assert.True(refused.IsBlocked);
        Assert.NotNull(refused.Validation);
        Assert.Contains(AdaptationBlockerKind.DetectedSecret, refused.Validation!.Blockers);
        Assert.Contains("not acknowledged", refused.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var storedBinding = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(storedBinding);
        Assert.Equal(first.Id, storedBinding!.ActiveVersionId);

        // The blanket flag and the per-kind set do not authorize a rollback either.
        var legacyFlag = await _service.RollbackToVersionAsync(
            "project-1",
            package.Id,
            blocked.Id,
            acknowledgeBlockers: true,
            acknowledgedBlockerKinds: new[] { AdaptationBlockerKind.DetectedSecret });

        Assert.True(legacyFlag.IsBlocked);

        var stillFirst = await _bindingRepository.GetByProjectAndPackageAsync("project-1", package.Id);

        Assert.NotNull(stillFirst);
        Assert.Equal(first.Id, stillFirst!.ActiveVersionId);

        var accepted = await _service.RollbackToVersionAsync(
            "project-1",
            package.Id,
            blocked.Id,
            acknowledgeBlockers: false,
            acknowledgedBlockerKinds: null,
            refused.Validation.Issues);

        Assert.True(accepted.IsSuccess);
        Assert.NotNull(accepted.Binding);
        Assert.Equal(blocked.Id, accepted.Binding!.ActiveVersionId);
        Assert.Equal(first.Id, accepted.PreviousVersionId);

        // Rollback keeps both blobs byte-identical.
        Assert.Equal(firstBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(first.BlobId)));
        Assert.Equal(blockedBytesBefore, await File.ReadAllBytesAsync(_blobStore.GetBlobPath(blocked.BlobId)));
    }

    [Fact]
    public async Task RollbackToVersionAsync_FailsForMissingVersion()
    {
        await _database.InitializeAsync();

        var result = await _service.RollbackToVersionAsync("project-1", "pkg-1", "version-missing");

        Assert.False(result.IsSuccess);
        Assert.Contains("does not exist", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Null(result.Binding);
    }

    [Fact]
    public async Task RollbackToVersionAsync_FailsForPackageMismatch()
    {
        await _database.InitializeAsync();
        var (_, foreignVersion) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        var result = await _service.RollbackToVersionAsync(
            "project-1",
            "pkg-other",
            foreignVersion.Id);

        Assert.False(result.IsSuccess);
        Assert.Contains("does not belong", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackToVersionAsync_FailsWhenPackageIsNotBound()
    {
        await _database.InitializeAsync();
        var (package, version) = await SeedWorkflowAsync("version-1", CreateArchive(
            ("README.md", "# Workflow")));

        var result = await _service.RollbackToVersionAsync("project-1", package.Id, version.Id);

        Assert.False(result.IsSuccess);
        Assert.Contains("not bound", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "pkg-1", "ver-1")]
    [InlineData("project-1", "", "ver-1")]
    [InlineData("project-1", "pkg-1", "")]
    public async Task RollbackToVersionAsync_RejectsBlankArguments(string projectId, string packageId, string versionId)
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.RollbackToVersionAsync(projectId, packageId, versionId));
    }

    private WorkflowBindingService CreateBindingService()
    {
        return new WorkflowBindingService(_bindingRepository, _packageRepository, _versionRepository, _time);
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

    /// <summary>
    /// A package with more files than the bounded analysis budget, so the sorted tail is dropped before it
    /// is read. The optional rewrite lands in the dropped part, which is exactly the case that used to
    /// vanish from the comparison.
    /// </summary>
    private static byte[] CreateStageArchive(bool rewriteTail)
    {
        var changedTail = SemanticPackageAnalyzer.MaxAnalyzedFiles + 4;

        return WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(archive, "README.md", "# Workflow");

            for (var index = 0; index <= SemanticPackageAnalyzer.MaxAnalyzedFiles + 4; index++)
            {
                WorkflowTestArchiveFactory.AddEntry(
                    archive,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"stages/step-{index:D4}.md"),
                    rewriteTail && index == changedTail
                        ? "Stage 1: build. Stage 2: publish the release."
                        : "step");
            }
        });
    }

    private static string CreateBindingsJson(
        string targetModelId,
        string role = "Executor",
        bool isSemanticChange = false)
    {
        return CreateBindingsJsonFor(role, isSemanticChange, targetModelId);
    }

    private static string CreateBindingsJsonFor(params string[] targetModelIds)
    {
        return CreateBindingsJsonFor("Executor", isSemanticChange: false, targetModelIds);
    }

    private static string CreateBindingsJsonFor(
        string role,
        bool isSemanticChange,
        params string[] targetModelIds)
    {
        return JsonSerializer.Serialize(targetModelIds.Select(targetModelId => new Dictionary<string, object?>
        {
            ["role"] = role,
            ["originalRoute"] = "acct-1",
            ["targetRoute"] = "acct-1:" + targetModelId,
            ["targetModelId"] = targetModelId,
            ["rationale"] = "Test mapping.",
            ["isSemanticChange"] = isSemanticChange,
            ["blockerKind"] = null
        }));
    }

    /// <summary>
    /// Simulates drift of the stored state that the gate re-validates: the row is re-stored with different
    /// bindings while the version id and its blob stay byte-identical. This is the only way to make a
    /// second revalidation of the same candidate report a different issue list, which is exactly the drift
    /// the fail-closed identity gate has to survive.
    /// </summary>
    private async Task ReplaceStoredVersionBindingsAsync(WorkflowVersion version, string? bindingsJson)
    {
        await _versionRepository.DeleteAsync(version.Id);

        await _versionRepository.UpsertAsync(new WorkflowVersion(
            version.Id,
            version.WorkflowPackageId,
            version.VersionNumber,
            version.BlobId,
            version.OriginalHash,
            version.SourceType,
            version.EntrypointsJson,
            version.DeclaredRolesJson,
            bindingsJson,
            version.CompatibilityReportJson,
            version.CreationMetadataJson,
            version.CreatedAtUtc,
            version.ActivatedAtUtc));
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
        double? reserveThreshold = 0.25)
    {
        await _providerRepository.UpsertAsync(new ProviderProfile(
            "prov-1",
            "OpenCode Local",
            BackendType.OpenCode,
            "http://127.0.0.1:11434/v1",
            executablePath: null,
            DataClassification.PrivateSource,
            isEnabled: true));

        await _accountRepository.SaveAsync(new Account(
            accountId,
            "prov-1",
            "Primary Account",
            providerNativeId: NativeModelId,
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
        string? bindingsJson = null,
        WorkflowPackage? package = null,
        int versionNumber = 1)
    {
        var blob = await _blobStore.SaveBlobAsync(new MemoryStream(archiveBytes));

        package ??= new WorkflowPackage(
            $"pkg-{versionId}",
            packageName,
            "Activation test workflow",
            new[] { "test" },
            WorkflowSourceType.ZipArchive,
            blob.BlobId,
            blob.BlobId,
            Timestamp,
            Timestamp);

        var version = new WorkflowVersion(
            versionId,
            package.Id,
            versionNumber,
            blob.BlobId,
            blob.BlobId,
            WorkflowSourceType.ZipArchive,
            entrypointsJson: null,
            declaredRolesJson,
            bindingsJson,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            Timestamp,
            activatedAtUtc: null);

        await _packageRepository.UpsertAsync(package);
        await _versionRepository.UpsertAsync(version);

        return (package, version);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
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

        public Func<string, Task>? AfterExtractAsync { get; set; }

        public Task<ScratchWorkspace> CreateWorkspaceAsync(
            ScratchScope scope,
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            CreatedScopes.Add(scope);

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
            return _inner.PostOperationSourceHashCheckAsync(blobId, cancellationToken);
        }
    }
}
