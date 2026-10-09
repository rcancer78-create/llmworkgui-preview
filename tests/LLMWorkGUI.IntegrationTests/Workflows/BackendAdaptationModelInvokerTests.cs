using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class BackendAdaptationModelInvokerTests : IDisposable
{

    // Synthetic policy for existing route/stream protocol tests only. Real SQL policy tests use the sealed store.
    internal sealed class FixtureProtocolPolicy : IAdaptationEgressPolicy
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
    private const string NativeModelId = "opencode/space-bunny-free";
    private const string OpenCodeProfileId = "prov-oc";
    private const string OpenCodeAccountId = "acct-oc";

    private readonly TestDirectory _directory = new();

    [Fact]
    public async Task RawAdaptationRequestWithoutStoredMaterialProvenanceCannotCreateOrSend()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            Session = new OpenCodeSessionResponse { Id = "owned-adaptation-session" },
            TurnResult = CreateTurnResult(outputText: "{\"mappings\":[]}")
        };
        var client = new FakeOpenCodeClient();
        var invoker = new AdaptationProtocolFixtureInvoker(lifecycle, client, profiles, accounts);
        // Synthetic protocol fixture: proves the invoker boundary, not native delivery or SQLite authority.
        await Assert.ThrowsAsync<WorkflowValidationException>(() => invoker.InvokeModelAsync(
            CreateRequest(routeId: new AdaptationRouteIdentity(OpenCodeAccountId, OpenCodeProfileId,
                BackendType.OpenCode, NativeModelId).RouteId)));
        Assert.Empty(lifecycle.CreateRequests); Assert.Empty(lifecycle.TurnRequests);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public async Task InvokeModelAsync_NullRequest_ThrowsArgumentNullException()
    {
        var invoker = CreateProtocolInvoker();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => invoker.InvokeModelAsync(null!));
    }

    [Fact]
    public async Task InvokeModelAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var invoker = CreateProtocolInvoker();
        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.InvokeModelAsync(CreateRequest(), cancellation.Token));
    }

    [Theory]
    [InlineData(BackendType.CursorAcp)]
    [InlineData(BackendType.Mirasim)]
    [InlineData(BackendType.StarCliProxy)]
    [InlineData(BackendType.Agy)]
    public async Task InvokeModelAsync_NonOpenCodeAccountRoute_NeverReachesOpenCode(BackendType backend)
    {
        var profiles = new StubProviderProfileRepository();
        profiles.Add(CreateProfile("prov-x", backend));
        var accounts = new StubAccountRepository();
        accounts.Add(CreateAccount("acct-x", "prov-x", NativeModelId));
        var lifecycle = new FakeOpenCodeSessionLifecycleService();
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        var routeId = new AdaptationRouteIdentity("acct-x", "prov-x", backend, NativeModelId).RouteId;

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: routeId)));

        Assert.Contains("only supported for OpenCode", exception.Message, StringComparison.Ordinal);
        Assert.Contains(backend.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.CreateRequests);
        Assert.Empty(client.CreateRequests);
        Assert.Empty(client.AbortedSessions);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("mirasim")]
    [InlineData("default")]
    [InlineData("opencode")]
    public async Task InvokeModelAsync_ReservedLiteralRouteIdThatIsNoAccount_ThrowsWorkflowValidationException(string routeId)
    {
        var profiles = new StubProviderProfileRepository();
        profiles.Add(CreateProfile(OpenCodeProfileId, BackendType.OpenCode));
        var accounts = new StubAccountRepository();
        accounts.Add(CreateAccount(OpenCodeAccountId, OpenCodeProfileId, NativeModelId));
        var lifecycle = new FakeOpenCodeSessionLifecycleService();
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: routeId)));

        Assert.Contains("could not be resolved", exception.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.CreateRequests);
        Assert.Empty(client.CreateRequests);
    }

    [Fact]
    public async Task InvokeModelAsync_MissingRepositories_ThrowsWorkflowValidationException()
    {
        var invoker = CreateProtocolInvoker(
            new FakeOpenCodeSessionLifecycleService(),
            new FakeOpenCodeClient());

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: OpenCodeAccountId)));

        Assert.Contains("repositories must both be configured", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeModelAsync_MissingOpenCodeServices_ThrowsWorkflowValidationException()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var invoker = CreateProtocolInvoker(
            openCodeLifecycle: null,
            openCodeClient: null,
            providerProfileRepository: profiles,
            accountRepository: accounts);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: OpenCodeAccountId)));

        Assert.Contains("must both be configured", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeModelAsync_RouteIdRoute_SendsBackendNativeModelIdToOpenCode()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            Session = new OpenCodeSessionResponse { Id = "session-abc" },
            TurnResult = CreateTurnResult(
                outputText: """{"mappings":[]}""",
                tokens: new OpenCodeTokenUsage(128, 64, 0, 10, 20))
        };
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        var routeId = new AdaptationRouteIdentity(
            OpenCodeAccountId,
            OpenCodeProfileId,
            BackendType.OpenCode,
            NativeModelId).RouteId;

        var response = await invoker.InvokeModelAsync(CreateRequest(
            routeId: routeId,
            systemPrompt: "System directive",
            messages: new[] { new AdaptationTurnMessage("user", "Adapt this workflow.") }));

        Assert.Equal("""{"mappings":[]}""", response.RawText);
        Assert.Equal(128, response.PromptTokens);
        Assert.Equal(64, response.CompletionTokens);

        var createRequest = Assert.Single(lifecycle.CreateRequests);

        Assert.Equal(NativeModelId, createRequest.Model);
        Assert.NotEqual(OpenCodeAccountId, createRequest.Model);
        Assert.StartsWith("workflow-adaptation-", createRequest.Title, StringComparison.Ordinal);

        var turn = Assert.Single(lifecycle.TurnRequests);

        Assert.Equal("session-abc", turn.SessionId);
        Assert.Equal(NativeModelId, turn.Request.Model);
        Assert.NotEqual(OpenCodeAccountId, turn.Request.Model);
        Assert.Contains("=== SYSTEM INSTRUCTION ===", turn.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains("System directive", turn.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Adapt this workflow.", turn.Request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[user]:", turn.Request.Prompt, StringComparison.Ordinal);

        Assert.Equal(new[] { "session-abc" }, client.AbortedSessions);
    }

    [Fact]
    public async Task InvokeModelAsync_RouteIdRoute_IgnoresModelIdSuppliedByTheCaller()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            TurnResult = CreateTurnResult()
        };
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        var routeId = new AdaptationRouteIdentity(
            OpenCodeAccountId,
            OpenCodeProfileId,
            BackendType.OpenCode,
            NativeModelId).RouteId;

        await invoker.InvokeModelAsync(CreateRequest(
            routeId: routeId,
            modelId: OpenCodeAccountId));

        Assert.Equal(NativeModelId, Assert.Single(lifecycle.CreateRequests).Model);
        Assert.Equal(NativeModelId, Assert.Single(lifecycle.TurnRequests).Request.Model);
    }

    [Fact]
    public async Task InvokeModelAsync_OpenCodeAccountWithoutNativeModelId_NeverSendsTheAccountId()
    {
        var profiles = new StubProviderProfileRepository();
        profiles.Add(CreateProfile(OpenCodeProfileId, BackendType.OpenCode));
        var accounts = new StubAccountRepository();
        accounts.Add(CreateAccount(OpenCodeAccountId, OpenCodeProfileId, providerNativeId: null));
        var lifecycle = new FakeOpenCodeSessionLifecycleService();
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(
                routeId: OpenCodeAccountId,
                modelId: OpenCodeAccountId)));

        Assert.Contains("no backend-native model id", exception.Message, StringComparison.Ordinal);
        Assert.Contains(OpenCodeAccountId, exception.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.CreateRequests);
        Assert.Empty(lifecycle.TurnRequests);
        Assert.Empty(client.CreateRequests);
        Assert.Empty(client.AbortedSessions);
    }

    [Fact]
    public async Task InvokeModelAsync_OpenCodeAccountIdReservedLiteral_StillReachesOpenCode()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories(
            accountId: "cursor",
            profileId: OpenCodeProfileId);
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            TurnResult = CreateTurnResult()
        };
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        var routeId = new AdaptationRouteIdentity(
            "cursor",
            OpenCodeProfileId,
            BackendType.OpenCode,
            NativeModelId).RouteId;

        var response = await invoker.InvokeModelAsync(CreateRequest(routeId: routeId));

        Assert.Equal("""{"mappings":[]}""", response.RawText);
        Assert.Equal(NativeModelId, Assert.Single(lifecycle.CreateRequests).Model);
        Assert.Equal(NativeModelId, Assert.Single(lifecycle.TurnRequests).Request.Model);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("opencode")]
    public async Task InvokeModelAsync_CursorAccountIdReservedLiteral_NeverReachesOpenCode(string accountId)
    {
        var profiles = new StubProviderProfileRepository();
        profiles.Add(CreateProfile("prov-cursor", BackendType.CursorAcp));
        var accounts = new StubAccountRepository();
        accounts.Add(CreateAccount(accountId, "prov-cursor", NativeModelId));
        var lifecycle = new FakeOpenCodeSessionLifecycleService();
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: accountId)));

        Assert.Contains("only supported for OpenCode", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CursorAcp", exception.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.CreateRequests);
        Assert.Empty(client.CreateRequests);
        Assert.Empty(client.AbortedSessions);
    }

    [Fact]
    public async Task InvokeModelAsync_AccountWithUnknownProviderProfile_ThrowsWorkflowValidationException()
    {
        var accounts = new StubAccountRepository();
        accounts.Add(CreateAccount(OpenCodeAccountId, "prov-missing", NativeModelId));
        var lifecycle = new FakeOpenCodeSessionLifecycleService();
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(
            lifecycle,
            client,
            new StubProviderProfileRepository(),
            accounts);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: OpenCodeAccountId)));

        Assert.Contains("is unknown", exception.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.CreateRequests);
    }

    [Fact]
    public async Task InvokeModelAsync_ResolvesOpenCodeBackendFromLegacyAccountModelRoute()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            TurnResult = CreateTurnResult()
        };
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        var response = await invoker.InvokeModelAsync(CreateRequest(
            routeId: $"{OpenCodeAccountId}:model-2",
            modelId: "model-2"));

        Assert.Equal("""{"mappings":[]}""", response.RawText);
        Assert.Equal(NativeModelId, Assert.Single(lifecycle.CreateRequests).Model);
        Assert.Equal(NativeModelId, Assert.Single(lifecycle.TurnRequests).Request.Model);
    }

    [Fact]
    public async Task InvokeModelAsync_MultipleMessages_FormatsRoleLabelledPrompt()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            TurnResult = CreateTurnResult()
        };
        var invoker = CreateProtocolInvoker(lifecycle, new FakeOpenCodeClient(), profiles, accounts);
        var messages = new[]
        {
            new AdaptationTurnMessage("user", "first request"),
            new AdaptationTurnMessage("assistant", "first reply"),
            new AdaptationTurnMessage("user", "second request")
        };

        await invoker.InvokeModelAsync(CreateRequest(
            routeId: OpenCodeAccountId,
            systemPrompt: string.Empty,
            messages: messages));

        var turn = Assert.Single(lifecycle.TurnRequests);

        Assert.DoesNotContain("=== SYSTEM INSTRUCTION ===", turn.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains("[user]:", turn.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains("[assistant]:", turn.Request.Prompt, StringComparison.Ordinal);
        Assert.True(
            turn.Request.Prompt.IndexOf("first reply", StringComparison.Ordinal)
                < turn.Request.Prompt.IndexOf("second request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeModelAsync_ClampsTokenCountsToIntMaxValue()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            TurnResult = CreateTurnResult(
                tokens: new OpenCodeTokenUsage(long.MaxValue, long.MaxValue, 0, 0, 0))
        };
        var invoker = CreateProtocolInvoker(lifecycle, new FakeOpenCodeClient(), profiles, accounts);

        var response = await invoker.InvokeModelAsync(CreateRequest(routeId: OpenCodeAccountId));

        Assert.Equal(int.MaxValue, response.PromptTokens);
        Assert.Equal(int.MaxValue, response.CompletionTokens);
    }

    [Fact]
    public async Task InvokeModelAsync_WithoutTokenUsage_ReturnsNullTokenCounts()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            TurnResult = CreateTurnResult()
        };
        var invoker = CreateProtocolInvoker(lifecycle, new FakeOpenCodeClient(), profiles, accounts);

        var response = await invoker.InvokeModelAsync(CreateRequest(routeId: OpenCodeAccountId));

        Assert.Null(response.PromptTokens);
        Assert.Null(response.CompletionTokens);
    }

    [Fact]
    public async Task InvokeModelAsync_OpenCodeExecutionFailed_ThrowsWorkflowValidationException()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            TurnResult = CreateTurnResult(
                outputText: string.Empty,
                status: TurnResult.FailedStatus,
                errorMessage: "backend exploded")
        };
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: OpenCodeAccountId)));

        Assert.Contains("backend exploded", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "session-abc" }, client.AbortedSessions);
    }

    [Fact]
    public async Task InvokeModelAsync_OpenCodeExecutionCancelled_ThrowsOperationCanceledException()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            TurnResult = CreateTurnResult(status: TurnResult.CancelledStatus)
        };
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: OpenCodeAccountId)));

        Assert.Equal(new[] { "session-abc" }, client.AbortedSessions);
    }

    [Fact]
    public async Task InvokeModelAsync_CancelledDuringTurn_ThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();

        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService
        {
            TurnHandler = (_, _) =>
            {
                cancellation.Cancel();
                return CreateTurnResult();
            }
        };
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: OpenCodeAccountId), cancellation.Token));

        Assert.Equal(new[] { "session-abc" }, client.AbortedSessions);
    }

    [Fact]
    public async Task InvokeModelAsync_ClientWithoutLifecycle_ThrowsWorkflowValidationException()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(
            openCodeLifecycle: null,
            openCodeClient: client,
            providerProfileRepository: profiles,
            accountRepository: accounts);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: OpenCodeAccountId)));

        Assert.Contains("must both be configured", exception.Message, StringComparison.Ordinal);
        Assert.Empty(client.CreateRequests);
        Assert.Empty(client.AbortedSessions);
    }

    [Fact]
    public async Task InvokeModelAsync_LifecycleWithoutClient_ThrowsWorkflowValidationException()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService();
        var invoker = CreateProtocolInvoker(
            openCodeLifecycle: lifecycle,
            openCodeClient: null,
            providerProfileRepository: profiles,
            accountRepository: accounts);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: OpenCodeAccountId)));

        Assert.Contains("must both be configured", exception.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.CreateRequests);
    }

    [Fact]
    public async Task InvokeModelAsync_UnknownRoute_ThrowsWorkflowValidationException()
    {
        var (profiles, accounts) = CreateOpenCodeRepositories();
        var lifecycle = new FakeOpenCodeSessionLifecycleService();
        var client = new FakeOpenCodeClient();
        var invoker = CreateProtocolInvoker(lifecycle, client, profiles, accounts);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => invoker.InvokeModelAsync(CreateRequest(routeId: "unknown-route-123")));

        Assert.Contains("could not be resolved", exception.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.CreateRequests);
        Assert.Empty(client.CreateRequests);
        Assert.Empty(client.AbortedSessions);
    }

    [Fact]
    public void ProductionDi_ContainsNoMockImplementations_AndResolvesBackendAdaptationModelInvoker()
    {
        IServiceCollection? capturedServices = null;

        using var host = HostBootstrapper
            .CreateHostBuilder(appDataDirectory: _directory.Root)
            .ConfigureServices((_, services) => capturedServices = services)
            .Build();

        var descriptors = capturedServices
            ?? throw new InvalidOperationException("The host builder did not expose its service collection.");

        var mockImplementationTypes = descriptors
            .Where(descriptor => descriptor.ImplementationType is not null
                && descriptor.ImplementationType.Name.StartsWith("Mock", StringComparison.OrdinalIgnoreCase))
            .Select(descriptor => descriptor.ImplementationType!.FullName)
            .ToArray();

        Assert.Empty(mockImplementationTypes);

        var invoker = host.Services.GetRequiredService<IAdaptationModelInvoker>();

        Assert.IsType<BackendAdaptationModelInvoker>(invoker);
    }

    private static (StubProviderProfileRepository Profiles, StubAccountRepository Accounts)
        CreateOpenCodeRepositories(
            string accountId = OpenCodeAccountId,
            string profileId = OpenCodeProfileId)
    {
        var profiles = new StubProviderProfileRepository();
        profiles.Add(CreateProfile(profileId, BackendType.OpenCode));
        var accounts = new StubAccountRepository();
        accounts.Add(CreateAccount(accountId, profileId, NativeModelId));

        return (profiles, accounts);
    }

    private static AdaptationModelRequest CreateRequest(
        string routeId = OpenCodeAccountId,
        string modelId = "model-1",
        string systemPrompt = "You are the workflow adaptation assistant.",
        IReadOnlyList<AdaptationTurnMessage>? messages = null)
    {
        return new AdaptationModelRequest(
            routeId,
            modelId,
            systemPrompt,
            messages ?? new[] { new AdaptationTurnMessage("user", "Adapt the workflow.") });
    }

    private static TurnResult CreateTurnResult(
        string outputText = """{"mappings":[]}""",
        string status = TurnResult.CompletedStatus,
        OpenCodeTokenUsage? tokens = null,
        string? errorMessage = null)
    {
        return new TurnResult
        {
            SessionId = "session-abc",
            OutputText = outputText,
            Status = status,
            Tokens = tokens,
            ErrorMessage = errorMessage
        };
    }

    private static ProviderProfile CreateProfile(string id, BackendType backend, bool isEnabled = true)
    {
        return new ProviderProfile(
            id,
            $"Profile {id}",
            backend,
            baseUrl: null,
            executablePath: null,
            DataClassification.PrivateSource,
            isEnabled);
    }

    private static Account CreateAccount(string id, string providerProfileId, string? providerNativeId)
    {
        return new Account(
            id,
            providerProfileId,
            $"Account {id}",
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
            secretReference: null);
    }

    private sealed class StubProviderProfileRepository : IProviderProfileRepository
    {
        private readonly Dictionary<string, ProviderProfile> _profiles = new(StringComparer.Ordinal);

        public void Add(ProviderProfile profile)
        {
            _profiles[profile.Id] = profile;
        }

        public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ProviderProfile>>(_profiles.Values.ToArray());
        }

        public Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_profiles.TryGetValue(id, out var profile) ? profile : null);
        }

        public Task UpsertAsync(
            ProviderProfile profile,
            string? apiKeySecretReference = null,
            CancellationToken cancellationToken = default)
        {
            Add(profile);
            return Task.CompletedTask;
        }

        public Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_profiles.Remove(id));
        }
    }

    private sealed class StubAccountRepository : IAccountRepository
    {
        private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);

        public void Add(Account account)
        {
            _accounts[account.Id] = account;
        }

        public Task<Account?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_accounts.TryGetValue(id, out var account) ? account : null);
        }

        public Task<IReadOnlyList<Account>> ListByProviderProfileIdAsync(
            string providerProfileId,
            CancellationToken cancellationToken = default)
        {
            var accounts = _accounts.Values
                .Where(account => string.Equals(account.ProviderProfileId, providerProfileId, StringComparison.Ordinal))
                .ToArray();

            return Task.FromResult<IReadOnlyList<Account>>(accounts);
        }

        public Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Account>>(_accounts.Values.ToArray());
        }

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
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpdateCooldownAsync(
            string id,
            DateTimeOffset? cooldownUntil,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretReferenceAsync(string accountId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeOpenCodeSessionLifecycleService : IOpenCodeSessionLifecycleService
    {
        public List<OpenCodeCreateSessionRequest> CreateRequests { get; } = new();

        public List<(string SessionId, OpenCodePromptRequest Request)> TurnRequests { get; } = new();

        public OpenCodeSessionResponse Session { get; set; } = new() { Id = "session-abc" };

        public TurnResult? TurnResult { get; set; }

        public Func<string, OpenCodePromptRequest, TurnResult>? TurnHandler { get; set; }
        public Func<string, OpenCodePromptRequest, CancellationToken, Task<TurnResult>>? AsyncTurnHandler { get; set; }
        public Func<OpenCodeCreateSessionRequest, CancellationToken, Task<OpenCodeSessionResponse>>? CreateHandler { get; set; }
        public Func<Task>? AfterCreate { get; set; }

        public async Task<OpenCodeSessionResponse> CreateAndConfirmSessionAsync(
            OpenCodeCreateSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateRequests.Add(request);
            if (CreateHandler is not null) Session = await CreateHandler(request,cancellationToken);
            if (AfterCreate is not null) await AfterCreate();
            return Session;
        }

        public Task<OpenCodeSessionResponse> ContinueSessionAsync(
            string sessionId,
            SessionBinding binding,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<TurnResult> ExecuteTurnAsync(
            string sessionId,
            OpenCodePromptRequest request,
            CancellationToken cancellationToken = default)
        {
            TurnRequests.Add((sessionId, request));
            if (AsyncTurnHandler is not null) return AsyncTurnHandler(sessionId,request,cancellationToken);

            var result = TurnHandler?.Invoke(sessionId, request)
                ?? TurnResult
                ?? throw new InvalidOperationException("The fake lifecycle service has no turn result configured.");

            return Task.FromResult(result);
        }

        public Task<bool> CancelTurnAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<OpenCodeSessionResponse> ResetSessionAsync(
            string oldSessionId,
            OpenCodeCreateSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<string> GetAncestry(string sessionId)
        {
            return Array.Empty<string>();
        }
    }

    private sealed class FakeOpenCodeClient : IOpenCodeClient
    {
        public List<OpenCodeCreateSessionRequest> CreateRequests { get; } = new();

        public List<(string SessionId, OpenCodePromptRequest Request)> SentPrompts { get; } = new();

        public List<string> AbortedSessions { get; } = new();

        public OpenCodeSessionResponse Session { get; set; } = new() { Id = "session-direct" };

        public bool SendPromptResult { get; set; } = true;

        public Uri BaseUrl { get; } = new("http://127.0.0.1:4096/");

        public Task<bool> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<OpenCodeDocResponse> GetDocAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<OpenCodeProviderInfo>> ListProvidersAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<OpenCodeProviderInfo>>(Array.Empty<OpenCodeProviderInfo>());
        }

        public Task<IReadOnlyList<OpenCodeModelInfo>> ListModelsAsync(
            string? providerId = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<OpenCodeModelInfo>>(Array.Empty<OpenCodeModelInfo>());
        }

        public Task<OpenCodeConfiguredProvidersResponse> ListConfiguredProvidersAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<OpenCodeSessionResponse> CreateSessionAsync(
            OpenCodeCreateSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateRequests.Add(request);
            return Task.FromResult(Session);
        }

        public Task<OpenCodeSessionResponse?> GetSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OpenCodeSessionResponse?>(Session);
        }

        public Task<IReadOnlyList<OpenCodeSessionResponse>> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<OpenCodeSessionResponse>>(Array.Empty<OpenCodeSessionResponse>());
        }

        public Task<OpenCodeSessionResponse> ForkSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> AbortSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            AbortedSessions.Add(sessionId);
            return Task.FromResult(true);
        }

        public Task<bool> SendPromptAsync(
            string sessionId,
            OpenCodePromptRequest request,
            CancellationToken cancellationToken = default)
        {
            SentPrompts.Add((sessionId, request));
            return Task.FromResult(SendPromptResult);
        }

        public Task<bool> ReplyPermissionAsync(
            string permissionId,
            OpenCodePermissionReply reply,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeEventsAsync(
            string? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
