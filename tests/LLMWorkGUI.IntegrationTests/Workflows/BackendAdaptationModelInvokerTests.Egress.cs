using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class BackendAdaptationModelInvokerTests
{
    private const string MaterialBlob = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private sealed class MaterialActor(string? actor = "owned fixture operator") : IUserApprovalIdentity
    { public string? GetCurrentApproverIdentity() => actor; }
    private static SqliteWorkflowMaterialPolicyStore MaterialStore(TestDatabase db, ApplicationInstanceGuard guard, string? actor = "owned fixture operator")
        => new(db.Factory,guard,new MaterialActor(actor),TimeProvider.System);
    private static SqliteAdaptationEgressPolicy MaterialGate(TestDatabase db,ApplicationInstanceGuard guard)
        => new(db.Factory,guard,new WorkflowSecretScanner(),TimeProvider.System);
    private static async Task SeedMaterial(TestDatabase db, string blobId = MaterialBlob)
    {
        await db.InitializeAsync(); Directory.CreateDirectory(db.GetWorkspacePath()); await db.SeedRouteChainAsync();
        var now = DateTimeOffset.UtcNow;
        await new SqliteWorkflowPackageRepository(db.Factory).UpsertAsync(new WorkflowPackage("material-package","Owned",null,[],
            WorkflowSourceType.SyntheticDraft,blobId,blobId,now,now));
        await new SqliteWorkflowVersionRepository(db.Factory).UpsertAsync(new WorkflowVersion("material-version","material-package",1,
            blobId,blobId,WorkflowSourceType.SyntheticDraft,null,null,null,null,null,now,null));
        await MaterialSql(db,"UPDATE Models SET ProviderModelId='opencode/space-bunny-free'");
        await MaterialSql(db,"UPDATE Accounts SET ProviderNativeId='opencode/space-bunny-free'");
        // Blob IDs are synthetic here: this fixture proves stored metadata/preflight, not source-blob reads or HTTP.
    }
    private static AdaptationModelRequest MaterialRequest() => new(new AdaptationRouteIdentity("account-1","provider-1",BackendType.OpenCode,NativeModelId).RouteId,
        NativeModelId,"Review workflow mappings",[new("user","Synthetic workflow prompt")])
        { ProjectId="project-1",SourceVersionId="material-version" };
    private static AdaptationProtocolFixtureInvoker MaterialInvoker(TestDatabase db,IAdaptationEgressPolicy policy,
        FakeOpenCodeSessionLifecycleService lifecycle,FakeOpenCodeClient client)
        => new(lifecycle,client,new SqliteProviderProfileRepository(db.Factory),new SqliteAccountRepository(db.Factory),egressPolicy:policy);
    private static async Task<object?> MaterialSql(TestDatabase db,string sql)
    { await using var c=await db.Factory.OpenConnectionAsync(); await using var command=c.CreateCommand(); command.CommandText=sql;
      var result=await command.ExecuteScalarAsync(); return result is DBNull ? null : result; }

    private static WorkflowAdaptationService MaterialService(TestDatabase db,WorkflowBlobStore blobs,
        IAdaptationEgressPolicy policy,IAdaptationModelInvoker invoker)
    {
        var profiles=new SqliteProviderProfileRepository(db.Factory); var accounts=new SqliteAccountRepository(db.Factory);
        return new(new SqliteWorkflowVersionRepository(db.Factory),new SqliteWorkflowPackageRepository(db.Factory),
            new ScratchWorkspaceManager(blobs,new SafeArchiveValidator()),new WorkflowSecretScanner(),new WorkflowAdaptationPromptBuilder(),
            new SanitizedCatalogProvider(profiles,accounts,new SqliteHealthStateRepository(db.Factory)),
            new SqliteQuotaSnapshotRepository(db.Factory),accounts,invoker,new AdaptationResponseParser(),new AdaptationReferenceValidator(),
            new SemanticDiffEngine(),new WorkflowDiffService(),blobs,egressPolicy:policy);
    }

    [Fact]
    public async Task RealSourceBlobServiceAndSqlPolicyBindEveryTurnAndRefuseLaterRestriction()
    {
        using var db=new TestDatabase(); var blobs=new WorkflowBlobStore(db.Root);
        var raw=WorkflowTestArchiveFactory.CreateArchive(zip=>WorkflowTestArchiveFactory.AddEntry(zip,"README.md","owned source marker"));
        var blob=await blobs.SaveBlobAsync(new MemoryStream(raw)); await SeedMaterial(db,blob.BlobId);
        using var guard=new ApplicationInstanceGuard(db.Root); var store=MaterialStore(db,guard);
        await store.DeclareAsync("material-version",blob.BlobId,0,DataClassification.PrivateSource);
        var policy=MaterialGate(db,guard);
        // Native lifecycle remains synthetic; the source bytes, extraction, service and SQL policy are real.
        var lifecycle=new FakeOpenCodeSessionLifecycleService { TurnResult=CreateTurnResult(outputText:
            """{"mappings":[],"rationale":"owned answer marker","warnings":[],"blockers":[],"fileModifications":{}}""") };
        var service=MaterialService(db,blobs,policy,MaterialInvoker(db,policy,lifecycle,new FakeOpenCodeClient()));
        var first=await service.StartAdaptationAsync(new AdaptationExecutionRequest("material-version",MaterialRequest().RouteId,AdaptationGoal.Balanced)
            { ProjectId="project-1" });
        await service.SubmitFollowUpTurnAsync(new(first.SessionId,"owned followup marker"));
        Assert.Equal(2,lifecycle.CreateRequests.Count); Assert.Equal(2,lifecycle.TurnRequests.Count);
        Assert.Contains("owned source marker",lifecycle.TurnRequests[0].Request.Prompt);
        Assert.Contains("owned source marker",lifecycle.TurnRequests[1].Request.Prompt);
        Assert.Contains("owned answer marker",lifecycle.TurnRequests[1].Request.Prompt);
        Assert.Contains("owned followup marker",lifecycle.TurnRequests[1].Request.Prompt);
        Assert.Equal(2L,await MaterialSql(db,"SELECT COUNT(DISTINCT PromptSha256) FROM WorkflowAdaptationPromptAdmissions"));
        Assert.Equal(2L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPromptAdmissions WHERE ProjectId='project-1' AND VersionId='material-version' AND State='PromptPreflight'"));
        Assert.Equal(6L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPolicyChecks"));
        Assert.Equal(raw,await File.ReadAllBytesAsync(blobs.GetBlobPath(blob.BlobId)));
        await store.DeclareAsync("material-version",blob.BlobId,1,DataClassification.Restricted);
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>service.SubmitFollowUpTurnAsync(new(first.SessionId,"refused followup")));
        Assert.Equal(2,lifecycle.CreateRequests.Count); Assert.Equal(2,lifecycle.TurnRequests.Count);
        Assert.Equal(2L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPromptAdmissions"));
        await service.DiscardSessionAsync(first.SessionId);
    }

    [Fact]
    public async Task TamperedRealSourceBlobRefusesBeforePreparationAndNativeLifecycle()
    {
        using var db=new TestDatabase(); var blobs=new WorkflowBlobStore(db.Root);
        var raw=WorkflowTestArchiveFactory.CreateArchive(zip=>WorkflowTestArchiveFactory.AddEntry(zip,"README.md","owned source"));
        var blob=await blobs.SaveBlobAsync(new MemoryStream(raw)); await SeedMaterial(db,blob.BlobId);
        using var guard=new ApplicationInstanceGuard(db.Root);
        await MaterialStore(db,guard).DeclareAsync("material-version",blob.BlobId,0,DataClassification.PrivateSource);
        await File.WriteAllBytesAsync(blobs.GetBlobPath(blob.BlobId),[1,2,3]);
        var policy=MaterialGate(db,guard); var lifecycle=new FakeOpenCodeSessionLifecycleService();
        var service=MaterialService(db,blobs,policy,MaterialInvoker(db,policy,lifecycle,new FakeOpenCodeClient()));
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.StartAdaptationAsync(
            new AdaptationExecutionRequest("material-version",MaterialRequest().RouteId,AdaptationGoal.Balanced) { ProjectId="project-1" }));
        Assert.Empty(lifecycle.CreateRequests); Assert.Empty(lifecycle.TurnRequests);
        Assert.Equal(0L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPromptAdmissions"));
    }

    [Fact]
    public async Task UndeclaredMaterialIsRestrictedAndNeverCreatesNativeSession()
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        var material=await MaterialStore(db,guard).ReadAsync("material-version");
        Assert.False(material.IsDeclared); Assert.Equal(0,material.Revision); Assert.Equal(DataClassification.Restricted,material.Classification);
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>MaterialGate(db,guard).PrepareAsync(MaterialRequest(),default));
        Assert.Equal(0L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPromptAdmissions"));
    }
    [Fact]
    public async Task MaterialDeclarationIsBoundToBytesAndLocalActorAndSurvivesRestart()
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        var first=await MaterialStore(db,guard).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource);
        Assert.True(first.IsDeclared); Assert.Equal(1,first.Revision);
        Assert.Equal(first,await MaterialStore(db,guard).ReadAsync("material-version"));
        Assert.Equal(64L,await MaterialSql(db,"SELECT length(ActorSha256) FROM WorkflowMaterialPolicyChanges"));
        Assert.NotEqual("owned fixture operator",await MaterialSql(db,"SELECT ActorSha256 FROM WorkflowMaterialPolicyChanges"));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>MaterialStore(db,guard).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PublicSource));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>MaterialStore(db,guard).DeclareAsync("material-version",MaterialBlob.Replace('a','b'),1,DataClassification.PublicSource));
        Assert.Equal(first,await MaterialStore(db,guard).ReadAsync("material-version"));
        Assert.Equal(1L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowMaterialPolicyChanges"));
    }
    [Fact]
    public async Task MaterialAuditFailureRollsBackDeclarationAndRevision()
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        await MaterialSql(db,"CREATE TRIGGER FixtureFailMaterialAudit BEFORE INSERT ON WorkflowMaterialPolicyChanges BEGIN SELECT RAISE(ABORT,'owned audit fault'); END");
        await Assert.ThrowsAsync<SqliteException>(()=>MaterialStore(db,guard).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource));
        Assert.False((await MaterialStore(db,guard).ReadAsync("material-version")).IsDeclared);
        Assert.Equal(0L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowMaterialPolicies"));
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public async Task UnavailableLocalActorCannotDeclareMaterial(string? actor)
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>MaterialStore(db,guard,actor).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PublicSource));
        Assert.Equal(0L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowMaterialPolicies"));
    }
    [Fact]
    public async Task SecondaryCanReadButCannotDeclareOrPrepareMaterial()
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var primary=new ApplicationInstanceGuard(db.Root);
        using var secondary=new ApplicationInstanceGuard(db.Root); Assert.True(secondary.IsViewOnly);
        Assert.False((await MaterialStore(db,secondary).ReadAsync("material-version")).IsDeclared);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>MaterialStore(db,secondary).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PublicSource));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>MaterialGate(db,secondary).PrepareAsync(MaterialRequest(),default));
    }
    [Fact]
    public async Task RealSqlPreparedPromptIsConsumedOnceByRealInvokerWithSyntheticLifecycle()
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        await MaterialStore(db,guard).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource);
        var policy=MaterialGate(db,guard); var request=MaterialRequest(); request=request with { AdmissionId=await policy.PrepareAsync(request,default) };
        var lifecycle=new FakeOpenCodeSessionLifecycleService { TurnResult=CreateTurnResult() }; var client=new FakeOpenCodeClient();
        var invoker=MaterialInvoker(db,policy,lifecycle,client);
        await invoker.InvokeModelAsync(request);
        Assert.Null(Assert.Single(lifecycle.CreateRequests).Directory); // Saved policy root is not asserted as native cwd.
        Assert.Equal("plan",lifecycle.CreateRequests[0].Agent);
        Assert.Equal("plan",Assert.Single(lifecycle.TurnRequests).Request.Agent); // Requested mode only; no native permission proof.
        Assert.Equal(AdaptationPromptEnvelope.Format(request),Assert.Single(lifecycle.TurnRequests).Request.Prompt);
        Assert.Equal("PromptPreflight",await MaterialSql(db,"SELECT State FROM WorkflowAdaptationPromptAdmissions"));
        Assert.Equal(3L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPolicyChecks"));
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>invoker.InvokeModelAsync(request));
        Assert.Single(lifecycle.CreateRequests); Assert.Single(lifecycle.TurnRequests);
    }
    [Theory]
    [InlineData("project-class")] [InlineData("material-class")] [InlineData("material-revision")]
    [InlineData("profile-disabled")] [InlineData("profile-max")] [InlineData("profile-name")]
    [InlineData("account-auth")] [InlineData("model-capability")] [InlineData("route-max")] [InlineData("root")]
    [InlineData("prompt")] [InlineData("admission")] [InlineData("project")] [InlineData("version")]
    public async Task CurrentSqlOrExactPromptChangeAfterPreparationRefusesBeforeCreate(string change)
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        var store=MaterialStore(db,guard); await store.DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource);
        var policy=MaterialGate(db,guard); var request=MaterialRequest(); request=request with { AdmissionId=await policy.PrepareAsync(request,default) };
        if(change.StartsWith("material-")) await store.DeclareAsync("material-version",MaterialBlob,1,
            change=="material-class" ? DataClassification.Restricted : DataClassification.PrivateSource);
        else if(change=="prompt") request=new(request.RouteId,request.ModelId,"Replacement unprepared system text",request.Messages)
            { AdmissionId=request.AdmissionId,ProjectId=request.ProjectId,SourceVersionId=request.SourceVersionId };
        else if(change=="admission") request=request with { AdmissionId=Guid.NewGuid() };
        else if(change=="project") request=request with { ProjectId="foreign" };
        else if(change=="version") request=request with { SourceVersionId="foreign" };
        else await MaterialSql(db,change switch
        {
            "project-class"=>"UPDATE Projects SET DataClassification='Restricted'",
            "profile-disabled"=>"UPDATE ProviderProfiles SET IsEnabled=0",
            "profile-max"=>"UPDATE ProviderProfiles SET MaxDataClass='PublicSource'",
            "profile-name"=>"UPDATE ProviderProfiles SET DisplayName='Changed current policy'",
            "account-auth"=>"UPDATE Accounts SET AuthState='Unknown'",
            "model-capability"=>"UPDATE Models SET CapabilityState='Unknown'",
            "route-max"=>"UPDATE Routes SET MaxDataClass='PublicSource'",
            _=>"UPDATE Projects SET RootPath='D:.'"
        });
        var lifecycle=new FakeOpenCodeSessionLifecycleService { TurnResult=CreateTurnResult() }; var client=new FakeOpenCodeClient();
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>MaterialInvoker(db,policy,lifecycle,client).InvokeModelAsync(request));
        Assert.Empty(lifecycle.CreateRequests); Assert.Empty(lifecycle.TurnRequests); Assert.Empty(client.AbortedSessions);
        Assert.Equal("Prepared",await MaterialSql(db,"SELECT State FROM WorkflowAdaptationPromptAdmissions"));
    }

    private sealed class MaterialClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task StoredRouteClassificationCannotBeReplacedWithUnknownEnum()
    {
        using var db=new TestDatabase(); await SeedMaterial(db);
        await Assert.ThrowsAsync<SqliteException>(()=>MaterialSql(db,"UPDATE Routes SET MaxDataClass='999'"));
        Assert.Equal("PrivateSource",await MaterialSql(db,"SELECT MaxDataClass FROM Routes"));
    }

    [Fact]
    public async Task ExpiredPreparationRefusesWithoutCreatingSession()
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        await MaterialStore(db,guard).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource);
        var clock=new MaterialClock(); var policy=new SqliteAdaptationEgressPolicy(db.Factory,guard,new WorkflowSecretScanner(),clock);
        var request=MaterialRequest(); request=request with { AdmissionId=await policy.PrepareAsync(request,default) };
        clock.Now+=TimeSpan.FromMinutes(5);
        var lifecycle=new FakeOpenCodeSessionLifecycleService();
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>MaterialInvoker(db,policy,lifecycle,new FakeOpenCodeClient()).InvokeModelAsync(request));
        Assert.Empty(lifecycle.CreateRequests); Assert.Equal("Prepared",await MaterialSql(db,"SELECT State FROM WorkflowAdaptationPromptAdmissions"));
        Assert.Equal(1L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPolicyChecks"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PreflightAuditFailureRollsBackAdmissionCreationOrConsumption(bool consume)
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        await MaterialStore(db,guard).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource);
        var policy=MaterialGate(db,guard); var request=MaterialRequest();
        if(consume) request=request with { AdmissionId=await policy.PrepareAsync(request,default) };
        await MaterialSql(db,"CREATE TRIGGER FixtureFailPreflightAudit BEFORE INSERT ON WorkflowAdaptationPolicyChecks BEGIN SELECT RAISE(ABORT,'owned audit fault'); END");
        if(consume)
        {
            var lifecycle=new FakeOpenCodeSessionLifecycleService();
            await Assert.ThrowsAsync<SqliteException>(()=>MaterialInvoker(db,policy,lifecycle,new FakeOpenCodeClient()).InvokeModelAsync(request));
            Assert.Empty(lifecycle.CreateRequests);
            Assert.Equal("Prepared",await MaterialSql(db,"SELECT State FROM WorkflowAdaptationPromptAdmissions"));
        }
        else await Assert.ThrowsAsync<SqliteException>(()=>policy.PrepareAsync(request,default));
        Assert.Equal(consume ? 1L : 0L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPromptAdmissions"));
        Assert.Equal(consume ? 1L : 0L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPolicyChecks"));
    }

    [Fact]
    public async Task ConcurrentConsumptionOfOnePreparationHasExactlyOneWinner()
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        await MaterialStore(db,guard).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource);
        var policy=MaterialGate(db,guard); var request=MaterialRequest(); request=request with { AdmissionId=await policy.PrepareAsync(request,default) };
        Assert.True(AdaptationRouteIdentity.TryParse(request.RouteId,out var identity));
        using var barrier=new Barrier(2);
        async Task<bool> Consume()
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            try { await policy.ValidateAsync(request,identity,AdaptationPromptEnvelope.Format(request),null,default); return true; }
            catch(WorkflowValidationException) { return false; }
        }
        var outcomes=await Task.WhenAll(Task.Run(Consume),Task.Run(Consume));
        Assert.Single(outcomes.Where(x=>x)); Assert.Single(outcomes.Where(x=>!x));
        Assert.Equal("CreatePreflight",await MaterialSql(db,"SELECT State FROM WorkflowAdaptationPromptAdmissions"));
        Assert.Equal(2L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPolicyChecks"));
    }
    [Fact]
    public async Task MaterialPolicyChangeDuringCreateRefusesPromptAndAbortsOnlyCreatedSession()
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        var store=MaterialStore(db,guard); await store.DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource);
        var policy=MaterialGate(db,guard); var request=MaterialRequest(); request=request with { AdmissionId=await policy.PrepareAsync(request,default) };
        var lifecycle=new FakeOpenCodeSessionLifecycleService { TurnResult=CreateTurnResult(),
            AfterCreate=async ()=>await store.DeclareAsync("material-version",MaterialBlob,1,DataClassification.Restricted) };
        var client=new FakeOpenCodeClient();
        await Assert.ThrowsAsync<WorkflowValidationException>(()=>MaterialInvoker(db,policy,lifecycle,client).InvokeModelAsync(request));
        Assert.Single(lifecycle.CreateRequests); Assert.Empty(lifecycle.TurnRequests);
        Assert.Equal(lifecycle.Session.Id,Assert.Single(client.AbortedSessions));
    }
    [Theory]
    [InlineData("system-secret")] [InlineData("history-secret")] [InlineData("unicode")] [InlineData("size")]
    public async Task FullFormattedPromptIsScannedAndBoundedIncludingFollowUpHistory(string change)
    {
        using var db=new TestDatabase(); await SeedMaterial(db); using var guard=new ApplicationInstanceGuard(db.Root);
        await MaterialStore(db,guard).DeclareAsync("material-version",MaterialBlob,0,DataClassification.PrivateSource);
        var secret="api_key = sk-0123456789abcdef0123456789abcdef";
        var request=MaterialRequest();
        request=change switch
        {
            "system-secret"=>new(request.RouteId,request.ModelId,secret,request.Messages),
            "history-secret"=>new(request.RouteId,request.ModelId,request.SystemPrompt,[new("user","first"),new("assistant","answer"),new("user",secret)]),
            "unicode"=>new(request.RouteId,request.ModelId,"bad\ud800",request.Messages),
            _=>new(request.RouteId,request.ModelId,request.SystemPrompt,[new("user",new string('x',200000))])
        };
        request=request with { ProjectId="project-1",SourceVersionId="material-version" };
        var error=await Assert.ThrowsAsync<WorkflowValidationException>(()=>MaterialGate(db,guard).PrepareAsync(request,default));
        Assert.DoesNotContain(secret,error.Message); Assert.Equal(0L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPolicyChecks"));
        Assert.Equal(0L,await MaterialSql(db,"SELECT COUNT(*) FROM WorkflowAdaptationPromptAdmissions"));
    }
}
