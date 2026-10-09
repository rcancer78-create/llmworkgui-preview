using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Infrastructure.Concurrency;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class SingleSupervisorGuardTests
{
    [Fact]
    public void FirstInstance_IsPrimarySupervisorWithInstanceId()
    {
        using var directory = new TestDirectory();
        using var guard = new ApplicationInstanceGuard(directory.Root);

        Assert.True(guard.IsPrimarySupervisor);
        Assert.False(guard.IsViewOnly);
        Assert.True(Guid.TryParse(guard.InstanceId, out _));

        guard.EnsureSupervisorPermitted();
    }

    [Fact]
    public void SecondInstance_WithSameAppDataDirectory_IsViewOnly()
    {
        using var directory = new TestDirectory();
        using var primary = new ApplicationInstanceGuard(directory.Root);

        var secondary = RunOnDedicatedThread(() => new ApplicationInstanceGuard(directory.Root));

        using (secondary)
        {
            Assert.True(primary.IsPrimarySupervisor);
            Assert.False(secondary.IsPrimarySupervisor);
            Assert.True(secondary.IsViewOnly);
            Assert.NotEqual(primary.InstanceId, secondary.InstanceId);

            Assert.Throws<SecondaryInstanceReadOnlyException>(secondary.EnsureSupervisorPermitted);
        }
    }

    [Fact]
    public void DifferentAppDataDirectories_AreIndependent()
    {
        using var directory = new TestDirectory();
        using var primary = new ApplicationInstanceGuard(directory.Root);
        using var otherDirectory = new TestDirectory();
        using var otherGuard = new ApplicationInstanceGuard(otherDirectory.Root);

        Assert.True(primary.IsPrimarySupervisor);
        Assert.True(otherGuard.IsPrimarySupervisor);

        var secondary = RunOnDedicatedThread(() => new ApplicationInstanceGuard(directory.Root));

        using (secondary)
        {
            Assert.True(secondary.IsViewOnly);
        }
    }

    [Fact]
    public void DisposedPrimaryInstance_AllowsNextInstanceToBecomePrimary()
    {
        using var directory = new TestDirectory();

        var first = new ApplicationInstanceGuard(directory.Root);
        Assert.True(first.IsPrimarySupervisor);
        first.Dispose();

        using var second = new ApplicationInstanceGuard(directory.Root);
        Assert.True(second.IsPrimarySupervisor);
    }

    [Fact]
    public void AbandonedSupervisorMutex_IsTakenOverByNextInstance()
    {
        using var directory = new TestDirectory();
        var mutexName = NamedMutexNames.ForSupervisor(directory.Root);

        using var keeper = new Mutex(initiallyOwned: false, mutexName, out _);
        using var ownerReady = new ManualResetEventSlim(false);

        var ownerThread = new Thread(() =>
        {
            var mutex = new Mutex(initiallyOwned: false, mutexName, out _);
            mutex.WaitOne(0);
            ownerReady.Set();
            // The thread terminates while owning the mutex; the keeper handle keeps the object alive,
            // so the next instance observes an abandoned mutex.
        })
        {
            IsBackground = true
        };

        ownerThread.Start();
        ownerReady.Wait();
        ownerThread.Join();

        // Kernel abandonment is not always observable immediately after Join; poll until the
        // abandoned mutex is taken over. A non-owning keeper handle keeps the named object alive.
        ApplicationInstanceGuard? guard = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (guard is null && DateTime.UtcNow < deadline)
        {
            var candidate = new ApplicationInstanceGuard(directory.Root);

            if (candidate.IsPrimarySupervisor)
            {
                guard = candidate;
                break;
            }

            candidate.Dispose();
            Thread.Sleep(10);
        }

        using (guard)
        {
            Assert.NotNull(guard);
            guard!.EnsureSupervisorPermitted();
        }
    }

    [Fact]
    public void NamedMutexNames_AreDeterministicAndHashed()
    {
        using var directory = new TestDirectory();

        var supervisorName = NamedMutexNames.ForSupervisor(directory.Root);
        var checkoutName = NamedMutexNames.ForCheckout(directory.Root);

        Assert.StartsWith(@"Local\LLMWorkGUI_Supervisor_", supervisorName, StringComparison.Ordinal);
        Assert.StartsWith(@"Local\LLMWorkGUI_Checkout_", checkoutName, StringComparison.Ordinal);
        Assert.Equal(64, supervisorName[^64..].Length);
        Assert.Equal(64, checkoutName[^64..].Length);
        Assert.Equal(supervisorName, NamedMutexNames.ForSupervisor(directory.Root));
        Assert.NotEqual(supervisorName, checkoutName);
    }

    private static T RunOnDedicatedThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true
        };

        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("Dedicated thread execution failed.", failure);
        }

        return result!;
    }
}
