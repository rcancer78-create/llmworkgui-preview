using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.CursorAcp;

public sealed partial class CursorAcpClient
{
    private const string NativePermissionPrefix = "acp-v1:";
    private readonly object _nativePermissionSync = new();
    private readonly Dictionary<string, NativePermission> _nativePermissions = new(StringComparer.Ordinal);
    // A consumed receipt still owns its exact wire ID during a write and after an uncertain
    // write. Pending receipts plus these claims share the same bounded ownership budget.
    private readonly HashSet<string> _permissionResponseIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelledPermissionSessions = new(StringComparer.Ordinal);

    private sealed record NativePermission(JsonElement RpcId, string SessionId, string? AllowOnce, string? RejectOnce,
        bool IsLegacy = false);

    private NormalizationResult NormalizeNativePermission(JsonRpcNotification notification, JsonElement parameters, string? sessionId)
    {
        // The wire ID (including number/string type) stays private to this client. The UI gets an
        // opaque handle, so neither option labels nor an arbitrary UI string can authorize a grant.
        if (sessionId is null || notification.Id is not { } id ||
            id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
            (id.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(id.GetString())) ||
            !HasUniqueFields(parameters) ||
            !parameters.TryGetProperty("toolCall", out var tool) || tool.ValueKind != JsonValueKind.Object ||
            !HasUniqueFields(tool) || ReadString(tool, "toolCallId") is not { } toolId ||
            !parameters.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array ||
            options.GetArrayLength() > 64)
            return NormalizationResult.Malformed;

        var optionIds = new HashSet<string>(StringComparer.Ordinal);
        string? allow = null, reject = null;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object || !HasUniqueFields(option) ||
                ReadString(option, "optionId") is not { } optionId || !optionIds.Add(optionId) ||
                ReadString(option, "name") is null || ReadString(option, "kind") is not { } kind)
                return NormalizationResult.Malformed;
            // Never map allow_always/reject_always to a one-shot decision. Ambiguous duplicate
            // kinds also refuse discovery rather than picking an option by list position.
            if (kind == "allow_once") { if (allow is not null) return NormalizationResult.Malformed; allow = optionId; }
            if (kind == "reject_once") { if (reject is not null) return NormalizationResult.Malformed; reject = optionId; }
        }

        var handle = NativePermissionPrefix + Guid.NewGuid().ToString("N");
        lock (_nativePermissionSync)
        {
            if (!CanCapturePermissionId(id))
                return NormalizationResult.Malformed;
            _nativePermissions.Add(handle, new NativePermission(id.Clone(), sessionId, allow, reject));
        }
        return NormalizationResult.Emitted(new CursorAcpStreamEvent.PermissionRequest
        {
            Method = notification.Method, SessionId = sessionId, RequestId = handle,
            Description = ReadString(tool, "title") ?? $"Tool call {toolId}",
            CanAllowOnce = allow is not null, RawPayload = parameters.Clone()
        });
    }

    private static bool HasUniqueFields(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().All(p => names.Add(p.Name));
    }

    // Called only while _nativePermissionSync is held. GetRawText preserves the ID's JSON type.
    private bool CanCapturePermissionId(JsonElement id) =>
        _nativePermissions.Count + _permissionResponseIds.Count < 128 &&
        !_permissionResponseIds.Contains(id.GetRawText()) &&
        !_nativePermissions.Values.Any(permission => permission.RpcId.GetRawText() == id.GetRawText());

    private async Task RejectMalformedPermissionAsync(JsonRpcNotification notification, CancellationToken cancellationToken)
    {
        if (notification.Method != "session/request_permission" || notification.Id is not { } id ||
            id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
            (id.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(id.GetString())))
            return;

        lock (_nativePermissionSync)
        {
            // Pending, writing, and uncertain replies retain sole ownership of this typed ID.
            if (!CanCapturePermissionId(id))
                return;
            _permissionResponseIds.Add(id.GetRawText());
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.RequestTimeout);
        // Invalid parameters are answered exactly once, without a permission result, payload alias,
        // or private request text. An uncertain write propagates failure and is never retried.
        await _transport.SendResponseAsync(id.Clone(), null,
            new JsonRpcError { Code = -32602, Message = "Invalid session/request_permission parameters." },
            budget.Token).ConfigureAwait(false);
        lock (_nativePermissionSync) _permissionResponseIds.Remove(id.GetRawText());
    }

    private bool IsCancelledNativePermission(CursorAcpStreamEvent.PermissionRequest request)
    {
        lock (_nativePermissionSync)
            return request.RequestId.StartsWith(NativePermissionPrefix, StringComparison.Ordinal) &&
                   request.SessionId is not null && _cancelledPermissionSessions.Contains(request.SessionId);
    }

    private async Task CancelNativePermissionsAsync(string sessionId, CancellationToken cancellationToken)
    {
        NativePermission[] permissions;
        lock (_nativePermissionSync)
        {
            _cancelledPermissionSessions.Add(sessionId);
            var pending = _nativePermissions.Where(p => p.Value.SessionId == sessionId).ToArray();
            permissions = pending.Select(p => p.Value).ToArray();
            // Retire every grant receipt before the first write. A failed first cancellation
            // must not leave another old receipt grantable when a new prompt starts.
            foreach (var item in pending)
            {
                _nativePermissions.Remove(item.Key);
                _permissionResponseIds.Add(item.Value.RpcId.GetRawText());
            }
        }
        foreach (var permission in permissions)
        {
            var result = await SendNativePermissionReplyAsync(permission, selected: null, cancellationToken).ConfigureAwait(false);
            if (!result.IsSent && result.FailureKind == CursorAcpPermissionReplyFailureKind.TransportFailure)
                throw new IOException(result.Blocker);
        }
    }

    private async Task<CursorAcpPermissionReplyResult> ReplyNativePermissionAsync(
        CursorAcpPermissionReplyRequest request, bool cancelled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativePermission permission;
        string? selected;
        lock (_nativePermissionSync)
        {
            if (!_nativePermissions.TryGetValue(request.PermissionId, out permission!) ||
                request.SessionId != permission.SessionId)
                return InvalidNativeReply("No matching pending native permission exists for this session.");
            cancelled |= _cancelledPermissionSessions.Contains(permission.SessionId);
            selected = cancelled ? null : request.Decision == CursorAcpPermissionDecision.AllowOnce ? permission.AllowOnce : permission.RejectOnce;
            if (!cancelled && request.Decision == CursorAcpPermissionDecision.AllowOnce && selected is null)
                return InvalidNativeReply("This native request offers no allow_once option.");
            cancellationToken.ThrowIfCancellationRequested();
            // Consume before writing: a failed/ambiguous write must never be retried as another grant.
            _nativePermissions.Remove(request.PermissionId);
            _permissionResponseIds.Add(permission.RpcId.GetRawText());
        }
        return await SendNativePermissionReplyAsync(permission, selected, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CursorAcpPermissionReplyResult> SendNativePermissionReplyAsync(
        NativePermission permission, string? selected, CancellationToken cancellationToken)
    {
        var result = permission.IsLegacy
            ? JsonSerializer.SerializeToElement(new { decision = selected ?? "deny" })
            : selected is null
            ? JsonSerializer.SerializeToElement(new { outcome = new { outcome = "cancelled" } })
            : JsonSerializer.SerializeToElement(new { outcome = new { outcome = "selected", optionId = selected } });
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.RequestTimeout);
        try
        {
            await _transport.SendResponseAsync(permission.RpcId, result, null, budget.Token).ConfigureAwait(false);
            lock (_nativePermissionSync) _permissionResponseIds.Remove(permission.RpcId.GetRawText());
            return CursorAcpPermissionReplyResult.Sent();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return CursorAcpPermissionReplyResult.Degraded(CursorAcpPermissionReplyFailureKind.TransportFailure,
                $"The native permission response could not be confirmed: {exception.Message}");
        }
    }

    private static CursorAcpPermissionReplyResult InvalidNativeReply(string reason) =>
        CursorAcpPermissionReplyResult.Degraded(CursorAcpPermissionReplyFailureKind.InvalidPermissionId, reason);
}
