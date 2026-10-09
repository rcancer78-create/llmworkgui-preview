using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Agy;

namespace LLMWorkGUI.Infrastructure.Agy;

/// <summary>
/// Tolerant NDJSON parser for the agy stream-json output (ТЗ §6.11a). It confirms the
/// session init, native conversation id, observed model, permissions, tool events and the
/// terminal result. Unknown event shapes are preserved as absence of evidence rather than
/// being reported as success; only sanitized metadata is retained (no tool payloads).
/// </summary>
internal sealed class AgyStreamJsonParser
{
    private static readonly string[] ConversationIdFields =
        ["conversation_id", "conversationId", "session_id", "sessionId"];

    private static readonly string[] ModelFields =
        ["model", "model_id", "modelId"];

    private static readonly string[] PermissionArrayFields =
        ["permissions", "granted_permissions", "grantedPermissions", "allowed_tools", "allowedTools", "tools"];

    private static readonly string[] PermissionModeFields =
        ["permission_mode", "permissionMode", "approval_mode", "approvalMode"];

    private readonly StringBuilder _lineBuffer = new();
    private readonly List<AgyToolEvent> _toolEvents = new();
    private readonly List<string> _grantedPermissions = new();

    public bool InitConfirmed { get; private set; }

    public string? ConversationId { get; private set; }

    public string? ObservedModel { get; private set; }

    public IReadOnlyList<string> GrantedPermissions => _grantedPermissions;

    public IReadOnlyList<AgyToolEvent> ToolEvents => _toolEvents;

    public AgyTerminalEvent? TerminalResult { get; private set; }

    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        _lineBuffer.Append(chunk);

        while (TryTakeLine(out var line))
        {
            ProcessLine(line);
        }
    }

    public void Complete()
    {
        if (_lineBuffer.Length == 0)
        {
            return;
        }

        var pending = _lineBuffer.ToString();
        _lineBuffer.Clear();
        ProcessLine(pending);
    }

    private bool TryTakeLine(out string line)
    {
        var buffer = _lineBuffer.ToString();
        var newlineIndex = buffer.IndexOf('\n');

        if (newlineIndex < 0)
        {
            line = string.Empty;
            return false;
        }

        var end = newlineIndex;
        if (end > 0 && buffer[end - 1] == '\r')
        {
            end--;
        }

        line = buffer[..end];
        _lineBuffer.Clear();
        _lineBuffer.Append(buffer[(newlineIndex + 1)..]);

        return true;
    }

    private void ProcessLine(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.Length == 0 || trimmed[0] is not '{')
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            Apply(root);
        }
    }

    private void Apply(JsonElement root)
    {
        var eventType = FirstString(root, "type", "event", "kind") ?? string.Empty;
        var subtype = FirstString(root, "subtype");
        var normalizedType = eventType.ToLowerInvariant();

        var isInitEvent =
            normalizedType is "init" or "system_init" or "session_init" or "start" ||
            string.Equals(subtype, "init", StringComparison.OrdinalIgnoreCase);

        if (isInitEvent)
        {
            InitConfirmed = true;
            ConversationId ??= FirstString(root, ConversationIdFields);
            ObservedModel ??= FirstString(root, ModelFields);
            ReadPermissions(root);
        }
        else if (IsTerminalEvent(normalizedType))
        {
            TerminalResult = new AgyTerminalEvent(
                IsError: IsErrorResult(root, subtype),
                Subtype: subtype,
                ResultText: FirstString(root, "result", "text", "response"));
        }
        else if (normalizedType.Contains("tool", StringComparison.Ordinal))
        {
            var name = FirstString(root, "name", "tool", "tool_name", "toolName") ?? "tool";
            var status = FirstString(root, "status") ?? subtype;
            _toolEvents.Add(new AgyToolEvent(name, status));
        }

        if (string.Equals(normalizedType, "message", StringComparison.OrdinalIgnoreCase) ||
            normalizedType.StartsWith("assistant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "model", StringComparison.OrdinalIgnoreCase))
        {
            ObservedModel ??= FirstString(root, ModelFields);
        }

        // The observed model may be reported on any event; the first non-empty value wins.
        ObservedModel ??= FirstString(root, ModelFields);
    }

    private void ReadPermissions(JsonElement root)
    {
        foreach (var field in PermissionArrayFields)
        {
            if (!root.TryGetProperty(field, out var element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        AddPermission(item.GetString());
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                AddPermission(element.GetString());
            }
        }

        var permissionMode = FirstString(root, PermissionModeFields);
        if (!string.IsNullOrWhiteSpace(permissionMode))
        {
            AddPermission($"permission-mode:{permissionMode}");
        }
    }

    private void AddPermission(string? permission)
    {
        if (string.IsNullOrWhiteSpace(permission))
        {
            return;
        }

        var normalized = permission.Trim();

        if (!_grantedPermissions.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _grantedPermissions.Add(normalized);
        }
    }

    private static bool IsTerminalEvent(string normalizedType) =>
        normalizedType is "result" or "final" or "turn_complete" or "turn_completed" or "complete" or "completed";

    private static bool IsErrorResult(JsonElement root, string? subtype)
    {
        if (string.Equals(subtype, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subtype, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetBoolean(root, "is_error", out var isError) ||
            TryGetBoolean(root, "isError", out isError) ||
            TryGetBoolean(root, "error", out isError))
        {
            return isError;
        }

        var status = FirstString(root, "status");

        return string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstString(JsonElement root, params string[] fields)
    {
        foreach (var field in fields)
        {
            if (!root.TryGetProperty(field, out var element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static bool TryGetBoolean(JsonElement root, string field, out bool value)
    {
        value = false;

        if (!root.TryGetProperty(field, out var element))
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        return false;
    }
}
