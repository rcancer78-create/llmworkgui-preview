namespace LLMWorkGUI.Application.Lifecycle;

/// <summary>
/// The long-term retention policy of the hardening phase: stale diagnostic archives older than the
/// bundle TTL are deleted, abandoned scratch workspaces are removed, and old audit/event records are
/// archived before any of them is removed. Active data - in-flight runs, recent bundles and active
/// scratch workspaces - is never touched (ROADMAP Phase 12).
/// </summary>
public sealed class RetentionPolicy
{
    /// <summary>Diagnostic bundle TTL: archives older than seven days are removed.</summary>
    public static readonly TimeSpan DefaultDiagnosticBundleTtl = TimeSpan.FromDays(7);

    /// <summary>Scratch workspace TTL: abandoned temporary workspaces older than seven days are removed.</summary>
    public static readonly TimeSpan DefaultScratchWorkspaceTtl = TimeSpan.FromDays(7);

    /// <summary>Audit/event TTL: records older than thirty days are archived.</summary>
    public static readonly TimeSpan DefaultAuditRecordTtl = TimeSpan.FromDays(30);

    public static RetentionPolicy Default { get; } = new();

    public TimeSpan DiagnosticBundleTtl { get; init; } = DefaultDiagnosticBundleTtl;

    public TimeSpan ScratchWorkspaceTtl { get; init; } = DefaultScratchWorkspaceTtl;

    public TimeSpan AuditRecordTtl { get; init; } = DefaultAuditRecordTtl;

    public DateTimeOffset GetDiagnosticBundleCutoff(DateTimeOffset nowUtc) => nowUtc - DiagnosticBundleTtl;

    public DateTimeOffset GetScratchWorkspaceCutoff(DateTimeOffset nowUtc) => nowUtc - ScratchWorkspaceTtl;

    public DateTimeOffset GetAuditRecordCutoff(DateTimeOffset nowUtc) => nowUtc - AuditRecordTtl;

    public void Validate()
    {
        if (DiagnosticBundleTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DiagnosticBundleTtl),
                DiagnosticBundleTtl,
                "The diagnostic bundle TTL must be positive.");
        }

        if (ScratchWorkspaceTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ScratchWorkspaceTtl),
                ScratchWorkspaceTtl,
                "The scratch workspace TTL must be positive.");
        }

        if (AuditRecordTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AuditRecordTtl),
                AuditRecordTtl,
                "The audit record TTL must be positive.");
        }
    }
}
