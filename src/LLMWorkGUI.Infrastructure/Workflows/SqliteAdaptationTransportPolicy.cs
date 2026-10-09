using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>Checks the actual serialized adaptation HTTP request and reserves uncertain ownership atomically.
/// Recorded directory, model and endpoint are requested/reported metadata, not native permission or origin proof.</summary>
public sealed class SqliteAdaptationTransportPolicy : IAdaptationTransportPolicy
{
    private readonly ISqliteConnectionFactory factory;
    private readonly IApplicationInstanceGuard guard;
    private readonly IWorkflowSecretScanner scanner;
    private readonly TimeProvider clock;
    private readonly OpenCodeAdaptationRuntimeRegistry? _runtimes;
    public SqliteAdaptationTransportPolicy(ISqliteConnectionFactory factory, IApplicationInstanceGuard guard,
        IWorkflowSecretScanner scanner, TimeProvider clock, OpenCodeAdaptationRuntimeRegistry runtimes)
        : this(factory, guard, scanner, clock)
    { ArgumentNullException.ThrowIfNull(runtimes); _runtimes = runtimes; }
    // Historical HTTP-only contract fixtures. Production composition always requires the sealed runtime registry.
    internal SqliteAdaptationTransportPolicy(ISqliteConnectionFactory factory, IApplicationInstanceGuard guard,
        IWorkflowSecretScanner scanner, TimeProvider clock)
    { this.factory = factory; this.guard = guard; this.scanner = scanner; this.clock = clock; }
    public const int MaxWireBytes = 200_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private sealed record Admission(string Id, string Project, string Version, string Route, string PromptHash,
        string PolicyHash, string Actor, string State, DateTimeOffset Expires, AdaptationRouteIdentity Identity);
    private sealed record Binding(string Admission, string Session, string Execution, string Origin, string Actor,
        string State, string QueryHash, string? NativeSession);

    public async Task AuthorizeCreateAsync(Guid admissionId, Uri requestUri, ReadOnlyMemory<byte> body, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        var origin = Origin(requestUri);
        var owned = _runtimes is null ? null : await _runtimes.RequireReadyAsync(admissionId, origin, token).ConfigureAwait(false);
        if (requestUri.AbsolutePath != "/session" || requestUri.Query != (owned?.Query ?? "")) throw Refused();
        using var json = Parse(body);
        ClosedObject(json.RootElement, "title", "model", "agent");
        var title = Text(json.RootElement, "title");
        if (!title.StartsWith("workflow-adaptation-", StringComparison.Ordinal)
            || !Guid.TryParseExact(title["workflow-adaptation-".Length..], "N", out _)) throw Refused();
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var admission = await ReadAdmissionAsync(connection, transaction, admissionId, token).ConfigureAwait(false);
        if (admission.State != "CreatePreflight"
            || await ReadBindingAsync(connection, transaction, admissionId, null, token).ConfigureAwait(false) is not null) throw Refused();
        ValidateModel(json.RootElement, admission.Identity.BackendModelId!, true);
        var current = await CurrentAsync(connection, transaction, admission, owned?.ExecutionId, token).ConfigureAwait(false);
        if (owned is null) CheckConfiguredOrigin(current.Rows.Profile.BaseUrl, origin);
        var session = "adapt-session-" + admissionId.ToString("N");
        var execution = "adapt-execution-" + admissionId.ToString("N");
        var now = clock.GetUtcNow().ToString("O");
        if (owned is null) await ExecuteAsync(connection, transaction, """
            INSERT INTO Sessions(Id,ProjectId,Backend,ProviderProfileId,AccountId,ModelId,WorkspaceRootPath,
                State,ReconciliationOutcome,CloseReason,Role,ActiveExecutionId,CreatedAtUtc,LastEventAtUtc)
            VALUES($session,$project,'OpenCode',$profile,$account,$model,$root,'Ambiguous','Ambiguous','None',
                'workflow-adaptation',$execution,$now,$now);
            INSERT INTO Executions(Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,ProcessState,CreatedAtUtc,StartedAtUtc)
            VALUES($execution,$session,$request,'Ambiguous','None',$route,'NativeCreateDeliveryUnconfirmed',$now,$now);
            """, token, ("$session", session), ("$project", admission.Project), ("$profile", admission.Identity.ProviderProfileId),
            ("$account", admission.Identity.AccountId), ("$model", current.Rows.Route.Binding.ModelId), ("$root", current.Root),
            ("$execution", execution), ("$now", now), ("$request", "adaptation:" + admission.Id), ("$route", admission.Route),
            ("$id", admission.Id), ("$origin", origin), ("$actor", guard.InstanceId), ("$query", Hash(""))).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO WorkflowAdaptationNativeBindings(AdmissionId,SessionId,ExecutionId,EndpointOrigin,PrimaryInstanceId,State,NativeDirectoryQuerySha256)
            VALUES($id,$session,$execution,$origin,$actor,'CreateAuthorized',$query);
            """, token, ("$id", admission.Id), ("$session", session), ("$execution", execution), ("$origin", origin),
            ("$actor", guard.InstanceId), ("$query", Hash(owned?.Query ?? ""))).ConfigureAwait(false);
        await AuditAsync(connection, transaction, new(admission.Id, session, execution, origin, guard.InstanceId,
            "CreateAuthorized", Hash(owned?.Query ?? ""), null), "Create", Hash(body.Span), requestUri, token).ConfigureAwait(false);
        await CommitAsync(transaction, token, admission.Expires, owned).ConfigureAwait(false);
    }

    public async Task BindCreatedAsync(Guid admissionId, string nativeSessionId, string? reportedDirectory, Uri requestUri, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted(); ValidateNativeId(nativeSessionId);
        var origin = Origin(requestUri);
        var owned = _runtimes is null ? null : await _runtimes.RequireReadyAsync(admissionId, origin, token).ConfigureAwait(false);
        if (requestUri.AbsolutePath != "/session" || requestUri.Query != (owned?.Query ?? "")) throw Refused();
        if (reportedDirectory is not null && (reportedDirectory.Length > 4096 || reportedDirectory.Any(char.IsControl))) throw Refused();
        if (owned is not null && (reportedDirectory is null || !Path.IsPathFullyQualified(reportedDirectory)
            || !string.Equals(Path.GetFullPath(reportedDirectory), owned.Runtime.WorkingDirectory, StringComparison.OrdinalIgnoreCase))) throw Refused();
        var query = owned?.Query ?? (string.IsNullOrWhiteSpace(reportedDirectory) ? "" : "?directory=" + Uri.EscapeDataString(reportedDirectory));
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var binding = await ReadBindingAsync(connection, transaction, admissionId, null, token).ConfigureAwait(false) ?? throw Refused();
        RequireOwner(binding, origin);
        if (binding.State != "CreateAuthorized" || binding.NativeSession is not null) throw Refused();
        await using (var command = Command(connection, transaction,
            "SELECT COUNT(*) FROM Sessions WHERE Backend='OpenCode' AND NativeSessionId=$native", ("$native", nativeSessionId)))
            if (Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0) throw Refused();
        // Ownership is recorded even if project classification changed after create. This authorizes no prompt.
        await ExecuteAsync(connection, transaction, """
            UPDATE Sessions SET NativeSessionId=$native,LastEventAtUtc=$now WHERE Id=$session AND NativeSessionId IS NULL;
            UPDATE WorkflowAdaptationNativeBindings SET State='Bound',NativeDirectoryQuerySha256=$query WHERE AdmissionId=$id AND State='CreateAuthorized';
            """, token, ("$native", nativeSessionId), ("$now", clock.GetUtcNow().ToString("O")), ("$session", binding.Session),
            ("$id", binding.Admission), ("$query", Hash(query))).ConfigureAwait(false);
        await AuditAsync(connection, transaction, binding, "Bind", Hash(nativeSessionId), requestUri, token).ConfigureAwait(false);
        await CommitAsync(transaction, token, owned: owned).ConfigureAwait(false);
    }

    public async Task AuthorizePromptAsync(Guid? admissionId, string nativeSessionId, Uri requestUri,
        ReadOnlyMemory<byte> body, CancellationToken token)
    {
        var owned = _runtimes is not null && admissionId is { } id
            ? await _runtimes.RequireReadyAsync(id, Origin(requestUri), token).ConfigureAwait(false) : null;
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var binding = await ReadBindingAsync(connection, transaction, admissionId, nativeSessionId, token).ConfigureAwait(false);
        if (admissionId is null)
        {
            // Only adaptation is covered here. Ordinary OpenCode workspace admission has its own journal.
            if (binding is not null) throw Refused();
            return;
        }
        guard.EnsureSupervisorPermitted();
        if (binding is null || binding.NativeSession != nativeSessionId || binding.State != "Bound") throw Refused();
        RequireOwner(binding, Origin(requestUri)); ValidateNativeId(nativeSessionId);
        if (requestUri.AbsolutePath != "/session/" + nativeSessionId + "/prompt_async" || Hash(requestUri.Query) != binding.QueryHash) throw Refused();
        using var json = Parse(body);
        ClosedObject(json.RootElement, "messageID", "model", "agent", "parts");
        var message = Text(json.RootElement, "messageID");
        if (!message.StartsWith("msg_", StringComparison.Ordinal) || !Guid.TryParseExact(message[4..], "N", out _)) throw Refused();
        var admission = await ReadAdmissionAsync(connection, transaction, admissionId.Value, token).ConfigureAwait(false);
        if (admission.State != "PromptPreflight") throw Refused();
        ValidateModel(json.RootElement, admission.Identity.BackendModelId!, false);
        var parts = json.RootElement.GetProperty("parts");
        if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 1) throw Refused();
        ClosedObject(parts[0], "type", "text");
        if (Text(parts[0], "type") != "text") throw Refused();
        var prompt = Text(parts[0], "text"); byte[] promptBytes;
        try { promptBytes = StrictUtf8.GetBytes(prompt); } catch (EncoderFallbackException) { throw Refused(); }
        if (Hash(promptBytes) != admission.PromptHash) throw Refused();
        var scan = await scanner.ScanFilesAsync(new Dictionary<string, byte[]> { ["adaptation.txt"] = promptBytes }, token).ConfigureAwait(false);
        if (scan.HasFindings || scan.Findings.Count != 0 || new SensitiveDataFilter().ContainsSensitiveData(prompt)) throw Refused();
        var current = await CurrentAsync(connection, transaction, admission, binding.Execution, token).ConfigureAwait(false);
        if (owned is null) CheckConfiguredOrigin(current.Rows.Profile.BaseUrl, binding.Origin);
        await ExecuteAsync(connection, transaction, """
            UPDATE WorkflowAdaptationNativeBindings SET State='PromptAuthorized' WHERE AdmissionId=$id AND State='Bound';
            UPDATE Executions SET ProcessState='NativePromptDeliveryUnconfirmed' WHERE Id=$execution;
            """, token, ("$id", binding.Admission), ("$execution", binding.Execution)).ConfigureAwait(false);
        await AuditAsync(connection, transaction, binding, "Prompt", Hash(body.Span), requestUri, token).ConfigureAwait(false);
        await CommitAsync(transaction, token, admission.Expires, owned).ConfigureAwait(false);
    }

    public async Task AuthorizeAbortAsync(string nativeSessionId, Uri requestUri, CancellationToken token)
    {
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var binding = await ReadBindingAsync(connection, transaction, null, nativeSessionId, token).ConfigureAwait(false);
        if (binding is null) return;
        if (_runtimes is not null) _runtimes.RequireAbortOwner(Guid.Parse(binding.Admission), Origin(requestUri));
        guard.EnsureSupervisorPermitted(); RequireOwner(binding, Origin(requestUri)); ValidateNativeId(nativeSessionId);
        if (binding.NativeSession != nativeSessionId || requestUri.AbsolutePath != "/session/" + nativeSessionId + "/abort"
            || Hash(requestUri.Query) != binding.QueryHash) throw Refused();
        // Cancellation remains available after a policy change. An acknowledgement cannot release the reservation.
        await AuditAsync(connection, transaction, binding, "Abort", Hash("{}"), requestUri, token).ConfigureAwait(false);
        await CommitAsync(transaction, token).ConfigureAwait(false);
    }

    private async Task<Admission> ReadAdmissionAsync(SqliteConnection c, SqliteTransaction tx, Guid id, CancellationToken token)
    {
        await using var command = Command(c, tx, """
            SELECT a.Id,a.ProjectId,a.VersionId,a.RouteId,a.PromptSha256,a.PolicySha256,a.PrimaryInstanceId,a.State,a.ExpiresAtUtc,
                r.AccountId,r.ProviderProfileId,m.ProviderModelId
            FROM WorkflowAdaptationPromptAdmissions a JOIN Routes r ON r.Id=a.RouteId AND r.Backend='OpenCode'
            JOIN Models m ON m.Id=r.ModelId AND m.ProviderProfileId=r.ProviderProfileId AND m.Backend=r.Backend WHERE a.Id=$id
            """, ("$id", id.ToString("D")));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)
            || !DateTimeOffset.TryParse(reader.GetString(8), out var expires) || expires <= clock.GetUtcNow()
            || reader.GetString(6) != guard.InstanceId) throw Refused();
        return new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),
            reader.GetString(6),reader.GetString(7),expires,new(reader.GetString(9),reader.GetString(10),BackendType.OpenCode,reader.GetString(11)));
    }

    private static async Task<Binding?> ReadBindingAsync(SqliteConnection c, SqliteTransaction tx, Guid? id, string? native, CancellationToken token)
    {
        await using var command = Command(c, tx, """
            SELECT b.AdmissionId,b.SessionId,b.ExecutionId,b.EndpointOrigin,b.PrimaryInstanceId,b.State,b.NativeDirectoryQuerySha256,s.NativeSessionId
            FROM WorkflowAdaptationNativeBindings b JOIN Sessions s ON s.Id=b.SessionId
            JOIN Executions e ON e.Id=b.ExecutionId AND e.SessionId=s.Id
            WHERE ($id IS NOT NULL AND b.AdmissionId=$id) OR ($id IS NULL AND s.NativeSessionId=$native)
            """, ("$id", id?.ToString("D")), ("$native", native));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var result = new Binding(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),
            reader.GetString(5),reader.GetString(6),reader.IsDBNull(7) ? null : reader.GetString(7));
        if (await reader.ReadAsync(token).ConfigureAwait(false)) throw Refused();
        return result;
    }

    private async Task<SqliteAdaptationEgressPolicy.CurrentPolicy> CurrentAsync(SqliteConnection c, SqliteTransaction tx,
        Admission a, string? execution, CancellationToken token)
    {
        var current = await SqliteAdaptationEgressPolicy.ReadCurrentAsync(c,tx,a.Project,a.Version,a.Identity,a.PromptHash,
            clock.GetUtcNow(),execution,token).ConfigureAwait(false);
        if (current.Rows.Route.Id != a.Route || current.Fingerprint != a.PolicyHash) throw Refused();
        return current;
    }

    private void RequireOwner(Binding b, string origin)
    { if (b.Actor != guard.InstanceId || b.Origin != origin) throw Refused(); }
    private async Task CommitAsync(SqliteTransaction tx, CancellationToken token, DateTimeOffset? expires = null,
        OpenCodeAdaptationRuntimeRegistry.Lease? owned = null)
    {
        guard.EnsureSupervisorPermitted(); token.ThrowIfCancellationRequested();
        if (expires is { } deadline && deadline <= clock.GetUtcNow()) throw Refused();
        if (owned is not null) _runtimes!.RequirePrivateReady(owned);
        await tx.CommitAsync(token).ConfigureAwait(false);
    }
    private async Task AuditAsync(SqliteConnection c, SqliteTransaction tx, Binding b, string phase, string payload, Uri uri, CancellationToken token)
        => await ExecuteAsync(c, tx, """
            INSERT INTO WorkflowAdaptationTransportChecks(Id,AdmissionId,ExecutionId,Phase,PayloadSha256,EndpointSha256,OccurredAtUtc)
            VALUES($id,$admission,$execution,$phase,$payload,$endpoint,$time)
            """, token, ("$id",Guid.NewGuid().ToString("D")),("$admission",b.Admission),("$execution",b.Execution),
            ("$phase",phase),("$payload",payload),("$endpoint",Hash(uri.AbsoluteUri)),("$time",clock.GetUtcNow().ToString("O"))).ConfigureAwait(false);
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string Key, object? Value)[] values)
    {
        var command=c.CreateCommand(); command.Transaction=tx; command.CommandText=sql;
        foreach (var (key,value) in values) command.Parameters.AddWithValue(key,value ?? DBNull.Value);
        return command;
    }
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token,
        params (string Key, object? Value)[] values)
    { await using var command=Command(c,tx,sql,values); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
    private static string Hash(string text) => Hash(StrictUtf8.GetBytes(text));
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Origin(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "http" || !uri.IsLoopback || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw Refused();
        return uri.GetLeftPart(UriPartial.Authority);
    }
    private static void CheckConfiguredOrigin(string? configured, string origin)
    {
        if (configured is not null && (!Uri.TryCreate(configured,UriKind.Absolute,out var uri)
            || Origin(uri) != origin || uri.AbsolutePath != "/" || uri.Query.Length != 0)) throw Refused();
    }
    private static void ValidateNativeId(string id)
    { if (id.Length is 0 or > 128 || id.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))) throw Refused(); }
    private static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxWireBytes) throw Refused();
        try { _ = StrictUtf8.GetString(bytes.Span); return JsonDocument.Parse(bytes); }
        catch (Exception e) when (e is JsonException or DecoderFallbackException) { throw Refused(); }
    }
    private static void ClosedObject(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Refused();
        var names=value.EnumerateObject().Select(p=>p.Name).ToArray();
        if (names.Length != keys.Length || names.Distinct(StringComparer.Ordinal).Count() != keys.Length
            || names.Any(n=>!keys.Contains(n,StringComparer.Ordinal))) throw Refused();
    }
    private static string Text(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key,out var item) || item.ValueKind != JsonValueKind.String) throw Refused();
        try { return item.GetString()!; }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw Refused(); }
    }
    private static void ValidateModel(JsonElement root, string model, bool create)
    {
        if (Text(root,"agent") != "plan") throw Refused();
        var descriptor=root.GetProperty("model"); var key=create ? "id" : "modelID"; var slash=model.IndexOf('/');
        if (slash > 0 && slash < model.Length-1)
        { ClosedObject(descriptor,"providerID",key); if (Text(descriptor,"providerID") != model[..slash] || Text(descriptor,key) != model[(slash+1)..]) throw Refused(); }
        else { ClosedObject(descriptor,key); if (Text(descriptor,key) != model) throw Refused(); }
    }
    private static WorkflowValidationException Refused() => new("Адаптация не отправлена: текущая политика, точное содержимое запроса или владение сессией не подтверждены. Незавершённая native-сессия сохраняет занятость аккаунта.");
}
