using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

namespace LLMWorkGUI.Backends.OpenCode.Sessions;

public sealed partial class OpenCodeSessionLifecycleService
{
    private const int PermissionHistoryLimit = 64;
    private const int PermissionDisplayLimit = 4096;

    public IReadOnlyList<OpenCodePendingPermission> GetPendingPermissions(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.ActiveTurn is not { } context
                || context.Completion.Task.IsCompleted) return Array.Empty<OpenCodePendingPermission>();
            var interactive = context.Phase != OpenCodeTurnPhase.Cancelling && !context.CancelSignal.IsCancellationRequested;
            return context.Permissions.Values.Where(p => !p.Resolved).Select(p => p.Snapshot with
            {
                CanAllowOnce = interactive && !p.ReplyAttempted && !p.Conflicting && p.ReplyProtocolSupported && p.NativeShapeSupported
                    && !p.Snapshot.IsDisplayTruncated,
                CanDeny = interactive && !p.ReplyAttempted && p.ReplyProtocolSupported,
                ReplyAttempted = p.ReplyAttempted,
                HasConflictingRequest = p.Conflicting
            }).ToArray();
        }
    }

    public async Task<bool> ReplyPermissionAsync(string sessionId, string receiptId, string response,
        CancellationToken cancellationToken = default)
    {
        if (response is not (OpenCodePermissionReply.Once or OpenCodePermissionReply.Reject))
            throw new NotSupportedException("Only a single-request permission reply or rejection is supported; native always exceeds an execution.");
        cancellationToken.ThrowIfCancellationRequested();
        PendingPermissionState pending;
        TurnContext context;
        CancellationToken turnCancellation;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.ActiveTurn is not { } active
                || active.Completion.Task.IsCompleted || active.CancelSignal.IsCancellationRequested
                || active.Phase == OpenCodeTurnPhase.Cancelling) return false;
            context = active;
            var found = context.Permissions.Values.SingleOrDefault(p => p.Snapshot.ReceiptId == receiptId);
            if (found is null || found.Resolved || found.ReplyAttempted || !found.ReplyProtocolSupported
                || response == OpenCodePermissionReply.Once && (!found.NativeShapeSupported || found.Conflicting || found.Snapshot.IsDisplayTruncated)) return false;
            pending = found;
            // Never retry a reply whose delivery may have reached the native permission gate.
            pending.ReplyAttempted = true;
            turnCancellation = context.CancelSignal.Token;
        }
        using var deadline = new CancellationTokenSource(_options.ConnectionTimeout, _timeProvider);
        using var replyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turnCancellation, deadline.Token);
        var acknowledged = await _client.ReplyPermissionAsync(pending.Snapshot.RequestId,
            new OpenCodePermissionReply { Response = response, ScopeSessionId = sessionId }, replyCancellation.Token)
            .ConfigureAwait(false);
        lock (_gate)
        {
            if (acknowledged) pending.Resolved = true;
        }
        return acknowledged;
    }

    private bool TrackPermissionEvent(OpenCodeEventEnvelope envelope, string sessionId, TurnContext context)
    {
        if (envelope.Type == "permission.replied" && envelope.Properties.ValueKind == JsonValueKind.Object
            && PermissionString(envelope.Properties, "sessionID") == sessionId
            && PermissionString(envelope.Properties, "requestID") is { } repliedId
            && PermissionString(envelope.Properties, "reply") is { } reply
            && OpenCodePermissionReply.StandardResponses.Contains(reply))
        {
            lock (_gate)
                if (context.Permissions.TryGetValue(repliedId, out var pending)) pending.Resolved = true;
            return true;
        }
        if (!PermissionRequestedEvent.TryParse(envelope, out var request) || request!.SessionId != sessionId)
            return true;
        if (request.RequestId.Length > 512 || request.RequestId.Any(char.IsControl)) return true;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Properties.GetRawText())));
        lock (_gate)
        {
            if (context.Permissions.TryGetValue(request.RequestId, out var existing))
            {
                if (existing.Fingerprint == fingerprint) return true;
                // A native request ID must not silently change the operation the user saw.
                existing.Conflicting = true;
                existing.NativeShapeSupported = IsSupportedPermissionShape(envelope, request);
                existing.ReplyProtocolSupported = IsSupportedPermissionReplyProtocol(envelope, request);
                existing.Snapshot = CreatePermissionSnapshot(envelope.Properties, request, existing.NativeShapeSupported, existing.ReplyProtocolSupported) with
                {
                    Explanation = "Нативный запрос изменился с тем же ID. Разрешение отключено; запрос можно только отклонить."
                };
                existing.Fingerprint = fingerprint;
                return true;
            }
            if (context.Permissions.Count >= PermissionHistoryLimit) return false;
            var supported = IsSupportedPermissionShape(envelope, request);
            var replySupported = IsSupportedPermissionReplyProtocol(envelope, request);
            context.Permissions.Add(request.RequestId, new PendingPermissionState
            {
                Snapshot = CreatePermissionSnapshot(envelope.Properties, request, supported, replySupported), Fingerprint = fingerprint,
                NativeShapeSupported = supported, ReplyProtocolSupported = replySupported
            });
            return true;
        }
    }

    private static bool IsSupportedPermissionShape(OpenCodeEventEnvelope envelope, PermissionRequestedEvent request) =>
        envelope.Type == PermissionRequestedEvent.AskedEventType && !string.IsNullOrWhiteSpace(request.Kind)
        && request.Kind.Length <= 256
        && envelope.Properties.TryGetProperty("patterns", out var patterns) && patterns.ValueKind == JsonValueKind.Array
        && patterns.EnumerateArray().All(p => p.ValueKind == JsonValueKind.String)
        && envelope.Properties.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
        && envelope.Properties.TryGetProperty("always", out var always) && always.ValueKind == JsonValueKind.Array
        && always.EnumerateArray().All(p => p.ValueKind == JsonValueKind.String);

    private static bool IsSupportedPermissionReplyProtocol(OpenCodeEventEnvelope envelope, PermissionRequestedEvent request) =>
        envelope.Type == PermissionRequestedEvent.AskedEventType
        && PermissionString(envelope.Properties, "id") == request.RequestId
        && !string.IsNullOrWhiteSpace(request.SessionId);

    private static OpenCodePendingPermission CreatePermissionSnapshot(JsonElement properties, PermissionRequestedEvent request, bool supported, bool replySupported)
    {
        var redactor = new CredentialTextRedactor();
        var kind = supported ? OpenCodePermissionPolicy.Normalize(request.Kind) : LLMWorkGUI.Domain.Enums.NormalizedApprovalKind.UnknownHighRisk;
        // Re-encode parsed strings before redaction so JSON escapes cannot hide known credential syntax.
        var canonical = JsonSerializer.Serialize(properties, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var redacted = redactor.RedactDiagnostic(canonical);
        return new OpenCodePendingPermission
        {
            ReceiptId = Guid.NewGuid().ToString("D"), RequestId = request.RequestId, SessionId = request.SessionId!,
            NativeKind = BoundPermissionText(redactor.RedactDiagnostic(request.Kind ?? "Not reported"), 256), Kind = kind,
            OriginalRequestDisplay = redacted.Length > PermissionDisplayLimit ? redacted[..PermissionDisplayLimit] + "…" : redacted,
            IsDisplayTruncated = redacted.Length > PermissionDisplayLimit,
            Explanation = redacted.Length > PermissionDisplayLimit
                ? "Исходный запрос сокращён. Разовое разрешение отключено; можно отклонить запрос или отменить выполнение."
                : supported ? OpenCodePermissionPolicy.Explain(kind)
                : replySupported ? "Детали нативного запроса неполные. Разрешение отключено; можно отклонить запрос или отменить выполнение."
                : "Формат нативного запроса не поддерживается этим каналом. Ответы отключены; можно отменить выполнение."
        };
    }

    private static string BoundPermissionText(string text, int length) =>
        text.Length > length ? text[..length] + "…" : text;

    private static string? PermissionString(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class PendingPermissionState
    {
        public required OpenCodePendingPermission Snapshot { get; set; }
        public required string Fingerprint { get; set; }
        public bool NativeShapeSupported { get; set; }
        public bool ReplyProtocolSupported { get; set; }
        public bool Conflicting { get; set; }
        public bool ReplyAttempted { get; set; }
        public bool Resolved { get; set; }
    }
}
