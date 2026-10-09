using System.Text.Json;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Normalized inbound ACP event produced from the JSON-RPC notification stream
/// (<c>acp-streaming-updates-sample.jsonl</c>). Unknown methods and malformed blocks are counted and
/// skipped without breaking the stream; terminal events are never silently lost.
/// </summary>
public abstract record CursorAcpStreamEvent
{
    /// <summary>Native session the event belongs to; null when the agent omitted it.</summary>
    public string? SessionId { get; init; }

    /// <summary>JSON-RPC method that carried the event.</summary>
    public required string Method { get; init; }

    /// <summary>Incremental model output (<c>update.type == "text"</c>).</summary>
    public sealed record TextChunk : CursorAcpStreamEvent
    {
        /// <summary>Text delta of this update.</summary>
        public required string Text { get; init; }
    }

    /// <summary>Incremental reasoning output (<c>update.type == "thought"</c>).</summary>
    public sealed record Thought : CursorAcpStreamEvent
    {
        /// <summary>Thought delta of this update.</summary>
        public required string Text { get; init; }
    }

    /// <summary>Tool invocation reported by the agent (<c>update.type == "tool_call"</c>).</summary>
    public sealed record ToolCall : CursorAcpStreamEvent
    {
        /// <summary>Native tool call identifier.</summary>
        public required string CallId { get; init; }

        /// <summary>Tool name reported by the agent.</summary>
        public required string ToolName { get; init; }

        /// <summary>Raw tool arguments when the agent returned them.</summary>
        public JsonElement? Arguments { get; init; }
    }

    /// <summary>Status/phase transition reported by the agent (<c>update.type == "status"</c>).</summary>
    public sealed record StatusUpdate : CursorAcpStreamEvent
    {
        /// <summary>Reported phase or state, e.g. <c>generating</c> or <c>cancelled</c>.</summary>
        public required string Phase { get; init; }
    }

    /// <summary>
    /// Approval request from the agent (<c>session/request_permission</c>). Per ADR-0003 §4.2 the
    /// normalized kind is always <see cref="NormalizedApprovalKind.UnknownHighRisk"/> until a
    /// sanitized per-kind fixture confirms a mapping; no auto-approval is ever applied.
    /// </summary>
    public sealed record PermissionRequest : CursorAcpStreamEvent
    {
        /// <summary>Identifier the client must echo in its permission reply.</summary>
        public required string RequestId { get; init; }

        /// <summary>Human-readable description of the requested operation.</summary>
        public required string Description { get; init; }

        /// <summary>False when native options do not offer a one-shot allow. Persistent grants are never substituted.</summary>
        public bool CanAllowOnce { get; init; }

        /// <summary>Raw sanitized payload of the permission request.</summary>
        public JsonElement? RawPayload { get; init; }

        /// <summary>Fail-closed normalized approval kind; always <c>UnknownHighRisk</c>.</summary>
        public NormalizedApprovalKind ApprovalKind => NormalizedApprovalKind.UnknownHighRisk;
    }
}
