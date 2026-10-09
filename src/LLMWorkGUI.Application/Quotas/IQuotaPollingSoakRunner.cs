namespace LLMWorkGUI.Application.Quotas;

/// <summary>
/// The deterministic soak configuration. Cycles must cover at least
/// <see cref="MinimumCycles"/> polling rounds; failures are injected on a fixed schedule so the run is
/// fully offline and reproducible.
/// </summary>
public sealed record QuotaSoakOptions
{
    /// <summary>The smallest cycle count that counts as a soak run.</summary>
    public const int MinimumCycles = 50;

    public int Cycles { get; init; } = 100;

    /// <summary>Delay between cycles; the default keeps the soak fast and deterministic.</summary>
    public TimeSpan InterCycleDelay { get; init; } = TimeSpan.Zero;

    public string ProviderProfileId { get; init; } = "soak-provider";

    public string AccountId { get; init; } = "soak-account";

    /// <summary>The managed memory delta above which the soak is not considered stable.</summary>
    public long MemoryGrowthBudgetBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>When true the runner also starts and stops the scheduler background timer.</summary>
    public bool ExerciseBackgroundScheduler { get; init; }

    public void Validate()
    {
        if (Cycles < MinimumCycles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Cycles),
                Cycles,
                $"A quota polling soak requires at least {MinimumCycles} cycles.");
        }

        if (InterCycleDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InterCycleDelay),
                InterCycleDelay,
                "The inter-cycle delay cannot be negative.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);

        if (MemoryGrowthBudgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MemoryGrowthBudgetBytes),
                MemoryGrowthBudgetBytes,
                "The managed memory growth budget must be positive.");
        }
    }
}

/// <summary>
/// Optional probe that reports how many timers are still outstanding. The production composition
/// leaves it unattached; the soak test attaches a counting time source to prove that the background
/// scheduler leaked no timer.
/// </summary>
public interface IQuotaSoakTimerProbe
{
    long OutstandingTimerCount { get; }
}

/// <summary>
/// Drives a sustained quota polling soak against <see cref="IQuotaRefreshScheduler"/>: many cycles
/// with provider failures, jitter and exponential backoff, measuring latency, managed memory and the
/// absence of leaked timers or stuck in-flight refreshes (ROADMAP Phase 12).
/// </summary>
public interface IQuotaPollingSoakRunner
{
    Task<QuotaSoakReport> RunAsync(
        QuotaSoakOptions? options = null,
        CancellationToken cancellationToken = default);
}
