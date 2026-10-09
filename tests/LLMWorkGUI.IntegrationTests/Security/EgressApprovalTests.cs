using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Workflows;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Security;

/// <summary>Approval authority, not a native model call or response-origin proof.</summary>
public sealed class EgressApprovalTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly Clock _clock = new();
    private EgressTarget _target = null!;
    private ApplicationInstanceGuard? _guard;
    private readonly EgressFragmentInput[] _fragments =
    [new("instructions", "Инструкция", "Review the provided text only.", DataClassification.PublicSource),
        new("prompt", "Задание", "api_key=synthetic-egress-canary\nReview this method.", DataClassification.Restricted)];

    public void Dispose() { _guard?.Dispose(); _db.Dispose(); }
    private EgressApprovalService Service(IProjectRepository? projects = null, IApplicationInstanceGuard? guard = null) => new(projects ?? new SqliteProjectRepository(_db.Factory),
        new SqliteRouteRepository(_db.Factory), new SqliteProviderProfileRepository(_db.Factory), new WorkflowSecretScanner(), _clock, guard ?? _guard!);

    private async Task<EgressApprovalService> Ready()
    {
        await _db.InitializeAsync();
        _guard = new ApplicationInstanceGuard(_db.Root);
        await _db.SeedRouteChainAsync(providerProfileId: GrokBotRestrictions.ProviderProfileId);
        await Sql("UPDATE ProviderProfiles SET Backend='NativeGateway',MaxDataClass='Restricted'; UPDATE Models SET Backend='NativeGateway'; UPDATE Routes SET Backend='NativeGateway',MaxDataClass='Restricted'; UPDATE Projects SET DataClassification='Restricted'");
        _target = new("project-1", _db.GetWorkspacePath(), "route-1",
            new SessionBinding(BackendType.NativeGateway, GrokBotRestrictions.ProviderProfileId, "account-1", "model-1", null, null, null), RoutingPolicy.ManualOnly);
        return Service();
    }

    private async Task Sql(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void Approve(EgressApprovalService service, EgressPreview preview)
    {
        foreach (var f in preview.Fragments) service.ApproveFragment(preview.Id, f.Id, f.ContentSha256);
    }

    [Fact]
    public async Task EveryFragmentIsRequired_OnlySanitizedContentIsReturned_ConsentIsSpentOnce()
    {
        var service = await Ready();
        var preview = await service.PrepareAsync(_target, _fragments);
        Assert.DoesNotContain("synthetic-egress-canary", string.Join("\n", preview.Fragments.Select(f => f.Content)));
        Assert.Contains("[REDACTED]", preview.Fragments[1].Content);
        Assert.Equal(_target, preview.Target);
        service.ApproveFragment(preview.Id, preview.Fragments[0].Id, preview.Fragments[0].ContentSha256);
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
        // The failed attempt invalidates even partially granted consent.
        Assert.Throws<EgressApprovalException>(() => service.ApproveFragment(preview.Id, "prompt", preview.Fragments[1].ContentSha256));
        preview = await service.PrepareAsync(_target, _fragments);
        Approve(service, preview);
        var payload = await service.ConsumeAsync(preview.Id, _target, _fragments);
        Assert.Equal(DataClassification.Restricted, payload.Classification);
        Assert.Equal(preview.PayloadSha256, payload.PayloadSha256);
        Assert.Equal(preview.Fragments.Select(f => f.Content), payload.Fragments.Select(f => f.Content));
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
        Assert.Throws<NotSupportedException>(() => ((IList<EgressPreviewFragment>)payload.Fragments).Clear());
    }

    [Theory]
    [InlineData("content")]
    [InlineData("same-sanitized-secret")]
    [InlineData("order")]
    [InlineData("label")]
    [InlineData("classification")]
    [InlineData("model")]
    [InlineData("account")]
    [InlineData("root")]
    [InlineData("project")]
    [InlineData("route")]
    [InlineData("mode")]
    public async Task ChangedPayloadOrDestinationCannotReuseConsent(string change)
    {
        var service = await Ready(); var preview = await service.PrepareAsync(_target, _fragments); Approve(service, preview);
        var target = _target; var fragments = _fragments.ToArray();
        switch (change)
        {
            case "content": fragments[1] = fragments[1] with { Content = "Different material" }; break;
            case "same-sanitized-secret": fragments[1] = fragments[1] with { Content = fragments[1].Content.Replace("synthetic-egress-canary", "different-secret") }; break;
            case "order": Array.Reverse(fragments); break;
            case "label": fragments[1] = fragments[1] with { Label = "Different fragment" }; break;
            case "classification": fragments[1] = fragments[1] with { Classification = DataClassification.PublicSource }; break;
            case "model": target = target with { Binding = new(target.Binding.Backend, target.Binding.ProviderProfileId, "account-1", "different", null, null, null) }; break;
            case "account": target = target with { Binding = new(target.Binding.Backend, target.Binding.ProviderProfileId, "different", "model-1", null, null, null) }; break;
            case "root": target = target with { RootPath = _db.GetWorkspacePath("other") }; break;
            case "project": target = target with { ProjectId = "other" }; break;
            case "route": target = target with { RouteId = "other" }; break;
            case "mode": target = target with { Policy = RoutingPolicy.Pinned }; break;
        }
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, target, fragments));
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
    }

    [Theory]
    [InlineData("UPDATE Projects SET DataClassification='PrivateSource'")]
    [InlineData("UPDATE Projects SET RootPath='D:\\different-egress-project'")]
    [InlineData("UPDATE Routes SET MaxDataClass='PrivateSource'")]
    [InlineData("UPDATE Routes SET IsEnabled=0")]
    [InlineData("UPDATE ProviderProfiles SET IsEnabled=0")]
    [InlineData("UPDATE ProviderProfiles SET Revision=Revision+1")]
    [InlineData("UPDATE ProviderProfiles SET MaxDataClass='PrivateSource'")]
    public async Task AuthoritativePolicyChangesInvalidatePreviouslyApprovedFragments(string sql)
    {
        var service = await Ready(); var preview = await service.PrepareAsync(_target, _fragments); Approve(service, preview);
        await Sql(sql);
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
    }

    [Fact]
    public async Task ExpiredRevokedRestartedAndForgedConsentCannotBeUsed()
    {
        var service = await Ready(); var preview = await service.PrepareAsync(_target, _fragments); Approve(service, preview);
        await Assert.ThrowsAsync<EgressApprovalException>(() => Service().ConsumeAsync(preview.Id, _target, _fragments));
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(Guid.NewGuid(), _target, _fragments));
        _clock.Now += EgressApprovalService.PreviewLifetime;
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
        preview = await service.PrepareAsync(_target, _fragments); Approve(service, preview); service.Revoke(preview.Id);
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
    }

    [Fact]
    public async Task ConcurrentConsumptionAndRevocationDuringPolicyReadCannotReleaseTwoPayloads()
    {
        await Ready();
        var repository = new BarrierProjects(new SqliteProjectRepository(_db.Factory)); var service = Service(repository);
        var preview = await service.PrepareAsync(_target, _fragments); Approve(service, preview);
        var first = service.ConsumeAsync(preview.Id, _target, _fragments);
        await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
            service.Revoke(preview.Id);
        }
        finally { repository.Release.TrySetResult(); }
        await Assert.ThrowsAsync<EgressApprovalException>(() => first);
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
    }

    [Theory]
    [InlineData(RoutingPolicy.Balanced)]
    [InlineData(RoutingPolicy.Pinned)]
    [InlineData(RoutingPolicy.SessionSticky)]
    public async Task RestrictedCannotBeApprovedForAutomaticOrPinnedPolicies(RoutingPolicy policy)
    {
        var service = await Ready();
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.PrepareAsync(_target with { Policy = policy }, _fragments));
    }

    [Fact]
    public async Task RepositoryReadingCliCannotClaimFragmentOnlyRestrictedTransport()
    {
        var service = await Ready();
        await Sql("UPDATE ProviderProfiles SET Backend='OpenCode'; UPDATE Models SET Backend='OpenCode'; UPDATE Routes SET Backend='OpenCode'");
        var target = _target with { Binding = new(BackendType.OpenCode, GrokBotRestrictions.ProviderProfileId, "account-1", "model-1", null, null, null) };
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.PrepareAsync(target, _fragments));
    }

    [Fact]
    public void CompositionResolvesSharedAuthorityWithoutNativeGatewayConstruction()
    {
        var services = new ServiceCollection().AddInfrastructure(_db.Root);
        services.AddSingleton<LLMGateway.Core.ILlmGateway>(_ => throw new Exception("Native gateway must remain lazy"));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<EgressApprovalService>(provider.GetRequiredService<IEgressApprovalService>());
        Assert.False(Directory.Exists(Path.Combine(_db.Root, "llmgateway")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrExpiryDuringPolicyReadInvalidatesTheReservedConsent(bool expire)
    {
        await Ready();
        var repository = new BarrierProjects(new SqliteProjectRepository(_db.Factory)); var service = Service(repository);
        var preview = await service.PrepareAsync(_target, _fragments); Approve(service, preview);
        using var cancellation = new CancellationTokenSource();
        var consume = service.ConsumeAsync(preview.Id, _target, _fragments, cancellation.Token);
        await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (expire) _clock.Now += EgressApprovalService.PreviewLifetime; else cancellation.Cancel();
        repository.Release.TrySetResult();
        if (expire) await Assert.ThrowsAsync<EgressApprovalException>(() => consume);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consume);
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
    }

    [Fact]
    public async Task SecondaryInstanceCannotPrepareApproveOrConsumePrimaryConsent()
    {
        var primary = await Ready(); var preview = await primary.PrepareAsync(_target, _fragments); Approve(primary, preview);
        using var secondary = new ApplicationInstanceGuard(_db.Root); Assert.True(secondary.IsViewOnly);
        var service = Service(guard: secondary);
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => service.PrepareAsync(_target, _fragments));
        Assert.Throws<SecondaryInstanceReadOnlyException>(() => service.ApproveFragment(preview.Id, "prompt", preview.Fragments[1].ContentSha256));
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
        await primary.ConsumeAsync(preview.Id, _target, _fragments);
    }

    [Fact]
    public async Task IncorrectDisplayedHashAndUnboundedOrInvalidInputCannotMintApproval()
    {
        var service = await Ready(); var preview = await service.PrepareAsync(_target, _fragments);
        Assert.Throws<EgressApprovalException>(() => service.ApproveFragment(preview.Id, "prompt", new string('0', 64)));
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.PrepareAsync(_target, [new("../escape", "label", "text", DataClassification.PublicSource)]));
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.PrepareAsync(_target, [new("id", "label", new string('x', 200_001), DataClassification.PublicSource)]));
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.PrepareAsync(_target, [new("id", "label", "\uD800", DataClassification.PublicSource)]));
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.PrepareAsync(_target, [new("id", "label", "text", (DataClassification)99)]));
    }

    [Theory]
    [InlineData("UPDATE ProviderProfiles SET BaseUrl='https://synthetic.invalid'")]
    [InlineData("UPDATE ProviderProfiles SET ExecutablePath='D:\\synthetic-provider.exe'")]
    [InlineData("UPDATE ProviderProfiles SET GatewayNativeId='synthetic-provider'")]
    public async Task DirectTransportMetadataChangeWithoutRevisionInvalidatesConsent(string sql)
    {
        var service = await Ready(); var preview = await service.PrepareAsync(_target, _fragments); Approve(service, preview);
        await Sql(sql);
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
    }

    [Theory]
    [InlineData("relative-root")]
    [InlineData("D:relative-root")]
    [InlineData("D:\\invalid\0root")]
    public async Task InvalidTargetRootRefusesWithContentFreeApprovalError(string root)
    {
        var service = await Ready();
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.PrepareAsync(_target with { RootPath = root }, _fragments));
    }

    [Fact]
    public async Task InvalidCurrentFragmentAttemptAlsoConsumesConsent()
    {
        var service = await Ready(); var preview = await service.PrepareAsync(_target, _fragments); Approve(service, preview);
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target,
            [new("../invalid", "label", "text", DataClassification.Restricted)]));
        await Assert.ThrowsAsync<EgressApprovalException>(() => service.ConsumeAsync(preview.Id, _target, _fragments));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BarrierProjects(IProjectRepository inner) : IProjectRepository
    {
        private int _reads;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Project?> GetByIdAsync(string id, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref _reads) > 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return await inner.GetByIdAsync(id, token);
        }
        public Task UpsertAsync(Project project, CancellationToken cancellationToken = default) => inner.UpsertAsync(project, cancellationToken);
        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken cancellationToken = default) => inner.GetByRootPathAsync(rootPath, cancellationToken);
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
    }
}
