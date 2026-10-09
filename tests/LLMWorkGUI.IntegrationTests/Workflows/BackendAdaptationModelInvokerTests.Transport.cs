using System.Collections.Concurrent;
using System.Net;
using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Workflows;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using Microsoft.Data.Sqlite;
using LLMWorkGUI.Infrastructure.OpenCode;
using LLMWorkGUI.Application.Reconciliation;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class BackendAdaptationModelInvokerTests
{
    private sealed class AdaptationHttpHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(string Path,string Body)> Requests { get; } = new();
        public string CreateJson="""{"id":"session-abc"}""";
        public HttpStatusCode Status=HttpStatusCode.OK;
        public Func<string,CancellationToken,Task>? AfterReceive;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var path=request.RequestUri!.AbsolutePath;
            Requests.Enqueue((path,request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            if (AfterReceive is not null) await AfterReceive(path,token);
            return new(Status) { Content=new StringContent(path=="/session" ? CreateJson : "true",Encoding.UTF8,"application/json") };
        }
    }

    [Fact]
    public async Task RealSqlLateRestrictionDuringClientResolutionRefusesActualPromptHttp()
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        await MaterialStore(db,guard).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource);
        var policy=MaterialGate(db,guard); var request=MaterialRequest(); request=request with { AdmissionId=await policy.PrepareAsync(request,default) };
        using var handler=new AdaptationHttpHandler(); using var http=new HttpClient(handler);
        var resolutions=0;
        var client=new OpenCodeClient(http,new Uri("http://127.0.0.1:54321/"),resolveBaseUrl:async token=>
        {
            if(Interlocked.Increment(ref resolutions)==2) await MaterialSql(db,"UPDATE Projects SET DataClassification='Restricted'");
            return new Uri("http://127.0.0.1:54321/");
        },adaptationTransportPolicy:new SqliteAdaptationTransportPolicy(db.Factory,guard,new WorkflowSecretScanner(),TimeProvider.System));
        // Native lifecycle is synthetic. Actual sealed client, SQLite and last-hop HTTP handler are real.
        var lifecycle=new FakeOpenCodeSessionLifecycleService
        {
            CreateHandler=client.CreateSessionAsync,
            AsyncTurnHandler=async (session,prompt,token)=>
            { await client.SendPromptAsync(session,prompt,token); return CreateTurnResult(); }
        };
        var invoker=new AdaptationProtocolFixtureInvoker(lifecycle,client,new SqliteProviderProfileRepository(db.Factory),
            new SqliteAccountRepository(db.Factory),egressPolicy:policy);
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>invoker.InvokeModelAsync(request));
        Assert.Contains(handler.Requests,r=>r.Path=="/session");
        Assert.DoesNotContain(handler.Requests,r=>r.Path.EndsWith("/prompt_async",StringComparison.Ordinal));
    }

    private sealed class TransportFixture : IDisposable
    {
        public TestDatabase Db { get; } = new();
        public ApplicationInstanceGuard Guard { get; private set; } = null!;
        public SqliteAdaptationEgressPolicy Egress { get; private set; } = null!;
        public SqliteAdaptationTransportPolicy Transport { get; private set; } = null!;
        public AdaptationModelRequest Request { get; private set; } = null!;
        public AdaptationHttpHandler Handler { get; } = new();
        private HttpClient Http { get; }
        public OpenCodeClient Client { get; private set; } = null!;
        public string Fingerprint { get; private set; } = null!;
        public Func<int,CancellationToken,Task<Uri>>? Resolve;
        private int resolutions;
        public TransportFixture() { Http=new(Handler); }
        public static async Task<TransportFixture> CreateAsync()
        {
            var f=new TransportFixture();
            try
            {
                await SeedMaterial(f.Db); f.Guard=new(f.Db.Root);
                await MaterialStore(f.Db,f.Guard).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource);
                f.Egress=MaterialGate(f.Db,f.Guard);
                f.Transport=new(f.Db.Factory,f.Guard,new WorkflowSecretScanner(),TimeProvider.System);
                f.Request=MaterialRequest(); f.Request=f.Request with { AdmissionId=await f.Egress.PrepareAsync(f.Request,default) };
                f.Client=new(f.Http,new Uri("http://127.0.0.1:54321/"),resolveBaseUrl:token=>
                    f.Resolve?.Invoke(Interlocked.Increment(ref f.resolutions),token) ?? Task.FromResult(new Uri("http://127.0.0.1:54321/")),
                    adaptationTransportPolicy:f.Transport);
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        public OpenCodeCreateSessionRequest CreateRequest() => new()
        { Title="workflow-adaptation-"+Guid.NewGuid().ToString("N"),Model=NativeModelId,Agent="plan",AdaptationAdmissionId=Request.AdmissionId };
        public OpenCodePromptRequest PromptRequest() => new()
        { Prompt=AdaptationPromptEnvelope.Format(Request),Model=NativeModelId,Agent="plan",AdaptationAdmissionId=Request.AdmissionId };
        public async Task PreflightAsync()
        {
            AdaptationRouteIdentity.TryParse(Request.RouteId,out var identity);
            Fingerprint=(await Egress.ValidateAsync(Request,identity,AdaptationPromptEnvelope.Format(Request),null,default)).Fingerprint;
        }
        public async Task BoundAsync()
        { await PreflightAsync(); await Client.CreateSessionAsync(CreateRequest()); }
        public async Task PromptReadyAsync()
        {
            await BoundAsync(); AdaptationRouteIdentity.TryParse(Request.RouteId,out var identity);
            await Egress.ValidateAsync(Request,identity,AdaptationPromptEnvelope.Format(Request),Fingerprint,default);
        }
        public Task<object?> Sql(string text) => MaterialSql(Db,text);
        public void Dispose() { Http.Dispose(); Guard?.Dispose(); Db.Dispose(); }
    }

    [Fact]
    public async Task ActualHttpExactPromptIsConsumedOnceAndAbortAcknowledgementRetainsAccountOwnership()
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync(); var prompt=f.PromptRequest();
        Assert.True(await f.Client.SendPromptAsync("session-abc",prompt));
        Assert.True(await f.Client.AbortSessionAsync("session-abc"));
        Assert.Equal("PromptAuthorized",await f.Sql("SELECT State FROM WorkflowAdaptationNativeBindings"));
        Assert.Equal("Ambiguous",await f.Sql("SELECT State FROM Executions"));
        Assert.Null(await f.Sql("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal(1L,await f.Sql("SELECT COUNT(*) FROM Sessions s JOIN Executions e ON s.ActiveExecutionId=e.Id"));
        Assert.Equal(4L,await f.Sql("SELECT COUNT(*) FROM WorkflowAdaptationTransportChecks"));
        var wire=f.Handler.Requests.Single(r=>r.Path.EndsWith("/prompt_async",StringComparison.Ordinal)).Body;
        Assert.DoesNotContain("AdaptationAdmissionId",wire);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(wire))),
            await f.Sql("SELECT PayloadSha256 FROM WorkflowAdaptationTransportChecks WHERE Phase='Prompt'"));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.SendPromptAsync("session-abc",prompt));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Egress.PrepareAsync(MaterialRequest(),default));
        Assert.Single(f.Handler.Requests.Where(r=>r.Path.EndsWith("/prompt_async",StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("UPDATE Projects SET DataClassification='Restricted'")]
    [InlineData("UPDATE Routes SET IsEnabled=0")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'")]
    [InlineData("UPDATE Accounts SET Health='Disabled'")]
    [InlineData("UPDATE Models SET ProviderModelId='different/model'")]
    [InlineData("UPDATE Models SET CapabilityState='Unknown'")]
    [InlineData("UPDATE ProviderProfiles SET MaxDataClass='PublicSource'")]
    [InlineData("UPDATE Routes SET ExecutionMode='write'")]
    [InlineData("UPDATE ProviderProfiles SET BaseUrl='http://127.0.0.1:12345/'")]
    public async Task ActualCreateHttpRefusesPolicyChangedByLateEndpointResolver(string mutation)
    {
        using var f=await TransportFixture.CreateAsync(); await f.PreflightAsync();
        f.Resolve=async (_,token)=> { await f.Sql(mutation); return new("http://127.0.0.1:54321/"); };
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.CreateSessionAsync(f.CreateRequest()));
        Assert.Empty(f.Handler.Requests); Assert.Equal(0L,await f.Sql("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(0L,await f.Sql("SELECT COUNT(*) FROM WorkflowAdaptationTransportChecks"));
    }

    [Theory]
    [InlineData("UPDATE Projects SET DataClassification='Restricted'")]
    [InlineData("UPDATE Routes SET IsEnabled=0")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'")]
    [InlineData("UPDATE Models SET CapabilityState='Unknown'")]
    [InlineData("UPDATE ProviderProfiles SET MaxDataClass='PublicSource'")]
    [InlineData("UPDATE Routes SET ReasoningEffort='high'")]
    public async Task ActualPromptHttpRefusesPolicyChangedAfterPromptPreflight(string mutation)
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync();
        f.Resolve=async (_,token)=> { await f.Sql(mutation); return new("http://127.0.0.1:54321/"); };
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.SendPromptAsync("session-abc",f.PromptRequest()));
        Assert.Single(f.Handler.Requests); Assert.Equal("Bound",await f.Sql("SELECT State FROM WorkflowAdaptationNativeBindings"));
        Assert.Null(await f.Sql("SELECT EndedAtUtc FROM Executions"));
    }

    [Theory]
    [InlineData("context")]
    [InlineData("forged")]
    [InlineData("model")]
    [InlineData("agent")]
    [InlineData("text")]
    [InlineData("secret")]
    [InlineData("message")]
    [InlineData("oversize")]
    public async Task ActualPromptHttpRefusesDifferentSerializedPayloadOrContext(string change)
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync(); var p=f.PromptRequest();
        p=change switch
        {
            "context"=>p with { AdaptationAdmissionId=null }, "forged"=>p with { AdaptationAdmissionId=Guid.NewGuid() },
            "model"=>p with { Model="other/model" }, "agent"=>p with { Agent="build" },
            "text"=>p with { Prompt=p.Prompt+"changed" }, "secret"=>p with { Prompt=p.Prompt+"\n-----BEGIN PRIVATE KEY-----" },
            "message"=>p with { MessageId="unbound" }, "oversize"=>p with { Prompt=new string('\u4e2d',200_001) }, _=>throw new Exception()
        };
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.SendPromptAsync("session-abc",p));
        Assert.Single(f.Handler.Requests); Assert.Equal("Bound",await f.Sql("SELECT State FROM WorkflowAdaptationNativeBindings"));
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Prompt")]
    public async Task ActualHttpAuditFailureRollsBackReservationOrConsumptionBeforeDelivery(string phase)
    {
        using var f=await TransportFixture.CreateAsync();
        if (phase=="Create") await f.PreflightAsync(); else await f.PromptReadyAsync();
        await f.Sql($"CREATE TRIGGER FixtureAuditFault BEFORE INSERT ON WorkflowAdaptationTransportChecks WHEN NEW.Phase='{phase}' BEGIN SELECT RAISE(ABORT,'owned audit fault'); END");
        if (phase=="Create")
        {
            await Assert.ThrowsAsync<SqliteException>(()=>f.Client.CreateSessionAsync(f.CreateRequest()));
            Assert.Empty(f.Handler.Requests); Assert.Equal(0L,await f.Sql("SELECT COUNT(*) FROM Sessions"));
            Assert.Equal(0L,await f.Sql("SELECT COUNT(*) FROM Executions"));
            Assert.Equal(0L,await f.Sql("SELECT COUNT(*) FROM WorkflowAdaptationNativeBindings"));
        }
        else
        {
            await Assert.ThrowsAsync<SqliteException>(()=>f.Client.SendPromptAsync("session-abc",f.PromptRequest()));
            Assert.Single(f.Handler.Requests); Assert.Equal("Bound",await f.Sql("SELECT State FROM WorkflowAdaptationNativeBindings"));
            Assert.Equal("NativeCreateDeliveryUnconfirmed",await f.Sql("SELECT ProcessState FROM Executions"));
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("cancel")]
    [InlineData("malformed")]
    [InlineData("invalid-id")]
    [InlineData("bind-audit")]
    public async Task ActualCreateHttpUnconfirmedOutcomeKeepsDurableReservationAndRefusesFreshManualRetry(string failure)
    {
        using var f=await TransportFixture.CreateAsync(); await f.PreflightAsync();
        using var cancellation=new CancellationTokenSource();
        if (failure=="status") f.Handler.Status=HttpStatusCode.ServiceUnavailable;
        if (failure=="cancel") f.Handler.AfterReceive=(_,_)=> { cancellation.Cancel(); return Task.FromCanceled(cancellation.Token); };
        if (failure=="malformed") f.Handler.CreateJson="not-json";
        if (failure=="invalid-id") f.Handler.CreateJson="""{"id":"session/other"}""";
        if (failure=="bind-audit") await f.Sql("CREATE TRIGGER FixtureBindFault BEFORE INSERT ON WorkflowAdaptationTransportChecks WHEN NEW.Phase='Bind' BEGIN SELECT RAISE(ABORT,'owned bind fault'); END");
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Client.CreateSessionAsync(f.CreateRequest(),cancellation.Token));
        Assert.Single(f.Handler.Requests); Assert.Equal("CreateAuthorized",await f.Sql("SELECT State FROM WorkflowAdaptationNativeBindings"));
        Assert.Null(await f.Sql("SELECT NativeSessionId FROM Sessions")); Assert.Null(await f.Sql("SELECT EndedAtUtc FROM Executions"));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Egress.PrepareAsync(MaterialRequest(),default));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.CreateSessionAsync(f.CreateRequest()));
        Assert.Single(f.Handler.Requests);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("cancel")]
    public async Task ActualPromptHttpUnconfirmedOutcomeCannotBeReplayed(string failure)
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync();
        using var cancellation=new CancellationTokenSource();
        if (failure=="status") f.Handler.Status=HttpStatusCode.ServiceUnavailable;
        else f.Handler.AfterReceive=(_,_)=> { cancellation.Cancel(); return Task.FromCanceled(cancellation.Token); };
        if (failure=="status")
        {
            // A server response after dispatch cannot prove non-delivery. The client reports that
            // uncertain HTTP outcome explicitly; it must never masquerade as local denial/false.
            var error = await Assert.ThrowsAsync<OpenCodeClientException>(() =>
                f.Client.SendPromptAsync("session-abc", f.PromptRequest()));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        }
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Client.SendPromptAsync("session-abc",f.PromptRequest(),cancellation.Token));
        Assert.Equal("PromptAuthorized",await f.Sql("SELECT State FROM WorkflowAdaptationNativeBindings"));
        Assert.Null(await f.Sql("SELECT EndedAtUtc FROM Executions"));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.SendPromptAsync("session-abc",f.PromptRequest()));
        Assert.Equal(2,f.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentActualCreateOrPromptHasExactlyOneHttpWinner(bool prompt)
    {
        using var f=await TransportFixture.CreateAsync(); if (prompt) await f.PromptReadyAsync(); else await f.PreflightAsync();
        async Task<bool> Attempt()
        {
            try { if(prompt) await f.Client.SendPromptAsync("session-abc",f.PromptRequest()); else await f.Client.CreateSessionAsync(f.CreateRequest()); return true; }
            catch(WorkflowValidationException) { return false; }
        }
        var results=await Task.WhenAll(Task.Run(Attempt),Task.Run(Attempt));
        Assert.Equal(1,results.Count(x=>x)); Assert.Equal(prompt ? 2 : 1,f.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("http://127.0.0.1:54322/")]
    [InlineData("https://127.0.0.1:54321/")]
    [InlineData("http://example.invalid/")]
    [InlineData("http://actor@127.0.0.1:54321/")]
    public async Task ChangedOrInvalidEndpointCannotSendBoundPromptOrAbort(string endpoint)
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync();
        f.Resolve=(_,_)=>Task.FromResult(new Uri(endpoint));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.SendPromptAsync("session-abc",f.PromptRequest()));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.AbortSessionAsync("session-abc"));
        Assert.Single(f.Handler.Requests);
    }

    [Fact]
    public async Task AbortAfterRestrictionIsAuditedWithoutReleasingUncertainOwnership()
    {
        using var f=await TransportFixture.CreateAsync(); await f.BoundAsync();
        await f.Sql("UPDATE Projects SET DataClassification='Restricted'");
        Assert.True(await f.Client.AbortSessionAsync("session-abc"));
        Assert.Equal(1L,await f.Sql("SELECT COUNT(*) FROM WorkflowAdaptationTransportChecks WHERE Phase='Abort'"));
        Assert.Null(await f.Sql("SELECT EndedAtUtc FROM Executions"));
        await Assert.ThrowsAsync<SqliteException>(()=>f.Sql("UPDATE Executions SET State='Succeeded',EndedAtUtc='2026-10-05T00:00:00Z'"));
        await Assert.ThrowsAsync<SqliteException>(()=>f.Sql("UPDATE Sessions SET ActiveExecutionId=NULL,State='Idle'"));
        await Assert.ThrowsAsync<SqliteException>(()=>f.Sql("DELETE FROM WorkflowAdaptationNativeBindings"));
        await Assert.ThrowsAsync<SqliteException>(()=>f.Sql("UPDATE WorkflowAdaptationPromptAdmissions SET PromptSha256='changed'"));
    }

    [Fact]
    public async Task SecondaryInstanceCannotDispatchOrCancelBoundAdaptation()
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync(); using var secondary=new ApplicationInstanceGuard(f.Db.Root);
        var policy=new SqliteAdaptationTransportPolicy(f.Db.Factory,secondary,new WorkflowSecretScanner(),TimeProvider.System);
        using var http=new HttpClient(f.Handler,disposeHandler:false);
        var client=new OpenCodeClient(http,new Uri("http://127.0.0.1:54321/"),adaptationTransportPolicy:policy);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>client.SendPromptAsync("session-abc",f.PromptRequest()));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>client.AbortSessionAsync("session-abc")); Assert.Single(f.Handler.Requests);
    }

    [Fact]
    public async Task ActuallyReportedDirectoryQueryIsPinnedWithoutClaimingNativeCwdAuthority()
    {
        using var f=await TransportFixture.CreateAsync(); f.Handler.CreateJson="""{"id":"session-abc","directory":"C:\\owned\\synthetic"}""";
        await f.PromptReadyAsync(); Assert.True(await f.Client.SendPromptAsync("session-abc",f.PromptRequest()));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Transport.AuthorizeAbortAsync("session-abc",
            new Uri("http://127.0.0.1:54321/session/session-abc/abort?directory=changed"),default));
        Assert.Equal("PromptAuthorized",await f.Sql("SELECT State FROM WorkflowAdaptationNativeBindings"));
    }

    [Fact]
    public async Task SyntheticCompletedTurnDoesNotReleaseActualHttpNativeOwnership()
    {
        using var f=await TransportFixture.CreateAsync();
        var lifecycle=new FakeOpenCodeSessionLifecycleService
        {
            CreateHandler=f.Client.CreateSessionAsync,
            AsyncTurnHandler=async (session,prompt,token)=> { await f.Client.SendPromptAsync(session,prompt,token); return CreateTurnResult(); }
        };
        var invoker=new AdaptationProtocolFixtureInvoker(lifecycle,f.Client,new SqliteProviderProfileRepository(f.Db.Factory),
            new SqliteAccountRepository(f.Db.Factory),egressPolicy:f.Egress);
        await invoker.InvokeModelAsync(f.Request);
        Assert.Equal(3,f.Handler.Requests.Count); Assert.Equal("Ambiguous",await f.Sql("SELECT State FROM Executions"));
        Assert.Null(await f.Sql("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal("PromptAuthorized",await f.Sql("SELECT State FROM WorkflowAdaptationNativeBindings"));
    }

    [Fact]
    public async Task NewPrimaryAfterRestartQuarantinesReservationAndCannotResendOrAcknowledgeItAway()
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync(); f.Guard.Dispose();
        using var restarted=new ApplicationInstanceGuard(f.Db.Root);
        var transport=new SqliteAdaptationTransportPolicy(f.Db.Factory,restarted,new WorkflowSecretScanner(),TimeProvider.System);
        var recovery=new SqliteOpenCodeJournalRecoveryService(f.Db.Factory,restarted,TimeProvider.System);
        var evidence=Assert.Single(await recovery.QuarantineInterruptedAsync());
        Assert.Equal("Ambiguous",evidence.Outcome.ToString()); Assert.Equal("session-abc",evidence.NativeSessionId);
        Assert.False(evidence.BindingMatched);
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>transport.AuthorizePromptAsync(f.Request.AdmissionId,"session-abc",
            new Uri("http://127.0.0.1:54321/session/session-abc/prompt_async"),Encoding.UTF8.GetBytes(OpenCodeSessionJson.SerializePromptRequest(f.PromptRequest())),default));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>transport.AuthorizeAbortAsync("session-abc",
            new Uri("http://127.0.0.1:54321/session/session-abc/abort"),default));
        foreach(var action in new[] { RecoveryAction.ResetSession,RecoveryAction.CloseSession,RecoveryAction.AcknowledgeAmbiguous })
            await Assert.ThrowsAsync<InvalidOperationException>(()=>recovery.TryApplyActionAsync(evidence.LocalSessionId,action));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>MaterialGate(f.Db,restarted).PrepareAsync(MaterialRequest(),default));
        Assert.Single(f.Handler.Requests); Assert.Null(await f.Sql("SELECT EndedAtUtc FROM Executions"));
    }

    [Fact]
    public async Task DeclaredMaterialRevisionChangeAfterPreflightCannotReachActualPromptHttp()
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync();
        await MaterialStore(f.Db,f.Guard).DeclareAsync("material-version",MaterialBlob,1,DataClassification.PublicSource);
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.SendPromptAsync("session-abc",f.PromptRequest()));
        Assert.Single(f.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackendAuthenticationFanoutOrHealthProjectionAfterPreflightStopsActualPrompt(bool fanout)
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync();
        await f.Sql(fanout
            ? "INSERT INTO HealthAuthenticationFanout(Id,ScopeType,ScopeId,Reason,ObservedAtUtc) VALUES('owned','backend','OpenCode','owned fixture','2026-10-05T00:00:00Z')"
            : "INSERT INTO HealthStates(Id,ScopeType,ScopeId,State,UpdatedAtUtc) VALUES('owned','backend','OpenCode','Disabled','2026-10-05T00:00:00Z')");
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.SendPromptAsync("session-abc",f.PromptRequest()));
        Assert.Single(f.Handler.Requests);
    }

    [Fact]
    public async Task IndependentUncertainExecutionConsumesAccountSlotBeforeActualCreate()
    {
        using var f=await TransportFixture.CreateAsync(); await f.PreflightAsync();
        await f.Sql("""
            INSERT INTO Sessions(Id,ProjectId,Backend,ProviderProfileId,AccountId,ModelId,WorkspaceRootPath,State,
                ReconciliationOutcome,CloseReason,ActiveExecutionId,CreatedAtUtc,LastEventAtUtc)
            SELECT 'other-session',Id,'OpenCode','provider-1','account-1','model-1',RootPath,'Ambiguous','Ambiguous','None',
                'other-execution',CreatedAtUtc,UpdatedAtUtc FROM Projects;
            INSERT INTO Executions(Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,CreatedAtUtc)
            VALUES('other-execution','other-session','other-request','Ambiguous','None','route-1','2026-10-05T00:00:00Z');
            """);
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.CreateSessionAsync(f.CreateRequest()));
        Assert.Empty(f.Handler.Requests); Assert.Equal(0L,await f.Sql("SELECT COUNT(*) FROM WorkflowAdaptationNativeBindings"));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("invalid-utf8")]
    [InlineData("wrong-parts")]
    [InlineData("invalid-unicode")]
    public async Task BoundaryRejectsNonCanonicalOrMalformedWireWithoutConsumingBinding(string kind)
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync();
        var json=OpenCodeSessionJson.SerializePromptRequest(f.PromptRequest());
        byte[] body=kind switch
        {
            "extra"=>Encoding.UTF8.GetBytes(json[..^1]+",\"tools\":true}"),
            "duplicate"=>Encoding.UTF8.GetBytes(json[..^1]+",\"agent\":\"plan\"}"),
            "invalid-utf8"=>[0xFF,0xFE],
            "wrong-parts"=>Encoding.UTF8.GetBytes(json.Replace("\"type\":\"text\"","\"type\":\"file\"",StringComparison.Ordinal)),
            "invalid-unicode"=>Encoding.UTF8.GetBytes(json.Replace("\"type\":\"text\"","\"type\":\"\\uD800\"",StringComparison.Ordinal)),
            _=>throw new Exception()
        };
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Transport.AuthorizePromptAsync(f.Request.AdmissionId,"session-abc",
            new Uri("http://127.0.0.1:54321/session/session-abc/prompt_async"),body,default));
        Assert.Equal("Bound",await f.Sql("SELECT State FROM WorkflowAdaptationNativeBindings")); Assert.Single(f.Handler.Requests);
    }

    [Fact]
    public async Task ExpiredAdmissionAtActualPromptBoundaryIsNotConsumed()
    {
        using var f=await TransportFixture.CreateAsync(); await f.PromptReadyAsync();
        var future=new MaterialClock { Now=DateTimeOffset.UtcNow+TimeSpan.FromMinutes(6) };
        var transport=new SqliteAdaptationTransportPolicy(f.Db.Factory,f.Guard,new WorkflowSecretScanner(),future);
        using var http=new HttpClient(f.Handler,disposeHandler:false);
        var client=new OpenCodeClient(http,new Uri("http://127.0.0.1:54321/"),adaptationTransportPolicy:transport);
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>client.SendPromptAsync("session-abc",f.PromptRequest()));
        Assert.Single(f.Handler.Requests); Assert.Equal("Bound",await f.Sql("SELECT State FROM WorkflowAdaptationNativeBindings"));
    }

    [Fact]
    public async Task CreateWithDirectoryOrMissingTransportCannotSendHttp()
    {
        using var f=await TransportFixture.CreateAsync(); await f.PreflightAsync();
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>f.Client.CreateSessionAsync(f.CreateRequest() with { Directory=f.Db.GetWorkspacePath() }));
        using var http=new HttpClient(f.Handler,disposeHandler:false); var raw=new OpenCodeClient(http,new Uri("http://127.0.0.1:54321/"));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>raw.CreateSessionAsync(f.CreateRequest()));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>raw.SendPromptAsync("session-abc",f.PromptRequest()));
        Assert.Empty(f.Handler.Requests);
    }
}
