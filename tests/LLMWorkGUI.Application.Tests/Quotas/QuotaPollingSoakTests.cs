using System.Collections.Concurrent;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Quotas;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Quotas;

/// <summary>
/// Phase 12 Milestone 12B: the quota polling soak drives 50+ deterministic cycles with injected
/// provider failures, exponential backoff and jitter, proves that no refresh stays in-flight and no
/// timer leaks, and stays free of UI-thread blocking.
/// </summary>
public sealed class QuotaPollingSoakTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Run_WithPeriodicProviderFailures_IsHealthyAndLeakFree()
    {
        var time = new CountingTimeProvider(Now);
        var adapter = new ScriptedQuotaSourceAdapter(failEveryNthCall: 5);
        var scheduler = CreateScheduler(adapter, time);
        var runner = new QuotaPollingSoakRunner(scheduler, time, time);

        var report = await runner.RunAsync(new QuotaSoakOptions
        {
            Cycles = 60,
            ExerciseBackgroundScheduler = true,
            ProviderProfileId = "prov-1",
            AccountId = "acc-1"
        });

        Assert.Equal(60, report.RequestedCycles);
        Assert.Equal(60, report.CompletedCycles);
        Assert.Equal(48, report.SuccessCount);
        Assert.Equal(12, report.FailureCount);
        Assert.Equal(1, report.MaxConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(30), report.MinObservedBackoff);
        Assert.Equal(TimeSpan.FromSeconds(30), report.MaxObservedBackoff);
        Assert.Equal(0, report.ActiveRefreshCountAtEnd);
        Assert.True(report.TimerProbeAttached);
        Assert.Equal(0, report.OutstandingTimersAtEnd);
        Assert.True(report.BackgroundSchedulerExercised);
        Assert.True(report.IsMemoryStable);
        Assert.True(report.IsHealthy);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Run_WithContinuousProviderFailures_AppliesBackoffUpToTheCap()
    {
        var time = new CountingTimeProvider(Now);
        var adapter = new ScriptedQuotaSourceAdapter(failEveryNthCall: 1);
        var scheduler = CreateScheduler(adapter, time);
        var runner = new QuotaPollingSoakRunner(scheduler, time);

        var report = await runner.RunAsync(new QuotaSoakOptions
        {
            Cycles = 50,
            ProviderProfileId = "prov-1",
            AccountId = "acc-1"
        });

        Assert.Equal(50, report.CompletedCycles);
        Assert.Equal(0, report.SuccessCount);
        Assert.Equal(50, report.FailureCount);
        Assert.Equal(50, report.MaxConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(30), report.MinObservedBackoff);
        Assert.Equal(TimeSpan.FromMinutes(15), report.MaxObservedBackoff);
        Assert.Equal(0, report.ActiveRefreshCountAtEnd);
        Assert.False(report.TimerProbeAttached);
        Assert.Equal(-1, report.OutstandingTimersAtEnd);
        Assert.True(report.IsHealthy);
    }

    [Fact]
    public async Task Run_RejectsASoakShorterThanTheMinimumCycleCount()
    {
        var time = new CountingTimeProvider(Now);
        var scheduler = CreateScheduler(new ScriptedQuotaSourceAdapter(), time);
        var runner = new QuotaPollingSoakRunner(scheduler, time);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => runner.RunAsync(new QuotaSoakOptions { Cycles = QuotaSoakOptions.MinimumCycles - 1 }));
    }

    [Fact]
    public async Task Run_DoesNotBlockTheCallingUiThread()
    {
        var time = new CountingTimeProvider(Now);
        var scheduler = CreateScheduler(new ScriptedQuotaSourceAdapter(), time);
        var runner = new QuotaPollingSoakRunner(scheduler, time);

        var report = await UiThreadSimulator.RunAsync(
            () => runner.RunAsync(new QuotaSoakOptions
            {
                Cycles = 50,
                ProviderProfileId = "prov-1",
                AccountId = "acc-1"
            }));

        Assert.True(report.IsHealthy);
    }

    private static QuotaRefreshScheduler CreateScheduler(
        IQuotaSourceAdapter adapter,
        TimeProvider timeProvider) =>
        new(
            adapter,
            new InMemoryQuotaSnapshotRepository(),
            accountRepository: null,
            Options.Create(new QuotaSchedulerOptions
            {
                JitterRatio = 0.0,
                ProviderRateLimitDelay = TimeSpan.Zero,
                InitialBackoff = TimeSpan.FromSeconds(30),
                BackoffMultiplier = 2.0,
                MaxBackoff = TimeSpan.FromMinutes(15),
                DefaultTtl = TimeSpan.FromMinutes(5),
                AutoStartBackgroundPolling = false
            }),
            timeProvider);

    private sealed class ScriptedQuotaSourceAdapter : IQuotaSourceAdapter
    {
        private readonly int _failEveryNthCall;
        private int _callCount;

        public ScriptedQuotaSourceAdapter(int failEveryNthCall = 0)
        {
            _failEveryNthCall = failEveryNthCall;
        }

        public string SourceKind => "soak-scripted";

        public Task<QuotaSnapshot> FetchQuotaAsync(
            string providerProfileId,
            string accountId,
            string? modelId = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            var now = DateTimeOffset.UtcNow;

            if (_failEveryNthCall > 0 && call % _failEveryNthCall == 0)
            {
                throw new HttpRequestException("simulated provider outage");
            }

            return Task.FromResult(new QuotaSnapshot(
                $"snap-{call}",
                accountId,
                QuotaProvenance.ExactProviderReported,
                capturedAt: now,
                buckets: new[]
                {
                    new QuotaBucket(
                        "requests_daily",
                        QuotaLimitUnit.Requests,
                        QuotaLimitWindow.PerDay,
                        limitValue: 1000,
                        usedValue: 10,
                        remainingValue: 990)
                },
                providerProfileId: providerProfileId,
                modelId: modelId,
                expiresAt: now.AddMinutes(5)));
        }
    }

    private sealed class CountingTimeProvider : TimeProvider, IQuotaSoakTimerProbe
    {
        private readonly object _syncRoot = new();
        private DateTimeOffset _now;
        private long _created;
        private long _disposed;

        public CountingTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public long OutstandingTimerCount =>
            Interlocked.Read(ref _created) - Interlocked.Read(ref _disposed);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_syncRoot)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            Interlocked.Increment(ref _created);

            return new CountingTimer(
                base.CreateTimer(callback, state, dueTime, period),
                this);
        }

        private sealed class CountingTimer : ITimer
        {
            private readonly ITimer _inner;
            private readonly CountingTimeProvider _owner;
            private int _disposed;

            public CountingTimer(ITimer inner, CountingTimeProvider owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => _inner.Change(dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Increment(ref _owner._disposed);
                }

                _inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();

                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A single-threaded message pump that simulates a WPF UI thread. If the soak ever blocked its
    /// thread with a synchronous wait, the pump would stop draining continuations and the guarded wait
    /// in <see cref="RunAsync{T}"/> would time out instead of the soak completing.
    /// </summary>
    private static class UiThreadSimulator
    {
        public static async Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            var context = new PumpSynchronizationContext();
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(context);
                context.Run();
            })
            {
                IsBackground = true,
                Name = "soak-ui-thread-simulator"
            };

            thread.Start();

            try
            {
                return await context.InvokeAsync(action).WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                context.Complete();
                thread.Join(TimeSpan.FromSeconds(5));
            }
        }

        private sealed class PumpSynchronizationContext : SynchronizationContext
        {
            private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

            public override void Post(SendOrPostCallback d, object? state)
            {
                _queue.Add((d, state));
            }

            public void Run()
            {
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            }

            public void Complete()
            {
                _queue.CompleteAdding();
            }

            public Task<T> InvokeAsync<T>(Func<Task<T>> action)
            {
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

                Post(
                    async _ =>
                    {
                        try
                        {
                            completion.SetResult(await action());
                        }
                        catch (Exception exception)
                        {
                            completion.SetException(exception);
                        }
                    },
                    state: null);

                return completion.Task;
            }
        }
    }
}
