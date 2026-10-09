using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>Current stored origin/policy and full-prompt scan before adaptation lifecycle calls.
/// This does not replace a final HTTP boundary or native execution ownership journal.</summary>
public sealed class SqliteAdaptationEgressPolicy(ISqliteConnectionFactory factory, IApplicationInstanceGuard guard,
    IWorkflowSecretScanner scanner, TimeProvider clock) : IAdaptationEgressPolicy
{
    public Task<Guid> PrepareAsync(AdaptationModelRequest request, CancellationToken cancellationToken)
        => PrepareCoreAsync(request,cancellationToken);
    private async Task<Guid> PrepareCoreAsync(AdaptationModelRequest request, CancellationToken token)
    {
        if (!AdaptationRouteIdentity.TryParse(request.RouteId,out var identity)) throw Refused();
        var id = Guid.NewGuid();
        await CheckAsync(request with { AdmissionId=id },identity,AdaptationPromptEnvelope.Format(request),null,true,token).ConfigureAwait(false);
        return id;
    }
    public async Task<AdaptationPolicySnapshot> ValidateAsync(AdaptationModelRequest request, AdaptationRouteIdentity identity,
        string exactPrompt, string? expectedFingerprint, CancellationToken token)
        => await CheckAsync(request,identity,exactPrompt,expectedFingerprint,false,token).ConfigureAwait(false);
    private async Task<AdaptationPolicySnapshot> CheckAsync(AdaptationModelRequest request, AdaptationRouteIdentity identity,
        string exactPrompt, string? expectedFingerprint, bool preparing, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        if (string.IsNullOrWhiteSpace(request.ProjectId) || string.IsNullOrWhiteSpace(request.SourceVersionId)
            || request.AdmissionId is null || identity.Backend != BackendType.OpenCode || request.ModelId != identity.BackendModelId) throw Refused();
        byte[] bytes;
        try { bytes = new UTF8Encoding(false,true).GetBytes(exactPrompt); }
        catch (EncoderFallbackException) { throw Refused(); }
        if (bytes.Length == 0 || bytes.Length > EgressApprovalService.MaxPayloadBytes) throw Refused();
        var scan = await scanner.ScanFilesAsync(new Dictionary<string,byte[]> { ["adaptation.txt"] = bytes },token).ConfigureAwait(false);
        if (scan.HasFindings || scan.Findings.Count != 0 || new SensitiveDataFilter().ContainsSensitiveData(exactPrompt)) throw Refused();
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred:false);
        string? ownedExecution = null;
        await using (var owned = connection.CreateCommand())
        {
            owned.Transaction=transaction; owned.CommandText="SELECT ExecutionId FROM WorkflowAdaptationNativeBindings WHERE AdmissionId=$id AND PrimaryInstanceId=$actor";
            owned.Parameters.AddWithValue("$id",request.AdmissionId.Value.ToString("D")); owned.Parameters.AddWithValue("$actor",guard.InstanceId);
            ownedExecution=await owned.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        }
        var promptHash=Convert.ToHexString(SHA256.HashData(bytes));
        var current=await ReadCurrentAsync(connection,transaction,request.ProjectId,request.SourceVersionId,identity,promptHash,
            clock.GetUtcNow(),ownedExecution,token).ConfigureAwait(false);
        var material=current.Material; var project=current.Rows.Project; var root=current.Root;
        var route=current.Rows.Route.Id; var classification=current.Classification; var fingerprint=current.Fingerprint;
        if (expectedFingerprint is not null && expectedFingerprint != fingerprint) throw Refused();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            if (preparing)
            {
                command.CommandText = """
                    INSERT INTO WorkflowAdaptationPromptAdmissions(Id,ProjectId,VersionId,RouteId,PromptSha256,PolicySha256,
                        PrimaryInstanceId,State,ExpiresAtUtc)
                    SELECT $id,$project,$version,$route,$prompt,$policy,$actor,'Prepared',$expires
                    WHERE (SELECT COUNT(*) FROM WorkflowAdaptationPromptAdmissions WHERE State='Prepared'
                        AND julianday(ExpiresAtUtc)>julianday($now))<128
                    """;
                command.Parameters.AddWithValue("$expires",(clock.GetUtcNow()+TimeSpan.FromMinutes(5)).ToString("O"));
            }
            else
            {
                command.CommandText = """
                    UPDATE WorkflowAdaptationPromptAdmissions SET State=$next WHERE Id=$id AND ProjectId=$project AND VersionId=$version
                        AND RouteId=$route AND PromptSha256=$prompt AND PolicySha256=$policy AND PrimaryInstanceId=$actor
                        AND State=$previous AND julianday(ExpiresAtUtc)>julianday($now)
                    """;
                command.Parameters.AddWithValue("$previous",expectedFingerprint is null ? "Prepared" : "CreatePreflight");
                command.Parameters.AddWithValue("$next",expectedFingerprint is null ? "CreatePreflight" : "PromptPreflight");
            }
            command.Parameters.AddWithValue("$id",request.AdmissionId.Value.ToString("D"));
            command.Parameters.AddWithValue("$project",project.Id); command.Parameters.AddWithValue("$version",material.VersionId);
            command.Parameters.AddWithValue("$route",route); command.Parameters.AddWithValue("$prompt",promptHash);
            command.Parameters.AddWithValue("$policy",fingerprint); command.Parameters.AddWithValue("$actor",guard.InstanceId);
            command.Parameters.AddWithValue("$now",clock.GetUtcNow().ToString("O"));
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw Refused();
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = """
                INSERT INTO WorkflowAdaptationPolicyChecks(Id,ProjectId,VersionId,RouteId,SourceBlobId,MaterialPolicyRevision,
                    Classification,PromptSha256,PolicySha256,PrimaryInstanceId,OccurredAtUtc)
                VALUES($id,$project,$version,$route,$blob,$revision,$class,$prompt,$policy,$actor,$time)
                """;
            command.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D")); command.Parameters.AddWithValue("$project",project.Id);
            command.Parameters.AddWithValue("$version",material.VersionId); command.Parameters.AddWithValue("$route",route);
            command.Parameters.AddWithValue("$blob",material.BlobId); command.Parameters.AddWithValue("$revision",material.Revision);
            command.Parameters.AddWithValue("$class",classification.ToString()); command.Parameters.AddWithValue("$prompt",promptHash);
            command.Parameters.AddWithValue("$policy",fingerprint); command.Parameters.AddWithValue("$actor",guard.InstanceId);
            command.Parameters.AddWithValue("$time",clock.GetUtcNow().ToString("O"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        guard.EnsureSupervisorPermitted(); token.ThrowIfCancellationRequested(); await transaction.CommitAsync(token).ConfigureAwait(false);
        return new(fingerprint,root);
    }

    internal sealed record CurrentPolicy(EgressPolicyRows Rows,WorkflowMaterialPolicy Material,string Root,
        string Fingerprint,DataClassification Classification);

    internal static async Task<CurrentPolicy> ReadCurrentAsync(SqliteConnection connection,SqliteTransaction transaction,
        string projectId,string versionId,AdaptationRouteIdentity identity,string promptHash,DateTimeOffset now,
        string? ownedExecution,CancellationToken token)
    {
        var material = await SqliteWorkflowMaterialPolicyStore.ReadAsync(connection,transaction,versionId,token).ConfigureAwait(false);
        if (!material.IsDeclared || material.Classification == DataClassification.Restricted) throw Refused();
        Project project;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = $"SELECT {SqliteProjectRepository.SelectColumns} FROM Projects WHERE Id=$project";
            command.Parameters.AddWithValue("$project",projectId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw Refused(); project = SqliteProjectRepository.Read(reader);
        }
        if (!Path.IsPathFullyQualified(project.RootPath) || !Directory.Exists(project.RootPath)
            || project.DataClassification == DataClassification.Restricted) throw Refused();
        var root = ProjectLock.CanonicalizeRoot(project.RootPath); string route;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = NativeGatewayRouteQuery.Sql.Replace("'NativeGateway'","'OpenCode'",StringComparison.Ordinal)
                + " AND r.ProviderProfileId=$profile AND r.AccountId=$account AND m.ProviderModelId=$model";
            if (ownedExecution is not null)
            {
                command.CommandText=command.CommandText.Replace("WHERE s.AccountId=a.Id AND (","WHERE s.AccountId=a.Id AND e.Id!=$owned AND (",StringComparison.Ordinal);
                command.Parameters.AddWithValue("$owned",ownedExecution);
            }
            command.Parameters.AddWithValue("$project",project.Id); command.Parameters.AddWithValue("$root",root);
            command.Parameters.AddWithValue("$now",now.ToString("O"));
            command.Parameters.AddWithValue("$profile",identity.ProviderProfileId); command.Parameters.AddWithValue("$account",identity.AccountId);
            command.Parameters.AddWithValue("$model",identity.BackendModelId!);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw Refused(); route = reader.GetString(5);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) throw Refused();
        }
        var rows = await SqliteEgressPolicyReader.ReadAsync(connection,transaction,project.Id,route,token).ConfigureAwait(false);
        var classification = (DataClassification)Math.Max((int)project.DataClassification,(int)material.Classification);
        if (!Enum.IsDefined(classification) || !Enum.IsDefined(rows.Route.MaxDataClass) || classification > rows.Route.MaxDataClass
            || !ProviderDataPolicy.Evaluate(classification,identity.ProviderProfileId,rows.Profile,BackendType.OpenCode).IsAllowed) throw Refused();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            currentPolicy = rows.Fingerprint, material.VersionId, material.BlobId, material.Classification, material.Revision, promptHash }))));
        return new(rows,material,root,fingerprint,classification);
    }
    private static WorkflowValidationException Refused() => new("Адаптация не передана: сохранённые материалы, проект, маршрут или текущая политика данных не подтверждены; секреты и Restricted запрещены.");
}
