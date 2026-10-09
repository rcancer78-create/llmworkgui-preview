using System.Security.Cryptography;
using System.Text;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Workflows;

public sealed class SqliteWorkflowMaterialPolicyStore(ISqliteConnectionFactory factory,
    IApplicationInstanceGuard guard, IUserApprovalIdentity identity, TimeProvider clock) : IWorkflowMaterialPolicyStore
{
    public async Task<WorkflowMaterialPolicy> ReadAsync(string versionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, null, versionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowMaterialPolicy> DeclareAsync(string versionId, string expectedBlobId, long expectedRevision,
        DataClassification classification, CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        if (!Enum.IsDefined(classification) || expectedRevision < 0) throw Refused();
        var actor = identity.GetCurrentApproverIdentity();
        if (string.IsNullOrWhiteSpace(actor)) throw Refused();
        var actorHash = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false,true).GetBytes(actor)));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred:false);
        var previous = await ReadAsync(connection, transaction, versionId, cancellationToken).ConfigureAwait(false);
        if (previous.BlobId != expectedBlobId || previous.Revision != expectedRevision) throw Refused();
        var revision = checked(expectedRevision+1); var now = clock.GetUtcNow().ToString("O");
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO WorkflowMaterialPolicies(VersionId,BlobId,Classification,Revision,DeclaredBySha256,DeclaredAtUtc)
            VALUES($version,$blob,$class,$revision,$actor,$now)
            ON CONFLICT(VersionId) DO UPDATE SET Classification=excluded.Classification,Revision=excluded.Revision,
                DeclaredBySha256=excluded.DeclaredBySha256,DeclaredAtUtc=excluded.DeclaredAtUtc;
            INSERT INTO WorkflowMaterialPolicyChanges(Id,VersionId,BlobId,Revision,PreviousClassification,Classification,ActorSha256,OccurredAtUtc)
            VALUES($id,$version,$blob,$revision,$previous,$class,$actor,$now);
            """;
        command.Parameters.AddWithValue("$version",versionId); command.Parameters.AddWithValue("$blob",expectedBlobId);
        command.Parameters.AddWithValue("$class",classification.ToString()); command.Parameters.AddWithValue("$revision",revision);
        command.Parameters.AddWithValue("$actor",actorHash); command.Parameters.AddWithValue("$now",now);
        command.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$previous",previous.IsDeclared ? previous.Classification.ToString() : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted(); cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(versionId,expectedBlobId,classification,revision,true);
    }

    internal static async Task<WorkflowMaterialPolicy> ReadAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string versionId, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT v.BlobId,p.BlobId,p.Classification,p.Revision,
                (SELECT COUNT(*) FROM WorkflowMaterialPolicyChanges c WHERE c.VersionId=p.VersionId AND c.BlobId=p.BlobId
                    AND c.Revision=p.Revision AND c.Classification=p.Classification AND c.ActorSha256=p.DeclaredBySha256)
            FROM WorkflowVersions v
            LEFT JOIN WorkflowMaterialPolicies p ON p.VersionId=v.Id WHERE v.Id=$version
            """;
        command.Parameters.AddWithValue("$version",versionId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw Refused();
        var blob = reader.GetString(0);
        if (reader.IsDBNull(1)) return new(versionId,blob,DataClassification.Restricted,0,false);
        if (reader.GetString(1) != blob || !Enum.TryParse<DataClassification>(reader.GetString(2),out var classification)
            || !Enum.IsDefined(classification) || reader.GetInt64(3) < 1 || reader.GetInt64(4) != 1) throw Refused();
        return new(versionId,blob,classification,reader.GetInt64(3),true);
    }
    private static WorkflowValidationException Refused() => new("Классификация материалов не сохранена или изменилась. Обновите версию и подтвердите её класс данных.");
}
