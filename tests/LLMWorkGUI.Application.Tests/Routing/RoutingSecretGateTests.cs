using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Tests.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Routing;

/// <summary>
/// An account whose credential cannot be resolved must stay out of routing, because a request sent
/// without it would fail at the provider instead of failing closed here (ADR-0005 §5.2).
/// </summary>
public sealed class RoutingSecretGateTests
{
    [Theory]
    [InlineData(RoutingPolicy.PriorityFirst, false)]
    [InlineData(RoutingPolicy.PriorityFirst, true)]
    [InlineData(RoutingPolicy.Pinned, false)]
    [InlineData(RoutingPolicy.Pinned, true)]
    [InlineData(RoutingPolicy.ManualOnly, false)]
    [InlineData(RoutingPolicy.ManualOnly, true)]
    [InlineData(RoutingPolicy.SessionSticky, false)]
    [InlineData(RoutingPolicy.SessionSticky, true)]
    public async Task ConfiguredCredentialWithoutLifecycleCannotAuthorizeAnyRoute(RoutingPolicy policy, bool accountKey)
    {
        _profileRepo.Save(CreateProfile());
        _profileRepo.SecretReference = accountKey ? null : ProfileReference;
        await _accountRepo.SaveAsync(CreateAccount("acc-1", accountKey ? AccountReference : null));
        var engine = new RoutingEngine(_accountRepo, _snapshotRepo, providerProfileRepository: _profileRepo);
        var decision = await engine.SelectRouteAsync(CreateRequest() with
        {
            Policy = policy, PinnedAccountId = "acc-1",
            ExistingStickyBinding = policy == RoutingPolicy.SessionSticky
                ? new SessionBinding(BackendType.OpenCode, ProviderProfileId, "acc-1", "gpt-4o", null, null, null) : null
        });
        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedBinding);
        var explanation = decision.ExplanationText + string.Join(" ", decision.RejectedCandidates.Select(candidate => candidate.Reason));
        Assert.Contains("secret lifecycle service is unavailable", explanation);
        Assert.DoesNotContain(ProfileReference, explanation);
        Assert.DoesNotContain(AccountReference, explanation);
    }
    private const string ProviderProfileId = "prov-1";
    private const string ProfileReference = "urn:llmworkgui:secret:profile-key";
    private const string AccountReference = "urn:llmworkgui:secret:account-key";

    private readonly InMemoryAccountRepository _accountRepo = new();
    private readonly InMemoryQuotaSnapshotRepository _snapshotRepo = new();
    private readonly InMemoryProviderProfileRepository _profileRepo = new();
    private readonly StubSecretLifecycle _secretLifecycle = new();

    [Fact]
    public async Task AutomaticRouting_RevokedProfileReference_RejectsTheAccount()
    {
        _profileRepo.Save(CreateProfile());
        _profileRepo.SecretReference = ProfileReference;
        _secretLifecycle.Status = SecretReferenceStatus.Revoked(ProfileReference, SecretReferenceKind.ProviderApiKey, true);
        await _accountRepo.SaveAsync(CreateAccount("acc-1", secretReference: null));

        var decision = await CreateEngine().SelectRouteAsync(CreateRequest());

        Assert.False(decision.IsSuccess);
        var rejection = Assert.Single(decision.RejectedCandidates);
        Assert.Contains(nameof(SecretReferenceState.Revoked), rejection.Reason);
    }

    [Fact]
    public async Task AutomaticRouting_MissingProfileReference_RejectsTheAccount()
    {
        _profileRepo.Save(CreateProfile());
        _profileRepo.SecretReference = ProfileReference;
        _secretLifecycle.Status = SecretReferenceStatus.Missing(ProfileReference, SecretReferenceKind.ProviderApiKey, true);
        await _accountRepo.SaveAsync(CreateAccount("acc-1", secretReference: null));

        var decision = await CreateEngine().SelectRouteAsync(CreateRequest());

        Assert.False(decision.IsSuccess);
        Assert.Contains(nameof(SecretReferenceState.Missing), Assert.Single(decision.RejectedCandidates).Reason);
    }

    [Fact]
    public async Task AutomaticRouting_RevokedAccountReference_RejectsTheAccount()
    {
        _profileRepo.Save(CreateProfile());
        _profileRepo.SecretReference = ProfileReference;
        _secretLifecycle.Status = SecretReferenceStatus.Active(ProfileReference, SecretReferenceKind.ProviderApiKey, true, DateTimeOffset.UtcNow, null);
        _secretLifecycle.StatusByReference[AccountReference] =
            SecretReferenceStatus.Revoked(AccountReference, SecretReferenceKind.ProviderApiKey, true);
        await _accountRepo.SaveAsync(CreateAccount("acc-1", secretReference: AccountReference));

        var decision = await CreateEngine().SelectRouteAsync(CreateRequest());

        // The account's own reference is the more specific one, so the profile reference being active
        // must not let the account into routing.
        Assert.False(decision.IsSuccess);
        Assert.Equal(AccountReference, _secretLifecycle.LastReference);
    }

    [Fact]
    public async Task PinnedRouting_RevokedReference_RefusesTheTargetAccount()
    {
        _profileRepo.Save(CreateProfile());
        _profileRepo.SecretReference = ProfileReference;
        _secretLifecycle.Status = SecretReferenceStatus.Revoked(ProfileReference, SecretReferenceKind.ProviderApiKey, true);
        await _accountRepo.SaveAsync(CreateAccount("acc-1", secretReference: null));

        var decision = await CreateEngine().SelectRouteAsync(CreateRequest() with
        {
            Policy = RoutingPolicy.Pinned,
            PolicySource = "ExplicitUser",
            PinnedAccountId = "acc-1"
        });

        Assert.False(decision.IsSuccess);
        Assert.Contains(nameof(SecretReferenceState.Revoked), decision.ExplanationText);
    }

    [Fact]
    public async Task SessionSticky_RevokedReference_RequiresAReplacementSession()
    {
        _profileRepo.Save(CreateProfile());
        _profileRepo.SecretReference = ProfileReference;
        _secretLifecycle.Status = SecretReferenceStatus.Revoked(ProfileReference, SecretReferenceKind.ProviderApiKey, true);
        await _accountRepo.SaveAsync(CreateAccount("acc-1", secretReference: null));

        var decision = await CreateEngine().SelectRouteAsync(CreateRequest() with
        {
            Policy = RoutingPolicy.SessionSticky,
            ExistingStickyBinding = new SessionBinding(
                BackendType.OpenCode,
                ProviderProfileId,
                "acc-1",
                "gpt-4o",
                null,
                null,
                null)
        });

        Assert.False(decision.IsSuccess);
        Assert.True(decision.RequiresReplacementSession);
    }

    [Fact]
    public async Task AutomaticRouting_ActiveReference_SelectsTheAccount()
    {
        _profileRepo.Save(CreateProfile());
        _profileRepo.SecretReference = ProfileReference;
        _secretLifecycle.Status = SecretReferenceStatus.Active(ProfileReference, SecretReferenceKind.ProviderApiKey, true, DateTimeOffset.UtcNow, null);
        await _accountRepo.SaveAsync(CreateAccount("acc-1", secretReference: null));

        var decision = await CreateEngine().SelectRouteAsync(CreateRequest());

        Assert.True(decision.IsSuccess);
        Assert.Equal("acc-1", decision.SelectedAccount!.Id);
    }

    [Fact]
    public async Task AutomaticRouting_KeylessProviderWithoutAnyReference_SelectsTheAccount()
    {
        // A provider that was never given a reference stays a keyless provider: there is nothing that
        // could have been revoked or lost.
        _profileRepo.Save(CreateProfile());
        _profileRepo.SecretReference = null;
        await _accountRepo.SaveAsync(CreateAccount("acc-1", secretReference: null));

        var decision = await CreateEngine().SelectRouteAsync(CreateRequest());

        Assert.True(decision.IsSuccess);
        Assert.Null(_secretLifecycle.LastReference);
    }

    [Fact]
    public async Task AutomaticRouting_ProfileReferenceIsResolvedOncePerSelection()
    {
        _profileRepo.Save(CreateProfile());
        _profileRepo.SecretReference = ProfileReference;
        _secretLifecycle.Status = SecretReferenceStatus.Active(ProfileReference, SecretReferenceKind.ProviderApiKey, true, DateTimeOffset.UtcNow, null);
        await _accountRepo.SaveAsync(CreateAccount("acc-1", secretReference: null));
        await _accountRepo.SaveAsync(CreateAccount("acc-2", secretReference: null));

        var decision = await CreateEngine().SelectRouteAsync(CreateRequest());

        Assert.True(decision.IsSuccess);
        Assert.Equal(2, _secretLifecycle.CallCount);
    }

    [Fact]
    public async Task AutomaticRouting_KeylessProviderWithoutSecretLifecycleRemainsEligible()
    {
        _profileRepo.Save(CreateProfile());
        _profileRepo.SecretReference = null;
        await _accountRepo.SaveAsync(CreateAccount("acc-1", secretReference: null));

        var engine = new RoutingEngine(
            _accountRepo,
            _snapshotRepo,
            timeProvider: TimeProvider.System,
            providerProfileRepository: _profileRepo);

        var decision = await engine.SelectRouteAsync(CreateRequest());

        Assert.True(decision.IsSuccess);
    }

    private RoutingEngine CreateEngine() =>
        new(
            _accountRepo,
            _snapshotRepo,
            timeProvider: TimeProvider.System,
            providerProfileRepository: _profileRepo,
            secretLifecycle: _secretLifecycle);

    private static RouteSelectionRequest CreateRequest() => new()
    {
        Backend = BackendType.OpenCode,
        ProviderProfileId = ProviderProfileId,
        ModelId = "gpt-4o",
        Policy = RoutingPolicy.PriorityFirst,
        PolicySource = "Test"
    };

    private static ProviderProfile CreateProfile() =>
        new(ProviderProfileId, "Provider", BackendType.OpenCode, "http://127.0.0.1:11434/v1", null, DataClassification.PrivateSource, true);

    private static Account CreateAccount(string id, string? secretReference) =>
        new(id, ProviderProfileId, id, null, AuthState.Valid, 0, true, HealthState.Healthy, null, null, 2, null, secretReference: secretReference);

    private sealed class StubSecretLifecycle : ISecretLifecycleService
    {
        public SecretReferenceStatus Status { get; set; } =
            SecretReferenceStatus.Active("urn:llmworkgui:secret:stub", SecretReferenceKind.ProviderApiKey, true, DateTimeOffset.UtcNow, null);

        public Dictionary<string, SecretReferenceStatus> StatusByReference { get; } = new(StringComparer.Ordinal);

        public string? LastReference { get; private set; }

        public int CallCount { get; private set; }

        public Task<SecretReferenceStatus> GetStatusAsync(string reference, CancellationToken cancellationToken = default)
        {
            LastReference = reference;
            CallCount++;

            return Task.FromResult(StatusByReference.TryGetValue(reference, out var status) ? status : Status);
        }

        public Task<SecretReferenceStatus> CreateAsync(string rawSecret, SecretReferenceKind kind = SecretReferenceKind.ProviderApiKey, SecretReferenceOwnerBinding? owner = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> RotateAsync(string reference, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> RevokeAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> BindAsync(string reference, SecretReferenceOwnerBinding owner, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> SaveProviderApiKeyAsync(ProviderProfile profile, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeProviderApiKeyAsync(string providerProfileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> SaveAccountSecretAsync(string accountId, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAccountSecretAsync(string accountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
