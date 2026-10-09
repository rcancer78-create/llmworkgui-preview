using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Cli;

public sealed class CliDetectionSnapshot
{
    public CliDetectionSnapshot(IReadOnlyList<CliStatus> tools, DateTimeOffset detectedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(tools);

        if (tools.Count == 0)
        {
            throw new ArgumentException("At least one CLI status is required.", nameof(tools));
        }

        Tools = tools.ToArray();
        DetectedAtUtc = detectedAtUtc;

        OpenCode = RequireTool(BackendType.OpenCode);
        CursorAcp = RequireTool(BackendType.CursorAcp);
    }

    public IReadOnlyList<CliStatus> Tools { get; }

    public DateTimeOffset DetectedAtUtc { get; }

    public CliStatus OpenCode { get; }

    public CliStatus CursorAcp { get; }

    /// <summary>
    /// Optional native AGY CLI status. The standalone AGY route is additive and is only
    /// reported when the detection service probes <c>agy</c>; older snapshots stay valid.
    /// </summary>
    public CliStatus? Agy => Tools.FirstOrDefault(tool => tool.Backend == BackendType.Agy);

    public int DetectedCount => Tools.Count(tool => tool.IsDetected);

    public bool HasAnyDetected => DetectedCount > 0;

    public bool HasDetectionErrors => Tools.Any(tool => tool.DetectionError is not null);

    public bool IsDegraded => !HasAnyDetected;

    public static CliDetectionSnapshot AllNotDetected(DateTimeOffset detectedAtUtc)
    {
        return new CliDetectionSnapshot(
            new[]
            {
                CliStatus.NotDetected(BackendType.OpenCode, "opencode", "OpenCode"),
                CliStatus.NotDetected(BackendType.CursorAcp, "cursor-agent", "Cursor Agent")
            },
            detectedAtUtc);
    }

    private CliStatus RequireTool(BackendType backend)
    {
        var status = Tools.FirstOrDefault(tool => tool.Backend == backend);

        if (status is null)
        {
            throw new ArgumentException($"The snapshot is missing a status for backend '{backend}'.", nameof(Tools));
        }

        return status;
    }
}
