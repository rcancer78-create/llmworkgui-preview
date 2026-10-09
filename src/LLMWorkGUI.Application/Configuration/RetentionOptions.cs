namespace LLMWorkGUI.Application.Configuration;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public int RawBackendEventsRetentionDays { get; set; } = 30;

    public int ProcessServerLogsRetentionDays { get; set; } = 14;

    public int DiagnosticBundlesRetentionDays { get; set; } = 7;

    public int QuotaSnapshotsRetentionDays { get; set; } = 90;

    public int QuotaSnapshotsDownsampleDays { get; set; } = 30;

    public int HealthAuditTransitionsRetentionDays { get; set; } = 180;
}
