using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using LLMWorkGUI.Infrastructure.Workflows.Channels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowReviewProvenanceTests
{
    private const string EgressPrompt = "synthetic review text bound to an owned admission";
    [Theory]
    [InlineData(DataClassification.PublicSource)]
    [InlineData(DataClassification.PrivateSource)]
    public async Task RealWorkflowHttpConsumesStoredPromptOnceAndAuditsActualNativeModel(DataClassification classification)
    {
        using var guard = new ApplicationInstanceGuard(_database.Root);
        using var provider = CreateProvider(); await PrepareHttpRun(provider, classification);
        var request = await AdmitHttpPrompt(provider);
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        var policy = new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System);
        Assert.Equal("native-model-1", await policy.ValidateAsync(new(request.ExecutionId, RealRouteId, StageId, ReviewerRole),
            "http://127.0.0.1:4971/", EgressPrompt, null, null, CancellationToken.None));
        var channel = HttpChannel(http, policy);
        var result = await channel.RunAsync(request);
        Assert.Equal(ReviewChannelTurnOutcome.IdentityNotProvable, result.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, result.State); Assert.Null(result.ObservedRouteId); Assert.False(channel.SupportsRoute(RealRouteId));
        Assert.Single(handler.Bodies);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("native-model-1", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(EgressPrompt, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.DoesNotContain("WorkflowReviewContext", handler.Bodies[0]);
        Assert.Equal("Running", await EgressSql("SELECT State FROM Executions WHERE Id='http-review-execution'"));
        Assert.Null(await EgressSql("SELECT ObservedRouteId FROM Executions WHERE Id='http-review-execution'"));
        var audit = (string)(await EgressSql("SELECT NormalizedRedactedPayloadJson FROM ExecutionEvents WHERE EventKind='WorkflowReviewTransportAuthorized'"))!;
        Assert.Contains(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(handler.Bodies[0]))), audit);
        Assert.DoesNotContain(EgressPrompt, audit); Assert.DoesNotContain(_database.Root, audit); Assert.DoesNotContain("owned-key", audit);
        using var metadata = JsonDocument.Parse(audit); Assert.False(metadata.RootElement.GetProperty("nativeIdentityConfirmed").GetBoolean());
        Assert.Equal(ReviewChannelTurnOutcome.EgressPolicyRefused, (await channel.RunAsync(request)).Outcome);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("max")]
    [InlineData("route")]
    [InlineData("account")]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("fingerprint")]
    [InlineData("artifact")]
    [InlineData("root")]
    [InlineData("backend")]
    [InlineData("health")]
    [InlineData("route-max")]
    public async Task RealClientRechecksPolicyAfterChannelValidationBeforeHttp(string change)
    {
        using var guard = new ApplicationInstanceGuard(_database.Root);
        using var provider = CreateProvider(); await PrepareHttpRun(provider); var request = await AdmitHttpPrompt(provider);
        var real = new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System);
        var originalArtifact = await CurrentArtifactAsync();
        var barrier = new AfterValidationPolicy(real, async () =>
        {
            if (change == "artifact")
            {
                using var content = new MemoryStream(Encoding.UTF8.GetBytes("replacement artifact after successful preflight"));
                await provider.GetRequiredService<IWorkflowRunService>().RecordStageArtifactAsync(
                    RunId, StageId, ArtifactKind, content, DataClassification.PrivateSource);
                Assert.NotEqual(originalArtifact.ArtifactId, (await CurrentArtifactAsync()).ArtifactId);
                return;
            }
            await EgressSql(change switch
        {
            "class" => "UPDATE Projects SET DataClassification='Restricted'",
            "max" => "UPDATE ProviderProfiles SET MaxDataClass='PublicSource'",
            "route" => "UPDATE Routes SET IsEnabled=0",
            "account" => "UPDATE Accounts SET AuthState='Unknown'",
            "model" => "UPDATE Models SET CapabilityState='Unknown'",
            "endpoint" => "UPDATE ProviderProfiles SET BaseUrl='http://127.0.0.1:4972/'",
            "fingerprint" => "UPDATE ProviderProfiles SET DisplayName='Changed while awaiting'",
            "root" => "UPDATE Projects SET RootPath='D:/owned-other-root'",
            "backend" => "UPDATE ProviderProfiles SET Backend='OpenCode'",
            "health" => "UPDATE Accounts SET Health='Quarantined'",
            "route-max" => "UPDATE Routes SET MaxDataClass='PublicSource'",
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        });
        });
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        var channel = HttpChannel(http, real, barrier);
        Assert.Equal(ReviewChannelTurnOutcome.EgressPolicyRefused, (await channel.RunAsync(request)).Outcome);
        Assert.True(barrier.Validated);
        Assert.True(barrier.Changed);
        Assert.Empty(handler.Bodies);
        Assert.Equal("Queued", await EgressSql("SELECT State FROM Executions WHERE Id='http-review-execution'"));
        Assert.Equal(0L, await EgressSql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='WorkflowReviewTransportAuthorized'"));
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("stage")]
    [InlineData("role")]
    [InlineData("execution")]
    [InlineData("session")]
    [InlineData("route")]
    public async Task CallerCannotSubstituteStoredReviewContextOrPrompt(string change)
    {
        using var guard = new ApplicationInstanceGuard(_database.Root);
        using var provider = CreateProvider(); await PrepareHttpRun(provider); var original = await AdmitHttpPrompt(provider);
        var request = new WorkflowChannelTurnRequest(change == "execution" ? "foreign" : original.ExecutionId,
            new WorkflowNodeDefinition(change == "stage" ? "foreign" : StageId, WorkflowNodeKind.Review, "Review", ReviewerRole),
            new RoleBindingDefinition(change == "role" ? "foreign" : ReviewerRole, RealRouteId, modelId: ModelId),
            change == "route" ? OtherRouteId : RealRouteId, true,
            change == "prompt" ? EgressPrompt + " unadmitted" : EgressPrompt, change == "session" ? "foreign-native" : null);
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        Assert.Equal(ReviewChannelTurnOutcome.EgressPolicyRefused, (await HttpChannel(http,
            new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System)).RunAsync(request)).Outcome);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task TransportAuditFailureRollsBackConsumptionBeforeHttp()
    {
        using var guard = new ApplicationInstanceGuard(_database.Root);
        using var provider = CreateProvider(); await PrepareHttpRun(provider); var request = await AdmitHttpPrompt(provider);
        await EgressSql("CREATE TRIGGER refuse_review_audit BEFORE INSERT ON ExecutionEvents WHEN NEW.EventKind='WorkflowReviewTransportAuthorized' BEGIN SELECT RAISE(ABORT,'owned audit failure'); END");
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        Assert.Equal(ReviewChannelTurnOutcome.EgressPolicyRefused, (await HttpChannel(http,
            new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System)).RunAsync(request)).Outcome);
        Assert.Empty(handler.Bodies);
        Assert.Equal("Queued", await EgressSql("SELECT State FROM Executions WHERE Id='http-review-execution'"));
        Assert.Equal("Queued", await EgressSql("SELECT e.State FROM WorkflowReviewExecutions b JOIN Executions e ON e.Id=b.ExecutionId WHERE b.ExecutionId='http-review-execution'"));
    }

    [Fact]
    public async Task ActualClientWithoutPolicyCannotUseValidatedChannelContext()
    {
        using var guard = new ApplicationInstanceGuard(_database.Root);
        using var provider = CreateProvider(); await PrepareHttpRun(provider); var request = await AdmitHttpPrompt(provider);
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        var channel = new StarCliProxyReviewReadOnlyChannel(new SqliteRouteRepository(_database.Factory), new FixtureHttpLocator(),
            new StarCliProxyClient(http), egressPolicy: new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System));
        Assert.Equal(ReviewChannelTurnOutcome.EgressPolicyRefused, (await channel.RunAsync(request)).Outcome);
        Assert.Empty(handler.Bodies); Assert.Equal("Queued", await EgressSql("SELECT State FROM Executions"));
    }

    [Fact]
    public async Task RelativeStoredProjectRootDoesNotBecomeAuthorityByResolvingAgainstProcessDirectory()
    {
        using var guard = new ApplicationInstanceGuard(_database.Root);
        using var provider = CreateProvider(); await PrepareHttpRun(provider);
        var driveRelative = Path.GetPathRoot(Environment.CurrentDirectory)!.TrimEnd(Path.DirectorySeparatorChar) + ".";
        Assert.True(Path.IsPathRooted(driveRelative)); Assert.False(Path.IsPathFullyQualified(driveRelative));
        await using (var c = await _database.Factory.OpenConnectionAsync())
        await using (var command = c.CreateCommand())
        {
            command.CommandText = "UPDATE Projects SET RootPath=$root";
            command.Parameters.AddWithValue("$root", driveRelative);
            await command.ExecuteNonQueryAsync();
        }
        // No cwd mutation or repo file reads: only Directory.Exists and owned synthetic HTTP.
        var request = await AdmitHttpPrompt(provider, Environment.CurrentDirectory);
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        Assert.Equal(ReviewChannelTurnOutcome.EgressPolicyRefused, (await HttpChannel(http,
            new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System)).RunAsync(request)).Outcome);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task SecondarySupervisorCannotConsumeQueuedReviewOrSendHttp()
    {
        using var primary = new ApplicationInstanceGuard(_database.Root);
        using var provider = CreateProvider(); await PrepareHttpRun(provider); var request = await AdmitHttpPrompt(provider);
        using var secondary = new ApplicationInstanceGuard(_database.Root); Assert.True(secondary.IsViewOnly);
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        Assert.Equal(ReviewChannelTurnOutcome.EgressPolicyRefused, (await HttpChannel(http,
            new SqliteWorkflowReviewEgressPolicy(_database.Factory, secondary, TimeProvider.System)).RunAsync(request)).Outcome);
        Assert.Empty(handler.Bodies); Assert.Equal("Queued", await EgressSql("SELECT State FROM Executions"));
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("model")]
    [InlineData("system")]
    [InlineData("two-messages")]
    [InlineData("effort")]
    [InlineData("session")]
    [InlineData("provider")]
    [InlineData("account")]
    [InlineData("stream")]
    [InlineData("context")]
    public async Task ActualClientCannotBypassStoredProofByReplacingSerializedWire(string change)
    {
        using var guard = new ApplicationInstanceGuard(_database.Root);
        using var provider = CreateProvider(); await PrepareHttpRun(provider); var admitted = await AdmitHttpPrompt(provider);
        var context = new WorkflowReviewEgressContext(admitted.ExecutionId, RealRouteId, StageId, ReviewerRole);
        var original = StarCliProxyChatRequest.Create("native-model-1", [new StarCliProxyChatMessage("user", EgressPrompt)])
            with { WorkflowReviewContext = context };
        var request = change switch
        {
            "prompt" => original with { Messages = [new("user", EgressPrompt + " altered")] },
            "model" => original with { ModelId = ModelId },
            "system" => original with { Messages = [new("system", EgressPrompt)] },
            "two-messages" => original with { Messages = [new("user", EgressPrompt),new("user", "additional unadmitted text")] },
            "effort" => original with { ReasoningEffort = "high" },
            "session" => original with { RequestedSessionId = "foreign" },
            "provider" => original with { RequestedProviderId = "foreign" },
            "account" => original with { RequestedAccountId = "foreign" },
            "stream" => original with { Stream = false },
            _ => original with { WorkflowReviewContext = context with { StageId = "foreign" } }
        };
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        var client = new StarCliProxyClient(http, workflowPolicy: new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System));
        await Assert.ThrowsAsync<WorkflowReviewEgressException>(async () =>
        { await foreach (var item in client.StreamChatCompletionAsync(StarCliProxyEndpoint.Loopback(4971, "owned-key"), request)) { } });
        Assert.Empty(handler.Bodies); Assert.Equal("Queued", await EgressSql("SELECT State FROM Executions"));
        Assert.Equal(0L, await EgressSql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='WorkflowReviewTransportAuthorized'"));
    }

    [Fact]
    public async Task SimultaneousClaimsOfOneStoredReviewReachHttpAtMostOnce()
    {
        using var guard = new ApplicationInstanceGuard(_database.Root);
        using var provider = CreateProvider(); await PrepareHttpRun(provider); var request = await AdmitHttpPrompt(provider);
        var real = new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System);
        var arrived = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = new AfterValidationPolicy(real, async () =>
        { if (Interlocked.Increment(ref arrived) == 2) release.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5)); });
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        var channel = HttpChannel(http, real, barrier);
        var results = await Task.WhenAll(channel.RunAsync(request), channel.RunAsync(request));
        Assert.Equal(2, arrived); Assert.Single(handler.Bodies);
        Assert.Single(results.Where(r => r.Outcome == ReviewChannelTurnOutcome.IdentityNotProvable));
        Assert.Single(results.Where(r => r.Outcome == ReviewChannelTurnOutcome.EgressPolicyRefused));
        Assert.Equal(1L, await EgressSql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='WorkflowReviewTransportAuthorized'"));
    }

    [Fact]
    public async Task RealAssignedReviewServicePreparesPromptAndRetainsNoNativeAuthority()
    {
        using var guard = new ApplicationInstanceGuard(_database.Root);
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        var policy = new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System);
        var channel = HttpChannel(http, policy);
        // Fixture addresses this channel explicitly; product SupportsRoute remains false / locator unaddressable.
        using var provider = CreateProvider(s => s.AddSingleton<IWorkflowChannelCatalog>(new FixtureHttpCatalog(channel)));
        await PrepareHttpRun(provider);
        var outcome = await provider.GetRequiredService<IWorkflowReviewRequestService>().RequestAssignedReviewAsync(RunId);
        Assert.False(outcome.IsRefusal); Assert.Single(handler.Bodies);
        Assert.Equal(1L, await EgressSql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='WorkflowReviewEgressPrepared'"));
        Assert.Equal(1L, await EgressSql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='WorkflowReviewTransportAuthorized'"));
        Assert.Equal("Ambiguous", await EgressSql("SELECT State FROM Executions"));
        Assert.Equal(await EgressSql("SELECT OccurredAtUtc FROM ExecutionEvents WHERE EventKind='WorkflowReviewTransportAuthorized'"),
            await EgressSql("SELECT StartedAtUtc FROM Executions"));
        Assert.Empty((await CurrentRunAsync()).Verdicts);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("503")]
    [InlineData("error")]
    [InlineData("partial-error")]
    [InlineData("malformed")]
    [InlineData("tool-call")]
    public async Task PossibleHttpDeliveryNeverBecomesRetryableWithoutNativeTerminalProof(string scenario)
    {
        using var guard = new ApplicationInstanceGuard(_database.Root); using var cancellation = new CancellationTokenSource();
        var handler = new ReviewHttpHandler { Scenario = scenario, CancelAfterReceive = cancellation }; using var http = new HttpClient(handler);
        var channel = HttpChannel(http, new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System));
        using var provider = CreateProvider(s => s.AddSingleton<IWorkflowChannelCatalog>(new FixtureHttpCatalog(channel)));
        await PrepareHttpRun(provider);
        var service = provider.GetRequiredService<IWorkflowReviewRequestService>();
        await service.RequestAssignedReviewAsync(RunId, cancellation.Token);
        Assert.Single(handler.Bodies);
        Assert.Equal("Ambiguous", await EgressSql("SELECT State FROM Executions"));
        Assert.Equal(1L, await EgressSql("SELECT COUNT(*) FROM Sessions WHERE State='Ambiguous' AND ActiveExecutionId IS NOT NULL"));
        Assert.True((await service.RequestAssignedReviewAsync(RunId)).IsRefusal);
        Assert.Single(handler.Bodies); Assert.Empty((await CurrentRunAsync()).Verdicts);
    }

    [Fact]
    public async Task ConfirmedPreTransportCancellationReleasesOnlyLocalOwnershipAndAllowsFreshManualReview()
    {
        using var guard = new ApplicationInstanceGuard(_database.Root); using var cancellation = new CancellationTokenSource();
        var handler = new ReviewHttpHandler(); using var http = new HttpClient(handler);
        var real = new SqliteWorkflowReviewEgressPolicy(_database.Factory, guard, TimeProvider.System);
        var cancelBeforeTransport = new AfterValidationPolicy(real, () => { cancellation.Cancel(); return Task.CompletedTask; });
        var channel = HttpChannel(http, real, cancelBeforeTransport);
        using var provider = CreateProvider(s => s.AddSingleton<IWorkflowChannelCatalog>(new FixtureHttpCatalog(channel)));
        await PrepareHttpRun(provider);
        var service = provider.GetRequiredService<IWorkflowReviewRequestService>();
        await service.RequestAssignedReviewAsync(RunId, cancellation.Token);
        Assert.True(cancelBeforeTransport.Validated); Assert.True(cancelBeforeTransport.Changed);
        Assert.Empty(handler.Bodies);
        Assert.Equal("Cancelled", await EgressSql("SELECT State FROM Executions"));
        Assert.Equal(0L, await EgressSql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='WorkflowReviewTransportAuthorized'"));
        Assert.Equal(0L, await EgressSql("SELECT COUNT(*) FROM Sessions WHERE ActiveExecutionId IS NOT NULL"));
        Assert.False((await service.RequestAssignedReviewAsync(RunId)).IsRefusal);
        Assert.Single(handler.Bodies); Assert.Empty((await CurrentRunAsync()).Verdicts);
    }

    private async Task PrepareHttpRun(ServiceProvider provider, DataClassification classification = DataClassification.PrivateSource)
    {
        Directory.CreateDirectory(_database.GetWorkspacePath());
        await using var c = await _database.Factory.OpenConnectionAsync(); await using var command = c.CreateCommand();
        command.CommandText = """
            UPDATE Projects SET RootPath=$root,DataClassification=$class;
            UPDATE ProviderProfiles SET Backend='StarCliProxy',BaseUrl='http://127.0.0.1:4971/',IsEnabled=1;
            UPDATE Routes SET Backend='StarCliProxy',IsEnabled=1;
            UPDATE Accounts SET AuthState='Valid',Health='Healthy',IsEnabled=1;
            UPDATE Models SET Backend='StarCliProxy',ProviderModelId='native-model-1',CapabilityState='Supported',Health='Healthy',IsEnabled=1;
            """;
        command.Parameters.AddWithValue("$root", _database.GetWorkspacePath()); command.Parameters.AddWithValue("$class", classification.ToString());
        await command.ExecuteNonQueryAsync(); await StartRunAsync(provider, RealRouteId, classification);
    }
    private async Task<WorkflowChannelTurnRequest> AdmitHttpPrompt(ServiceProvider provider, string? root = null)
    {
        var artifact = await CurrentArtifactAsync(); var now = DateTimeOffset.UtcNow;
        var session = new Session("http-review-session", new SessionBinding(BackendType.StarCliProxy, ProfileId, AccountId, ModelId, null, null, null),
            ProjectId, root ?? _database.GetWorkspacePath(), null, SessionState.Starting, ReconciliationOutcome.None, CloseReason.None, null, null,
            RunId, ReviewerRole, "http-review-execution", now, now);
        var execution = new Execution("http-review-execution", session.Id, "http-review-request", ExecutionState.Queued, ExecutionFailureReason.None,
            RealRouteId, null, null, null, null, null, [], null, null, now, null, null);
        var evidence = new ReviewerExecutionEvidence(execution.Id, session.Id, RunId, ReviewerRole, StageId, RealRouteId, null,
            artifact.ArtifactId, artifact.HashSha256, true, ExecutionState.Queued);
        Assert.True(await provider.GetRequiredService<IWorkflowReviewDispatchStore>().TryAdmitWithPromptAsync(session, execution, evidence, EgressPrompt));
        return new(execution.Id, new WorkflowNodeDefinition(StageId, WorkflowNodeKind.Review, "Review", ReviewerRole),
            new RoleBindingDefinition(ReviewerRole, RealRouteId, modelId: ModelId), RealRouteId, true, EgressPrompt);
    }
    private StarCliProxyReviewReadOnlyChannel HttpChannel(HttpClient http, IWorkflowReviewEgressPolicy transport, IWorkflowReviewEgressPolicy? channel = null)
        => new(new SqliteRouteRepository(_database.Factory), new FixtureHttpLocator(), new StarCliProxyClient(http, workflowPolicy: transport), egressPolicy: channel ?? transport);
    private async Task<object?> EgressSql(string sql)
    {
        await using var c = await _database.Factory.OpenConnectionAsync(); await using var command = c.CreateCommand(); command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(); return result is DBNull ? null : result;
    }
    private sealed class FixtureHttpLocator : IReviewChannelGatewayLocator
    {
        public ReviewChannelGatewayLocation Locate(WorkflowRouteAssignment assignment) => ReviewChannelGatewayLocation.At(StarCliProxyEndpoint.Loopback(4971, "owned-key"));
    }
    private sealed class FixtureHttpCatalog(IWorkflowNodeChannel channel) : IWorkflowChannelCatalog
    {
        public IWorkflowNodeChannel ResolveChannel(string route, IReadOnlyList<string> capabilities) => channel;
    }
    private sealed class AfterValidationPolicy(IWorkflowReviewEgressPolicy inner, Func<Task> change) : IWorkflowReviewEgressPolicy
    {
        public bool Validated { get; private set; }
        public bool Changed { get; private set; }
        public async Task<string> ValidateAsync(WorkflowReviewEgressContext context, string endpoint, string prompt, string? effort, string? session, CancellationToken token)
        { var model = await inner.ValidateAsync(context, endpoint, prompt, effort, session, token); Validated = true; await change(); Changed = true; return model; }
        public Task AuthorizeAsync(WorkflowReviewEgressContext context, string endpoint, string body, string? session, CancellationToken token)
            => inner.AuthorizeAsync(context, endpoint, body, session, token);
    }
    private sealed class ReviewHttpHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public string? Scenario;
        public CancellationTokenSource? CancelAfterReceive;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (Scenario == "cancel") { CancelAfterReceive!.Cancel(); throw new OperationCanceledException(token); }
            if (Scenario == "503") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("owned gateway failure") };
            if (Scenario == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("data: {bad-json}\n\n",Encoding.UTF8,"text/event-stream") };
            if (Scenario == "tool-call") return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call\",\"type\":\"function\",\"function\":{\"name\":\"unsupported\",\"arguments\":\"{}\"}}]}}]}\n\n",Encoding.UTF8,"text/event-stream") };
            if (Scenario is "error" or "partial-error") return new(HttpStatusCode.OK) { Content = new StringContent(
                (Scenario == "partial-error" ? "data: {\"choices\":[{\"delta\":{\"content\":\"partial answer\"}}]}\n\n" : "")
                + "data: {\"error\":{\"message\":\"owned stream fault\"}}\n\n", Encoding.UTF8,"text/event-stream") };
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"owned review answer\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8,"application/json") };
        }
    }
}
