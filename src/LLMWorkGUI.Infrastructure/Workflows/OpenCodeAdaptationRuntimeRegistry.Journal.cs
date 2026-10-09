using LLMWorkGUI.Application.Workflows;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Workflows;

public sealed partial class OpenCodeAdaptationRuntimeRegistry
{
    private sealed record Admission(string Project, string Version, string Route, string PromptHash,
        string Fingerprint, DateTimeOffset Expires);

    private async Task<string> ReadMappingAsync(string account, string provider, CancellationToken token)
    {
        await using var c = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = c.CreateCommand();
        command.CommandText = """
            SELECT m.SecretReference FROM OpenCodeAdaptationAccountMappings m JOIN Accounts a ON a.Id=m.AccountId
            JOIN ProviderProfiles p ON p.Id=a.ProviderProfileId JOIN SecretReferences k ON k.Reference=m.SecretReference
            WHERE m.AccountId=$account AND m.NativeProviderId=$provider AND a.SecretReference=m.SecretReference
              AND p.Backend='OpenCode' AND k.Kind='ProviderApiKey' AND k.State='Active'
            """;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$provider", provider);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string ?? throw Refused();
    }

    private async Task ReserveAsync(Lease lease, CancellationToken token)
    {
        RequireLive(lease); guard.EnsureSupervisorPermitted();
        await CheckCredentialAsync(lease, token).ConfigureAwait(false);
        if (FileHash(lease.Executable) != lease.ExecutableHash) throw Refused();
        await using var c = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var tx = c.BeginTransaction(deferred: false);
        var admission = await ReadAdmissionAsync(c, tx, lease, token).ConfigureAwait(false);
        var current = await SqliteAdaptationEgressPolicy.ReadCurrentAsync(c, tx, admission.Project, admission.Version,
            lease.Identity, admission.PromptHash, clock.GetUtcNow(), null, token).ConfigureAwait(false);
        if (current.Rows.Route.Id != admission.Route || current.Fingerprint != admission.Fingerprint
            || current.Fingerprint != lease.Fingerprint) throw Refused();
        await using (var selected = Command(c, tx, """
            SELECT COUNT(*) FROM Accounts a JOIN OpenCodeAdaptationAccountMappings m ON m.AccountId=a.Id
            JOIN SecretReferences k ON k.Reference=m.SecretReference
            WHERE a.Id=$account AND a.ProviderProfileId=$profile AND a.SecretReference=$reference
              AND m.SecretReference=$reference AND m.NativeProviderId=$provider AND k.Kind='ProviderApiKey' AND k.State='Active'
            """, ("$account", lease.Identity.AccountId), ("$profile", lease.Identity.ProviderProfileId), ("$reference", lease.Reference),
            ("$provider", lease.Identity.BackendModelId![..lease.Identity.BackendModelId!.IndexOf('/')])))
            if (Convert.ToInt64(await selected.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1) throw Refused();
        var selectedExecutable = current.Rows.Profile.ExecutablePath ?? options.Value.CustomExecutablePath;
        if (selectedExecutable is null || Path.GetFullPath(selectedExecutable) != lease.Executable) throw Refused();
        var now = clock.GetUtcNow().ToString("O");
        await ExecuteAsync(c, tx, """
            INSERT INTO Sessions(Id,ProjectId,Backend,ProviderProfileId,AccountId,ModelId,WorkspaceRootPath,
                State,ReconciliationOutcome,CloseReason,Role,ActiveExecutionId,CreatedAtUtc,LastEventAtUtc)
            VALUES($session,$project,'OpenCode',$profile,$account,$model,$root,'Ambiguous','Ambiguous','None',
                'workflow-adaptation',$execution,$now,$now);
            INSERT INTO Executions(Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,ProcessState,CreatedAtUtc,StartedAtUtc)
            VALUES($execution,$session,$request,'Ambiguous','None',$route,'NativeLaunchReserved',$now,$now);
            INSERT INTO WorkflowAdaptationRuntimeOwners(AdmissionId,SessionId,ExecutionId,NativeExecutionId,PrimaryInstanceId,
                WorkingDirectory,ConfigurationSha256,ExecutableSha256,CredentialReferenceSha256,NativeProviderId,State)
            VALUES($id,$session,$execution,$native,$actor,$cwd,$config,$exe,$reference,$provider,'Reserved');
            """, token, ("$session", lease.SessionId), ("$project", admission.Project),
            ("$profile", lease.Identity.ProviderProfileId), ("$account", lease.Identity.AccountId),
            ("$model", current.Rows.Route.Binding.ModelId), ("$root", current.Root), ("$execution", lease.ExecutionId),
            ("$now", now), ("$request", "adaptation:" + lease.Admission.ToString("D")), ("$route", admission.Route),
            ("$id", lease.Admission.ToString("D")), ("$native", lease.Runtime.NativeExecutionId), ("$actor", guard.InstanceId),
            ("$cwd", lease.Runtime.WorkingDirectory), ("$config", lease.Runtime.ConfigurationSha256),
            ("$exe", lease.ExecutableHash), ("$reference", Hash(lease.Reference)),
            ("$provider", lease.Identity.BackendModelId![..lease.Identity.BackendModelId!.IndexOf('/')])).ConfigureAwait(false);
        await AuditAsync(c, tx, lease, "Reserve", token).ConfigureAwait(false);
        await CommitAsync(tx, token, admission.Expires).ConfigureAwait(false);
    }

    private async Task MarkReadyAsync(Lease lease, CancellationToken token)
    {
        RequireLive(lease); guard.EnsureSupervisorPermitted();
        var endpoint = lease.Runtime.BaseUrl ?? throw Refused();
        if (!endpoint.IsLoopback || endpoint.Scheme != "http" || endpoint.UserInfo.Length != 0) throw Refused();
        await CheckCredentialAsync(lease, token).ConfigureAwait(false);
        await using var c = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var tx = c.BeginTransaction(deferred: false);
        var a = await ReadAdmissionAsync(c, tx, lease, token).ConfigureAwait(false);
        await RequireOwnerAsync(c, tx, lease, "Reserved", token).ConfigureAwait(false);
        await AuditAsync(c, tx, lease, "Ready", token).ConfigureAwait(false);
        if (await ExecuteAsync(c, tx, "UPDATE WorkflowAdaptationRuntimeOwners SET State='Ready',EndpointOrigin=$origin WHERE AdmissionId=$id AND State='Reserved'",
            token, ("$origin", endpoint.GetLeftPart(UriPartial.Authority)), ("$id", lease.Admission.ToString("D"))).ConfigureAwait(false) != 1) throw Refused();
        await CommitAsync(tx, token, a.Expires).ConfigureAwait(false);
    }

    private async Task ReleaseAsync(Lease lease)
    {
        RequireLive(lease);
        if (!IsPhysicallyStopped(lease)) throw Refused();
        guard.EnsureSupervisorPermitted();
        await using var c = await factory.OpenConnectionAsync().ConfigureAwait(false);
        using var tx = c.BeginTransaction(deferred: false);
        // A callback can fail before reserve or after an uncertain commit acknowledgement. Inspect the exact immutable owner.
        await using (var count = Command(c, tx, "SELECT COUNT(*) FROM WorkflowAdaptationRuntimeOwners WHERE AdmissionId=$id", ("$id", lease.Admission.ToString("D"))))
            if (Convert.ToInt64(await count.ExecuteScalarAsync().ConfigureAwait(false)) == 0) return;
        await RequireOwnerAsync(c, tx, lease, null, CancellationToken.None).ConfigureAwait(false);
        await using (var already = Command(c, tx, "SELECT State FROM WorkflowAdaptationRuntimeOwners WHERE AdmissionId=$id", ("$id", lease.Admission.ToString("D"))))
            if (await already.ExecuteScalarAsync().ConfigureAwait(false) as string == "Terminated") return;
        await AuditAsync(c, tx, lease, "Stopped", CancellationToken.None).ConfigureAwait(false);
        await ExecuteAsync(c, tx, """
            UPDATE WorkflowAdaptationRuntimeOwners SET State='Terminated' WHERE AdmissionId=$id;
            UPDATE Executions SET State=$terminal,EndedAtUtc=$now,ProcessState='OwnedRuntimeStopped',TerminationReason='OwnedRuntimeStopped'
                WHERE Id=$execution AND SessionId=$session AND State='Ambiguous' AND EndedAtUtc IS NULL;
            UPDATE Sessions SET State='Closed',ActiveExecutionId=NULL,LastEventAtUtc=$now WHERE Id=$session AND ActiveExecutionId=$execution;
            """, CancellationToken.None, ("$id", lease.Admission.ToString("D")), ("$now", clock.GetUtcNow().ToString("O")),
            ("$execution", lease.ExecutionId), ("$session", lease.SessionId), ("$terminal", lease.TerminalState)).ConfigureAwait(false);
        await CommitAsync(tx, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Admission> ReadAdmissionAsync(SqliteConnection c, SqliteTransaction tx, Lease lease, CancellationToken token)
    {
        await using var command = Command(c, tx, """
            SELECT a.ProjectId,a.VersionId,a.RouteId,a.PromptSha256,a.PolicySha256,a.ExpiresAtUtc
            FROM WorkflowAdaptationPromptAdmissions a JOIN Routes r ON r.Id=a.RouteId AND r.Backend='OpenCode'
            JOIN Models m ON m.Id=r.ModelId AND m.ProviderProfileId=r.ProviderProfileId AND m.Backend=r.Backend
            WHERE a.Id=$id AND a.State='CreatePreflight' AND a.PrimaryInstanceId=$actor
              AND r.AccountId=$account AND r.ProviderProfileId=$profile AND m.ProviderModelId=$model
            """, ("$id", lease.Admission.ToString("D")), ("$actor", guard.InstanceId),
            ("$account", lease.Identity.AccountId), ("$profile", lease.Identity.ProviderProfileId), ("$model", lease.Identity.BackendModelId));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || !DateTimeOffset.TryParse(reader.GetString(5), out var expires)
            || expires <= clock.GetUtcNow()) throw Refused();
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), expires);
    }

    private async Task RequireOwnerAsync(SqliteConnection c, SqliteTransaction tx, Lease lease, string? state, CancellationToken token)
    {
        RequireLive(lease);
        await using var command = Command(c, tx, """
            SELECT COUNT(*) FROM WorkflowAdaptationRuntimeOwners o JOIN Sessions s ON s.Id=o.SessionId
            JOIN Executions e ON e.Id=o.ExecutionId AND e.SessionId=s.Id
            WHERE o.AdmissionId=$id AND o.SessionId=$session AND o.ExecutionId=$execution
              AND o.NativeExecutionId=$native AND o.PrimaryInstanceId=$actor AND o.WorkingDirectory=$cwd
              AND o.ConfigurationSha256=$config AND o.ExecutableSha256=$exe AND o.CredentialReferenceSha256=$reference
              AND s.AccountId=$account AND s.ProviderProfileId=$profile AND s.Backend='OpenCode'
              AND ($state IS NULL OR (o.State=$state AND s.ActiveExecutionId=e.Id AND e.State='Ambiguous' AND e.EndedAtUtc IS NULL))
            """, ("$id", lease.Admission.ToString("D")), ("$session", lease.SessionId), ("$execution", lease.ExecutionId),
            ("$native", lease.Runtime.NativeExecutionId), ("$actor", guard.InstanceId), ("$cwd", lease.Runtime.WorkingDirectory),
            ("$config", lease.Runtime.ConfigurationSha256), ("$exe", lease.ExecutableHash), ("$reference", Hash(lease.Reference)),
            ("$account", lease.Identity.AccountId), ("$profile", lease.Identity.ProviderProfileId), ("$state", state));
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1) throw Refused();
    }

    private async Task AuditAsync(SqliteConnection c, SqliteTransaction tx, Lease lease, string phase, CancellationToken token)
        => await ExecuteAsync(c, tx, """
            INSERT INTO WorkflowAdaptationRuntimeChecks(Id,AdmissionId,NativeExecutionId,PrimaryInstanceId,Phase,OccurredAtUtc)
            VALUES($check,$id,$native,$actor,$phase,$now)
            """, token, ("$check", Guid.NewGuid().ToString("D")), ("$id", lease.Admission.ToString("D")),
            ("$native", lease.Runtime.NativeExecutionId), ("$actor", guard.InstanceId), ("$phase", phase), ("$now", clock.GetUtcNow().ToString("O"))).ConfigureAwait(false);
    private async Task CommitAsync(SqliteTransaction tx, CancellationToken token, DateTimeOffset? expires = null)
    {
        guard.EnsureSupervisorPermitted(); token.ThrowIfCancellationRequested();
        if (expires is { } value && value <= clock.GetUtcNow()) throw Refused();
        await tx.CommitAsync(token).ConfigureAwait(false);
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string Key, object? Value)[] values)
    {
        var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private static async Task<int> ExecuteAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token,
        params (string Key, object? Value)[] values)
    { await using var command = Command(c, tx, sql, values); return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
}
