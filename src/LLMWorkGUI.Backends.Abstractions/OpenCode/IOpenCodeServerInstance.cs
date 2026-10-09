namespace LLMWorkGUI.Backends.Abstractions.OpenCode;

public interface IOpenCodeServerInstance
{
    string InstanceId { get; }

    int? ProcessId { get; }

    /// <summary>Supervisor-issued local launch identity. Zero means unknown.</summary>
    long ProcessGeneration => 0;

    int AssignedPort { get; }

    Uri BaseUrl { get; }

    /// <summary>The supervisor task is active and its owned lifetime has not been retired.
    /// A canceled lifetime is unusable even while physical cleanup continues; false alone
    /// does not prove physical tree exit.</summary>
    bool IsAlive { get; }

    /// <summary>Observation from this manager's exact owned supervisor cleanup, not session/account origin evidence.
    /// Task completion or IsAlive=false alone does not imply physical termination.</summary>
    bool IsTerminationConfirmed => false;

    DateTimeOffset StartedAtUtc { get; }
}
