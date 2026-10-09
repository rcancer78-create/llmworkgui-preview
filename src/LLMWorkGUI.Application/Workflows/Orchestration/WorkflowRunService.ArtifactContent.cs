namespace LLMWorkGUI.Application.Workflows.Orchestration;

public sealed partial class WorkflowRunService
{
    public const int MaxExecutionArtifactBytes = 20 * 1024 * 1024;

    private static async Task<MemoryStream> CaptureArtifactAsync(Stream content, CancellationToken cancellationToken)
    {
        var captured = new MemoryStream();
        try
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var allowed = (int)Math.Min(buffer.Length, MaxExecutionArtifactBytes - captured.Length + 1);
                var count = await content.ReadAsync(buffer.AsMemory(0, allowed), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (captured.Length + count > MaxExecutionArtifactBytes)
                    throw new WorkflowValidationException("The collected artifact exceeds the 20 MiB limit.");
                captured.Write(buffer, 0, count);
            }
            captured.Position = 0;
            return captured;
        }
        catch { captured.Dispose(); throw; }
    }
}
