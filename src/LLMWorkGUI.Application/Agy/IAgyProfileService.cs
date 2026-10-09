namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Native multi-account bridge over the user-selected agy-profile utility (ТЗ §6.4, §6.11a).
/// The service only uses the documented <c>list</c>, <c>current</c> and <c>switch</c> commands,
/// never passes <c>-Force</c>, never issues <c>next</c>/<c>random</c> rotation commands and
/// never reads, copies or logs AGY credentials (DPAPI profiles stay untouched).
/// </summary>
public interface IAgyProfileService
{
    /// <summary>True when the agy-profile utility could be located and may be invoked.</summary>
    bool IsAvailable { get; }

    /// <summary>Resolved path of the agy-profile entry point, or null when unavailable.</summary>
    string? ExecutablePath { get; }

    /// <summary>Precise blocker description when the utility is unavailable.</summary>
    string? AvailabilityBlocker { get; }

    /// <summary>
    /// Returns the name of the currently active profile via <c>agy-profile current</c>,
    /// or null when no saved profile matches the logged-in account.
    /// </summary>
    Task<string?> GetActiveProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns saved profiles with the active marker via <c>agy-profile list</c>.
    /// Never includes credentials or token material.
    /// </summary>
    Task<IReadOnlyList<AgyProfileSummary>> ListProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the active profile via <c>agy-profile switch &lt;profileName&gt;</c>.
    /// Refused while an agy process is running and confirmed by re-reading the active profile.
    /// </summary>
    Task<AgyProfileSwitchResult> SwitchProfileAsync(string profileName, CancellationToken cancellationToken = default);
}
