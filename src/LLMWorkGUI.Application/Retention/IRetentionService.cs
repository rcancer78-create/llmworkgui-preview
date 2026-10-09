namespace LLMWorkGUI.Application.Retention;

public interface IRetentionService
{
    Task<RetentionRunReport> RunAsync(CancellationToken cancellationToken = default);
}
