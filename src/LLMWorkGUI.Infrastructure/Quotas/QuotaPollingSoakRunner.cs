using System.Diagnostics;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Quotas;

/// <summary>
/// Default quota polling soak runner. It drives the real scheduler through many deterministic cycles,
/// records successes, failures, backoff and latency, and proves at the end that no refresh stayed
/// in-flight and no background timer leaked. The runner only awaits asynchronous calls, so it never
/// blocks the calling (UI) thread (ROADMAP Phase 12).
/// </summary>
public sealed class QuotaPollingSoakRunner : IQuotaPollingSoakRunner
{
    private readonly IQuotaRefreshScheduler _scheduler;
    private readonly TimeProvider _timeProvider;
    private readonly IQuotaSoakTimerProbe? _timerProbe;

    public QuotaPollingSoakRunner(
        IQuotaRefreshScheduler scheduler,
        TimeProvider? timeProvider = null,
        IQuotaSoakTimerProbe? timerProbe = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        _scheduler = scheduler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timerProbe = timerProbe;
    }

    public async Task<QuotaSoakReport> RunAsync(
        QuotaSoakOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? new QuotaSoakOptions();
        effectiveOptions.Validate();

        var startedAt = _timeProvider.GetUtcNow();
        var memoryStart = GC.GetTotalMemory(forceFullCollection: true);

        var completedCycles = 0;
        var successCount = 0;
        var failureCount = 0;
        var consecutiveFailures = 0;
        var maxConsecutiveFailures = 0;
        var minBackoff = TimeSpan.Zero;
        var maxBackoff = TimeSpan.Zero;
        var maxCycleDuration = TimeSpan.Zero;
        var warnings = new List<string>();

        if (effectiveOptions.ExerciseBackgroundScheduler)
        {
            await _scheduler.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            for (var cycle = 1; cycle <= effectiveOptions.Cycles; cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cycleWatch = Stopwatch.StartNew();

                var snapshot = await _scheduler
                    .RefreshAccountNowAsync(
                        effectiveOptions.ProviderProfileId,
                        effectiveOptions.AccountId,
                        modelId: null,
                        force: true,
                        cancellationToken)
                    .ConfigureAwait(false);

                cycleWatch.Stop();

                if (cycleWatch.Elapsed > maxCycleDuration)
                {
                    maxCycleDuration = cycleWatch.Elapsed;
                }

                if (snapshot.Provenance == QuotaProvenance.Error)
                {
                    failureCount++;
                    consecutiveFailures++;
                    maxConsecutiveFailures = Math.Max(maxConsecutiveFailures, consecutiveFailures);
                }
                else
                {
                    successCount++;
                    consecutiveFailures = 0;
                }

                var status = _scheduler.GetStatus(effectiveOptions.AccountId);

                if (status is null)
                {
                    warnings.Add($"Cycle {cycle} produced no refresh status.");
                }
                else if (status.CurrentBackoff > TimeSpan.Zero)
                {
                    minBackoff = minBackoff == TimeSpan.Zero
                        ? status.CurrentBackoff
                        : TimeSpan.FromTicks(Math.Min(minBackoff.Ticks, status.CurrentBackoff.Ticks));
                    maxBackoff = TimeSpan.FromTicks(Math.Max(maxBackoff.Ticks, status.CurrentBackoff.Ticks));
                }

                completedCycles++;

                if (effectiveOptions.InterCycleDelay > TimeSpan.Zero)
                {
                    await Task.Delay(effectiveOptions.InterCycleDelay, _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (effectiveOptions.ExerciseBackgroundScheduler)
            {
                await _scheduler.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        var activeRefreshCountAtEnd = _scheduler
            .GetAllStatuses()
            .Count(status => status.IsRefreshing);

        if (activeRefreshCountAtEnd > 0)
        {
            warnings.Add(
                $"{activeRefreshCountAtEnd} refresh status(es) stayed in-flight after the last soak cycle.");
        }

        var memoryEnd = GC.GetTotalMemory(forceFullCollection: true);

        return new QuotaSoakReport
        {
            StartedAtUtc = startedAt,
            CompletedAtUtc = _timeProvider.GetUtcNow(),
            RequestedCycles = effectiveOptions.Cycles,
            CompletedCycles = completedCycles,
            SuccessCount = successCount,
            FailureCount = failureCount,
            MaxConsecutiveFailures = maxConsecutiveFailures,
            MinObservedBackoff = minBackoff,
            MaxObservedBackoff = maxBackoff,
            MaxCycleDuration = maxCycleDuration,
            ManagedMemoryStartBytes = memoryStart,
            ManagedMemoryEndBytes = memoryEnd,
            MemoryGrowthBudgetBytes = effectiveOptions.MemoryGrowthBudgetBytes,
            ActiveRefreshCountAtEnd = activeRefreshCountAtEnd,
            OutstandingTimersAtEnd = _timerProbe?.OutstandingTimerCount ?? -1,
            TimerProbeAttached = _timerProbe is not null,
            BackgroundSchedulerExercised = effectiveOptions.ExerciseBackgroundScheduler,
            Warnings = warnings
        };
    }
}
