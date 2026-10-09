namespace LLMWorkGUI.Application.Cli;

public interface ICliDetectionService
{
    Task<CliDetectionSnapshot> DetectAsync(CancellationToken cancellationToken = default);
}
