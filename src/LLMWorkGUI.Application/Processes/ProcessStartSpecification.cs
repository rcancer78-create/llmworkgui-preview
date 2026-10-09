namespace LLMWorkGUI.Application.Processes;

public sealed class ProcessStartSpecification
{
    public required string ExecutionId { get; init; }

    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string? WorkingDirectory { get; init; }

    /// <summary>False supplies only EnvironmentVariables to the child; ambient credentials/config are excluded.</summary>
    public bool InheritEnvironment { get; init; } = true;

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public ProcessStdinPolicy StdinPolicy { get; init; } = ProcessStdinPolicy.Closed;

    /// <summary>Called by the supervisor after successful OS startup and tree ownership.
    /// The positive generation identifies this local launch, not native model/account identity.</summary>
    public Action<long>? ProcessStarted { get; init; }
}
