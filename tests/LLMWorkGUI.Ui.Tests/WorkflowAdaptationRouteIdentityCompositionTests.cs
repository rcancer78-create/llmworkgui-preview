using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Projects;
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
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Synthetic route/model composition evidence (fixture policy, no stored data authority or HTTP): the production
/// <see cref="SanitizedCatalogProvider"/> builds the catalog rows, the adaptation dialog turns a row
/// into a route id, and the historical <see cref="AdaptationProtocolFixtureInvoker"/> turns that route id
/// back into an OpenCode request. The route id under test is the one the dialog actually selects, and
/// the model sent to OpenCode must be the seeded backend-native model id, never the account id.
/// </summary>
public sealed class WorkflowAdaptationRouteIdentityCompositionTests
{

    // Synthetic policy for existing route/stream protocol tests only. Real SQL policy tests use the sealed store.
    private sealed class FixtureProtocolPolicy : IAdaptationEgressPolicy
    {
        public Task<Guid> PrepareAsync(AdaptationModelRequest request,CancellationToken token) => Task.FromResult(Guid.NewGuid());
        public Task<AdaptationPolicySnapshot> ValidateAsync(AdaptationModelRequest request, AdaptationRouteIdentity identity,
            string prompt, string? expected, CancellationToken token)
            => Task.FromResult(new AdaptationPolicySnapshot("fixture-only", Environment.CurrentDirectory));
    }
    private static AdaptationProtocolFixtureInvoker CreateProtocolInvoker(
        IOpenCodeSessionLifecycleService? openCodeLifecycle = null, IOpenCodeClient? openCodeClient = null,
        IProviderProfileRepository? providerProfileRepository = null, IAccountRepository? accountRepository = null)
        => new(openCodeLifecycle,openCodeClient,providerProfileRepository,accountRepository,egressPolicy:new FixtureProtocolPolicy());
    private const string OpenCodeProfileId = "prov-oc";
    private const string OpenCodeAccountId = "acct-oc";
    private const string NativeModelId = "opencode/space-bunny-free";
    private const string ConfiguredModelId = "opencode/space-bunny-free";
    private const string SecondConfiguredModelId = "opencode/space-bunny-pro";
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DialogRouteId_ProducesOpenCodeRequestsForTheSeededNativeModelId()
    {
        var world = new RouteWorld();
        world.AddOpenCodeAccount(OpenCodeAccountId, OpenCodeProfileId, NativeModelId);

        var viewModel = CreateViewModel(world);
        await OpenAsync(viewModel, world);

        var route = Assert.Single(viewModel.AvailableRoutes);

        // The route id is a composite identity, not the account id.
        Assert.NotEqual(OpenCodeAccountId, route.Id);
        Assert.True(AdaptationRouteIdentity.TryParse(route.Id, out var identity));
        Assert.Equal(OpenCodeAccountId, identity.AccountId);
        Assert.Equal(OpenCodeProfileId, identity.ProviderProfileId);
        Assert.Equal(BackendType.OpenCode, identity.Backend);
        Assert.Equal(NativeModelId, identity.BackendModelId);

        await viewModel.StartAdaptationAsync();

        // The very same route id the dialog produced is what the engine is asked to invoke.
        var executionRequest = Assert.Single(world.Adaptation.StartRequests);
        Assert.Equal(route.Id, executionRequest.AdapterRouteId);

        var lifecycle = new RecordingOpenCodeLifecycle();
        var invoker = CreateProtocolInvoker(lifecycle, new RecordingOpenCodeClient(), world.Profiles, world.Accounts);

        await invoker.InvokeModelAsync(new AdaptationModelRequest(
            executionRequest.AdapterRouteId,
            OpenCodeAccountId,
            "Adapt the workflow.",
            new[] { new AdaptationTurnMessage("user", "Adapt the workflow.") }));

        var createRequest = Assert.Single(lifecycle.CreateRequests);
        var turn = Assert.Single(lifecycle.TurnRequests);

        Assert.Equal(NativeModelId, createRequest.Model);
        Assert.Equal(NativeModelId, turn.Request.Model);
        Assert.NotEqual(OpenCodeAccountId, createRequest.Model);
        Assert.NotEqual(OpenCodeAccountId, turn.Request.Model);
    }

    [Fact]
    public async Task OpenCodeAccountWithoutNativeModelId_IsNotOfferedAndNeverSendsItsAccountId()
    {
        var world = new RouteWorld();
        world.AddOpenCodeAccount("plugin-account", OpenCodeProfileId, providerNativeId: null);

        var viewModel = CreateViewModel(world);
        await OpenAsync(viewModel, world);

        // No local source knows a model for this profile, so no route is offered at all: the account id
        // is never promoted into a model.
        Assert.Empty(viewModel.AvailableRoutes);
        Assert.Null(viewModel.SelectedRoute);

        var lifecycle = new RecordingOpenCodeLifecycle();
        var invoker = CreateProtocolInvoker(lifecycle, new RecordingOpenCodeClient(), world.Profiles, world.Accounts);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest("plugin-account")));

        Assert.Empty(lifecycle.CreateRequests);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task OpenCodeAccountWithoutNativeId_IsUsableThroughItsLocallyConfiguredModel()
    {
        var world = new RouteWorld();
        world.ModelCatalog.Configure(OpenCodeProfileId, ConfiguredModelId);
        world.AddOpenCodeAccount(OpenCodeAccountId, OpenCodeProfileId, providerNativeId: null);

        var viewModel = CreateViewModel(world);
        await OpenAsync(viewModel, world);

        // The production normal OpenCode account shape now has a working route.
        var route = Assert.Single(viewModel.AvailableRoutes);
        Assert.True(string.IsNullOrEmpty(viewModel.ErrorMessage));

        Assert.True(AdaptationRouteIdentity.TryParse(route.Id, out var identity));
        Assert.Equal(OpenCodeAccountId, identity.AccountId);
        Assert.Equal(OpenCodeProfileId, identity.ProviderProfileId);
        Assert.Equal(BackendType.OpenCode, identity.Backend);
        Assert.Equal(ConfiguredModelId, identity.BackendModelId);
        Assert.Contains(ConfiguredModelId, route.Name, StringComparison.Ordinal);

        var lifecycle = new RecordingOpenCodeLifecycle();
        var invoker = CreateProtocolInvoker(lifecycle, new RecordingOpenCodeClient(), world.Profiles, world.Accounts);

        await viewModel.StartAdaptationAsync();

        var executionRequest = Assert.Single(world.Adaptation.StartRequests);
        Assert.Equal(route.Id, executionRequest.AdapterRouteId);

        await invoker.InvokeModelAsync(new AdaptationModelRequest(
            executionRequest.AdapterRouteId,
            OpenCodeAccountId,
            "Adapt the workflow.",
            new[] { new AdaptationTurnMessage("user", "Adapt the workflow.") }));

        var createRequest = Assert.Single(lifecycle.CreateRequests);
        var turn = Assert.Single(lifecycle.TurnRequests);

        Assert.Equal(ConfiguredModelId, createRequest.Model);
        Assert.Equal(ConfiguredModelId, turn.Request.Model);
        Assert.NotEqual(OpenCodeAccountId, createRequest.Model);
        Assert.NotEqual(OpenCodeAccountId, turn.Request.Model);
    }

    [Fact]
    public async Task AccountWithSeveralConfiguredModels_OffersOneUnambiguousRoutePerModel()
    {
        var world = new RouteWorld();
        world.ModelCatalog.Configure(OpenCodeProfileId, ConfiguredModelId, SecondConfiguredModelId);
        world.AddOpenCodeAccount(OpenCodeAccountId, OpenCodeProfileId, providerNativeId: null);

        var viewModel = CreateViewModel(world);
        await OpenAsync(viewModel, world);

        Assert.Equal(2, viewModel.AvailableRoutes.Count);
        Assert.Equal(2, viewModel.AvailableRoutes.Select(route => route.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, viewModel.AvailableRoutes.Select(route => route.Name).Distinct(StringComparer.Ordinal).Count());

        var lifecycle = new RecordingOpenCodeLifecycle();
        var invoker = CreateProtocolInvoker(lifecycle, new RecordingOpenCodeClient(), world.Profiles, world.Accounts);

        foreach (var route in viewModel.AvailableRoutes)
        {
            Assert.True(AdaptationRouteIdentity.TryParse(route.Id, out var identity));
            Assert.Equal(OpenCodeAccountId, identity.AccountId);

            await invoker.InvokeModelAsync(CreateRequest(route.Id));

            // The request names exactly the model the chosen route carries, never the account id.
            Assert.Equal(identity.BackendModelId, lifecycle.CreateRequests[^1].Model);
            Assert.Equal(identity.BackendModelId, lifecycle.TurnRequests[^1].Request.Model);
            Assert.NotEqual(OpenCodeAccountId, identity.BackendModelId);
        }

        Assert.Equal(2, lifecycle.CreateRequests.Count);
        Assert.Equal(
            new[] { ConfiguredModelId, SecondConfiguredModelId },
            lifecycle.CreateRequests.Select(request => request.Model).ToArray());
    }

    [Fact]
    public async Task ConfiguredModelsOfAnotherProfile_AreNeverOfferedToThisAccount()
    {
        var world = new RouteWorld();
        world.ModelCatalog.Configure("prov-other", "other/model-only-here");
        world.AddOpenCodeAccount(OpenCodeAccountId, OpenCodeProfileId, providerNativeId: null);

        var viewModel = CreateViewModel(world);
        await OpenAsync(viewModel, world);

        // The source is queried for the owning profile on the owning backend, so a model configured for
        // a different provider cannot leak into this account's routes.
        Assert.Empty(viewModel.AvailableRoutes);
        Assert.All(world.ModelCatalog.Queries, query => Assert.Equal(OpenCodeProfileId, query.ProviderProfileId));
        Assert.Contains(world.ModelCatalog.Queries, query => query.Backend == BackendType.OpenCode);
    }

    [Fact]
    public async Task ImplausibleConfiguredValue_IsNotOfferedAsAModel()
    {
        var world = new RouteWorld();
        world.ModelCatalog.Configure(OpenCodeProfileId, @"C:\Users\tester\.codex\home");
        world.AddOpenCodeAccount(OpenCodeAccountId, OpenCodeProfileId, providerNativeId: null);

        var viewModel = CreateViewModel(world);
        await OpenAsync(viewModel, world);

        Assert.Empty(viewModel.AvailableRoutes);
    }

    [Fact]
    public async Task OpenCodeAccountIdNamedCursor_StillReachesOpenCode()
    {
        var world = new RouteWorld();
        world.AddOpenCodeAccount("cursor", OpenCodeProfileId, NativeModelId);

        var viewModel = CreateViewModel(world);
        await OpenAsync(viewModel, world);

        var route = Assert.Single(viewModel.AvailableRoutes);

        var lifecycle = new RecordingOpenCodeLifecycle();
        var invoker = CreateProtocolInvoker(lifecycle, new RecordingOpenCodeClient(), world.Profiles, world.Accounts);

        await invoker.InvokeModelAsync(CreateRequest(route.Id));

        Assert.Equal(NativeModelId, Assert.Single(lifecycle.CreateRequests).Model);
        Assert.Equal(NativeModelId, Assert.Single(lifecycle.TurnRequests).Request.Model);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("opencode")]
    public async Task CursorAccountIdNamedReservedLiteral_NeverReachesOpenCode(string accountId)
    {
        var world = new RouteWorld();
        world.AddAccount(accountId, "prov-cursor", BackendType.CursorAcp, NativeModelId);

        var viewModel = CreateViewModel(world);
        await OpenAsync(viewModel, world);

        // A Cursor route is not offered for adaptation.
        Assert.Empty(viewModel.AvailableRoutes);

        var lifecycle = new RecordingOpenCodeLifecycle();
        var invoker = CreateProtocolInvoker(lifecycle, new RecordingOpenCodeClient(), world.Profiles, world.Accounts);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(accountId)));

        Assert.Empty(lifecycle.CreateRequests);
    }

    [Fact]
    public async Task NonAdaptationBackendsAreNeverOfferedAsRoutes()
    {
        var world = new RouteWorld();
        world.AddAccount("acct-cursor", "prov-cursor", BackendType.CursorAcp, "cursor-model");
        world.AddAccount("acct-mirasim", "prov-mirasim", BackendType.Mirasim, "mirasim-model");
        world.AddAccount("acct-starcli", "prov-starcli", BackendType.StarCliProxy, "starcli-model");
        world.AddOpenCodeAccount(OpenCodeAccountId, OpenCodeProfileId, NativeModelId);

        var viewModel = CreateViewModel(world);
        await OpenAsync(viewModel, world);

        var route = Assert.Single(viewModel.AvailableRoutes);

        Assert.True(AdaptationRouteIdentity.TryParse(route.Id, out var identity));
        Assert.Equal(OpenCodeAccountId, identity.AccountId);
    }

    private static WorkflowAdaptationViewModel CreateViewModel(RouteWorld world) =>
        new(world.Adaptation, activationService: null, new SanitizedCatalogProvider(
            world.Profiles,
            world.Accounts,
            world.HealthStates,
            world.ModelCatalog));

    private static Task OpenAsync(WorkflowAdaptationViewModel viewModel, RouteWorld world) =>
        viewModel.OpenForVersionAsync(world.Version, world.Package, world.Project);

    private static AdaptationModelRequest CreateRequest(string routeId) => new(
        routeId,
        routeId,
        "Adapt the workflow.",
        new[] { new AdaptationTurnMessage("user", "Adapt the workflow.") });

    private sealed class RouteWorld
    {
        public StubProviderProfileRepository Profiles { get; } = new();

        public StubAccountRepository Accounts { get; } = new();

        public StubHealthStateRepository HealthStates { get; } = new();

        public StubProviderModelCatalogSource ModelCatalog { get; } = new();

        public RecordingAdaptationService Adaptation { get; } = new();

        public WorkflowVersionItemViewModel Version { get; } = new(new WorkflowVersion(
            "ver-1",
            "pkg-1",
            1,
            "sha256:" + new string('a', 64),
            "sha256:" + new string('a', 64),
            WorkflowSourceType.ZipArchive,
            null,
            null,
            null,
            null,
            null,
            Now,
            null));

        public WorkflowPackageItemViewModel Package { get; } = new(new WorkflowPackage(
            "pkg-1",
            "Release Workflow",
            "Coordinates release tasks",
            new[] { "release" },
            WorkflowSourceType.ZipArchive,
            "sha256:" + new string('c', 64),
            "sha256:" + new string('c', 64),
            Now,
            Now));

        public Project Project { get; } = new(
            "project-1",
            "Project project-1",
            @"C:\work\project-1",
            null,
            isDirty: false,
            hasRequiredInstructions: true,
            defaultWorkflowId: null,
            defaultRoutePolicyId: null,
            DataClassification.PrivateSource);

        public void AddOpenCodeAccount(string accountId, string providerProfileId, string? providerNativeId)
        {
            AddAccount(accountId, providerProfileId, BackendType.OpenCode, providerNativeId);
        }

        public void AddAccount(
            string accountId,
            string providerProfileId,
            BackendType backend,
            string? providerNativeId)
        {
            if (!Profiles.Contains(providerProfileId))
            {
                Profiles.Add(new ProviderProfile(
                    providerProfileId,
                    $"Profile {providerProfileId}",
                    backend,
                    baseUrl: "http://127.0.0.1:11434/v1",
                    executablePath: null,
                    DataClassification.PrivateSource,
                    isEnabled: true));
            }

            Accounts.Add(new Account(
                accountId,
                providerProfileId,
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
    }

    private sealed class RecordingAdaptationService : IWorkflowAdaptationService
    {
        public List<AdaptationExecutionRequest> StartRequests { get; } = new();

        public Task<AdaptationPreSendPreview> PreparePreSendPreviewAsync(
            string workflowVersionId,
            string adapterRouteId,
            AdaptationGoal goal,
            IReadOnlyList<string>? userExcludedFiles = null,
            bool allowExpandedSemanticScope = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdaptationPreSendPreview(
                workflowVersionId,
                sourceVersionNumber: 1,
                "sha256:" + new string('a', 64),
                adapterRouteId,
                adapterRouteId,
                goal,
                WorkflowSecretScanReport.Empty,
                Array.Empty<string>(),
                Array.Empty<string>(),
                new SanitizedCapabilityCatalog(
                    Array.Empty<SanitizedProviderInfo>(),
                    Array.Empty<SanitizedModelInfo>(),
                    Now),
                AdaptationPreSendPreview.UnknownQuotaValue,
                AdaptationPreSendPreview.UnknownQuotaValue,
                reserveThreshold: null,
                promptPreview: "preview"));

        public Task<AdaptationCandidateResult> StartAdaptationAsync(
            AdaptationExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            StartRequests.Add(request);
            throw new InvalidOperationException("The recording service does not run a turn.");
        }

        public Task<AdaptationCandidateResult> SubmitFollowUpTurnAsync(
            AdaptationFollowUpRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SaveCandidateVersionResult> SaveCandidateVersionAsync(
            string sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DiscardSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public AdaptationSessionSnapshot GetSessionSnapshot(string sessionId) => throw new NotSupportedException();
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
                OutputText = """{"mappings":[]}""",
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
        public List<string> AbortedSessions { get; } = new();

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

        public Task<bool> AbortSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            AbortedSessions.Add(sessionId);
            return Task.FromResult(true);
        }

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

    private sealed class StubProviderProfileRepository : IProviderProfileRepository
    {
        private readonly Dictionary<string, ProviderProfile> _profiles = new(StringComparer.Ordinal);

        public bool Contains(string id) => _profiles.ContainsKey(id);

        public void Add(ProviderProfile profile) => _profiles[profile.Id] = profile;

        public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderProfile>>(_profiles.Values.ToArray());

        public Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profiles.TryGetValue(id, out var profile) ? profile : null);

        public Task UpsertAsync(
            ProviderProfile profile,
            string? apiKeySecretReference = null,
            CancellationToken cancellationToken = default)
        {
            Add(profile);
            return Task.CompletedTask;
        }

        public Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profiles.Remove(id));
    }

    private sealed class StubAccountRepository : IAccountRepository
    {
        private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);

        public void Add(Account account) => _accounts[account.Id] = account;

        public Task<Account?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_accounts.TryGetValue(id, out var account) ? account : null);

        public Task<IReadOnlyList<Account>> ListByProviderProfileIdAsync(
            string providerProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Account>>(_accounts.Values
                .Where(account => string.Equals(account.ProviderProfileId, providerProfileId, StringComparison.Ordinal))
                .ToArray());

        public Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Account>>(_accounts.Values.ToArray());

        public Task SaveAsync(Account account, CancellationToken cancellationToken = default)
        {
            Add(account);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            _accounts.Remove(id);
            return Task.CompletedTask;
        }

        public Task UpdateAuthStateAsync(
            string id,
            AuthState authState,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateCooldownAsync(
            string id,
            DateTimeOffset? cooldownUntil,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> GetSecretReferenceAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class StubHealthStateRepository : IHealthStateRepository
    {
        public Task UpsertAsync(HealthStateRecord healthState, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<HealthStateRecord?> GetAsync(
            string scopeType,
            string scopeId,
            CancellationToken cancellationToken = default) => Task.FromResult<HealthStateRecord?>(null);

        public Task<IReadOnlyList<HealthStateRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HealthStateRecord>>(Array.Empty<HealthStateRecord>());
    }

    /// <summary>
    /// Stands in for the local provider configuration: it hands out the model ids a profile is
    /// configured with and records the exact account/profile/backend relation it was asked about.
    /// </summary>
    private sealed class StubProviderModelCatalogSource : IProviderModelCatalogSource
    {
        public Dictionary<string, List<string>> ModelsByProfile { get; } = new(StringComparer.Ordinal);

        public List<ProviderModelCatalogQuery> Queries { get; } = new();

        public void Configure(string providerProfileId, params string[] modelIds) =>
            ModelsByProfile[providerProfileId] = modelIds.ToList();

        public Task<IReadOnlyList<ProviderModelDescriptor>> ListModelsAsync(
            ProviderModelCatalogQuery query,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);

            IReadOnlyList<ProviderModelDescriptor> models = ModelsByProfile.TryGetValue(
                query.ProviderProfileId,
                out var modelIds)
                    ? modelIds.Select(modelId => new ProviderModelDescriptor(modelId)).ToArray()
                    : Array.Empty<ProviderModelDescriptor>();

            return Task.FromResult(models);
        }
    }
}
