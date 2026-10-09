using LLMWorkGUI.Application.Agy;
using LLMWorkGUI.Application.StarCliProxy;
using Xunit;

namespace LLMWorkGUI.Application.Tests.StarCliProxy;

public sealed class AccountContextManagerTests
{
    private const string MissingUtilityBlocker =
        "agy-profile utility was not found; install it from https://github.com/haclongkim/agy-profile";

    [Fact]
    public void Constructor_RejectsNullService()
    {
        Assert.Throws<ArgumentNullException>(() => new AccountContextManager(null!));
    }

    [Fact]
    public void RegisterCodexContext_RejectsNull()
    {
        var manager = new AccountContextManager(new FakeAgyProfileService());

        Assert.Throws<ArgumentNullException>(() => manager.RegisterCodexContext(null!));
    }

    [Fact]
    public async Task RegisterCodexContext_CannotRemapAnExistingAccountToAnotherHome()
    {
        using var firstHome = new TemporaryDirectory();
        using var otherHome = new TemporaryDirectory();
        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        manager.RegisterCodexContext(new CodexAccountContext("work", firstHome.Root));

        Assert.Throws<InvalidOperationException>(() => manager.RegisterCodexContext(new CodexAccountContext("work", otherHome.Root)));

        Assert.Equal(firstHome.Root, (await manager.VerifyContextAsync(AccountContextRequest.ForCodex("work"))).CodexHomePath);
        Assert.Single(manager.CodexContexts);
    }

    [Fact]
    public void RegisterCodexContext_RejectsAnotherAccountSharingNormalizedHome()
    {
        using var home = new TemporaryDirectory();
        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        manager.RegisterCodexContext(new CodexAccountContext("work", home.Root));

        Assert.Throws<InvalidOperationException>(() => manager.RegisterCodexContext(
            new CodexAccountContext("personal", Path.Combine(home.Root, "."))));

        Assert.Equal("work", Assert.Single(manager.CodexContexts).AccountId);
    }

    [Fact]
    public void RegisterCodexContext_SameAccountAndHomeRegistrationIsIdempotent()
    {
        using var home = new TemporaryDirectory();
        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        manager.RegisterCodexContext(new CodexAccountContext("work", home.Root));
        manager.RegisterCodexContext(new CodexAccountContext("work", Path.Combine(home.Root, ".")));

        var registered = Assert.Single(manager.CodexContexts);
        Assert.Equal("work", registered.AccountId);
        Assert.Equal(home.Root, registered.CodexHomePath);
    }

    [Fact]
    public async Task RegisterCodexContext_ConcurrentAliasesCannotReserveOneHomeTwice()
    {
        using var home = new TemporaryDirectory();
        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrations = new[] { "work", "personal" }.Select(account => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                manager.RegisterCodexContext(new CodexAccountContext(account, home.Root));
                return true;
            }
            catch (InvalidOperationException) { return false; }
        })).ToArray();

        start.TrySetResult();
        var accepted = await Task.WhenAll(registrations).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(accepted.Where(result => result));
        Assert.Single(manager.CodexContexts);
    }

    [Fact]
    public async Task VerifyContextAsync_ObservesProfileOnlyAfterManagedSwitchCompletes()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "personal" };
        var manager = new AccountContextManager(service);
        var switchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSwitch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeSwitchAsync = async () =>
        {
            switchEntered.TrySetResult();
            await releaseSwitch.Task;
        };
        var selecting = manager.SelectAndVerifyAsync(AccountContextRequest.ForAgy("work"));
        try
        {
            await switchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var verification = manager.VerifyContextAsync(AccountContextRequest.ForAgy("personal"));
            Assert.False(verification.IsCompleted);
            releaseSwitch.TrySetResult();
            Assert.True((await selecting.WaitAsync(TimeSpan.FromSeconds(5))).IsResolved);
            Assert.False((await verification.WaitAsync(TimeSpan.FromSeconds(5))).IsResolved);
            Assert.Equal(new[] { "work" }, service.SwitchCalls);
        }
        finally
        {
            releaseSwitch.TrySetResult();
            await selecting.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void CodexAccountContext_RejectsRelativeHomePath()
    {
        Assert.Throws<ArgumentException>(() => new CodexAccountContext("work", @"relative\codex-home"));
    }

    [Fact]
    public void HasPinnableContexts_ReflectsRegisteredContextsAndUtilityAvailability()
    {
        var unavailable = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        var available = new AccountContextManager(new FakeAgyProfileService { IsAvailable = true });

        Assert.False(unavailable.HasPinnableContexts);
        Assert.True(available.HasPinnableContexts);

        Assert.False(new AccountContextManager(new FakeAgyProfileService { IsAvailable = false }).HasPinnableContexts);

        using var home = new TemporaryDirectory();

        unavailable.RegisterCodexContext(new CodexAccountContext("work", home.Root));

        Assert.True(unavailable.HasPinnableContexts);
    }

    [Fact]
    public async Task CodexContexts_ResolveEachAccountHomeIndependently()
    {
        using var homeA = new TemporaryDirectory();
        using var homeB = new TemporaryDirectory();

        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        manager.RegisterCodexContext(new CodexAccountContext("work", homeA.Root, "Work"));
        manager.RegisterCodexContext(new CodexAccountContext("personal", homeB.Root, "Personal"));

        var work = await manager.SelectAndVerifyAsync(AccountContextRequest.ForCodex("work"));
        var personal = await manager.SelectAndVerifyAsync(AccountContextRequest.ForCodex("personal"));

        Assert.True(work.IsResolved);
        Assert.True(personal.IsResolved);
        Assert.Equal(AccountContextKind.Codex, work.Kind);
        Assert.Equal(homeA.Root, work.CodexHomePath);
        Assert.Equal(homeB.Root, personal.CodexHomePath);
        Assert.NotEqual(work.CodexHomePath, personal.CodexHomePath);

        // Two CODEX_HOME contexts never share or copy each other's directory.
        Assert.DoesNotContain(personal.CodexHomePath!, work.CodexHomePath!);
        Assert.DoesNotContain(work.CodexHomePath!, personal.CodexHomePath!);
    }

    [Fact]
    public async Task SelectAndVerify_UnknownCodexAccount_FailsClosedWithHomeRequirement()
    {
        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });

        var selection = await manager.SelectAndVerifyAsync(AccountContextRequest.ForCodex("missing"));

        Assert.False(selection.IsResolved);
        Assert.Contains("absolute CODEX_HOME", selection.FailureReason);
        Assert.Contains("missing", selection.FailureReason);
    }

    [Fact]
    public async Task SelectAndVerify_CodexHomeDirectoryMissing_FailsClosed()
    {
        using var home = new TemporaryDirectory();
        var missingPath = Path.Combine(home.Root, "not-created");

        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        manager.RegisterCodexContext(new CodexAccountContext("work", missingPath));

        var selection = await manager.SelectAndVerifyAsync(AccountContextRequest.ForCodex("work"));

        Assert.False(selection.IsResolved);
        Assert.Contains("does not exist", selection.FailureReason);
    }

    [Fact]
    public async Task Agy_WhenProfileAlreadyActive_ResolvesWithoutSwitching()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "work" };
        var manager = new AccountContextManager(service);

        var selection = await manager.SelectAndVerifyAsync(AccountContextRequest.ForAgy("work"));

        Assert.True(selection.IsResolved);
        Assert.Equal("work", selection.AgyProfileName);
        Assert.False(selection.RequiresNewSession);
        Assert.Empty(service.SwitchCalls);
    }

    [Fact]
    public async Task Agy_WhenProfileNotActive_SwitchesUnderLockAndRequiresNewSession()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "personal" };
        var manager = new AccountContextManager(service);

        var selection = await manager.SelectAndVerifyAsync(AccountContextRequest.ForAgy("work"));

        Assert.True(selection.IsResolved);
        Assert.True(selection.RequiresNewSession);
        Assert.Equal(new[] { "work" }, service.SwitchCalls);
    }

    [Fact]
    public async Task Agy_WhenUtilityUnavailable_FailsClosedWithInstallUrl()
    {
        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });

        var selection = await manager.SelectAndVerifyAsync(AccountContextRequest.ForAgy("work"));

        Assert.False(selection.IsResolved);
        Assert.Contains("https://github.com/haclongkim/agy-profile", selection.FailureReason);
    }

    [Fact]
    public async Task Agy_WhenProfileIsNotSaved_FailsClosedWithoutSwitch()
    {
        var service = new FakeAgyProfileService
        {
            IsAvailable = true,
            ActiveProfile = "personal",
            Profiles = [new AgyProfileSummary("personal", IsActive: true)]
        };
        var manager = new AccountContextManager(service);

        var selection = await manager.SelectAndVerifyAsync(AccountContextRequest.ForAgy("work"));

        Assert.False(selection.IsResolved);
        Assert.Contains("not a saved agy-profile profile", selection.FailureReason);
        Assert.Empty(service.SwitchCalls);
    }

    [Fact]
    public async Task Agy_WhenSwitchFails_FailsClosed()
    {
        var service = new FakeAgyProfileService
        {
            IsAvailable = true,
            ActiveProfile = "personal",
            SwitchResult = AgyProfileSwitchResult.Failure("agy is running; switch refused")
        };
        var manager = new AccountContextManager(service);

        var selection = await manager.SelectAndVerifyAsync(AccountContextRequest.ForAgy("work"));

        Assert.False(selection.IsResolved);
        Assert.Contains("agy is running", selection.FailureReason);
    }

    [Fact]
    public async Task Agy_WhenSwitchConfirmationDiffers_FailsClosed()
    {
        var service = new FakeAgyProfileService
        {
            IsAvailable = true,
            ActiveProfile = "personal",
            SwitchResult = AgyProfileSwitchResult.Success("other")
        };
        var manager = new AccountContextManager(service);

        var selection = await manager.SelectAndVerifyAsync(AccountContextRequest.ForAgy("work"));

        Assert.False(selection.IsResolved);
        Assert.Contains("not confirmed", selection.FailureReason);
    }

    [Fact]
    public async Task Agy_InvalidProfileName_FailsClosedWithoutInvokingUtility()
    {
        var service = new FakeAgyProfileService { IsAvailable = true };
        var manager = new AccountContextManager(service);

        var selection = await manager.SelectAndVerifyAsync(
            new AccountContextRequest(LLMWorkGUI.Domain.Enums.BackendType.StarCliProxy, AccountContextKind.Agy, "work; rm"));

        Assert.False(selection.IsResolved);
        Assert.Contains("invalid", selection.FailureReason);
        Assert.Empty(service.SwitchCalls);
        Assert.False(service.ListCalled);
    }

    [Fact]
    public async Task VerifyContext_WhenAgyInactive_DoesNotSwitch()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "personal" };
        var manager = new AccountContextManager(service);

        var verification = await manager.VerifyContextAsync(AccountContextRequest.ForAgy("work"));

        Assert.False(verification.IsResolved);
        Assert.Contains("not the active profile", verification.FailureReason);
        Assert.Empty(service.SwitchCalls);
    }

    [Fact]
    public async Task ExecuteSerializedAsync_SerializesConcurrentOperationsUnderOneLock()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "personal" };
        var manager = new AccountContextManager(service);
        var request = AccountContextRequest.ForAgy("work");

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = manager.ExecuteSerializedAsync(
            request,
            async (selection, _) =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
                return selection;
            });

        await firstEntered.Task;

        var second = manager.ExecuteSerializedAsync(
            request,
            (selection, _) =>
            {
                secondEntered.TrySetResult();
                return Task.FromResult(selection);
            });

        // The second select→verify→launch must wait for the first account operation to finish.
        var earlyCompletion = await Task.WhenAny(secondEntered.Task, Task.Delay(150));
        Assert.NotSame(secondEntered.Task, earlyCompletion);

        releaseFirst.TrySetResult();

        var firstResult = await first;
        var secondResult = await second;

        Assert.True(firstResult.IsResolved);
        Assert.True(secondResult.IsResolved);

        // Exactly one agy-profile switch happened: the second request observed the switched
        // profile after the lock was released.
        Assert.Equal(new[] { "work" }, service.SwitchCalls);
    }

    [Fact]
    public async Task SelectAndVerifyAsync_HoldsAccountLockAgainstSerializedExecution()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "personal" };
        var manager = new AccountContextManager(service);
        var request = AccountContextRequest.ForAgy("work");

        var switchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSwitch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        service.BeforeSwitchAsync = async () =>
        {
            switchEntered.TrySetResult();
            await releaseSwitch.Task;
        };

        var direct = manager.SelectAndVerifyAsync(request);

        await switchEntered.Task;

        var serializedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serialized = manager.ExecuteSerializedAsync(
            request,
            (selection, _) =>
            {
                serializedEntered.TrySetResult();
                return Task.FromResult(selection);
            });

        // The serialized select→verify→launch must wait for the direct profile switch to finish.
        var earlyCompletion = await Task.WhenAny(serializedEntered.Task, Task.Delay(150));
        Assert.NotSame(serializedEntered.Task, earlyCompletion);

        releaseSwitch.TrySetResult();

        var directResult = await direct;
        var serializedResult = await serialized;

        Assert.True(directResult.IsResolved);
        Assert.True(serializedResult.IsResolved);

        // Exactly one switch: the serialized request observed the switched profile after the lock
        // was released, so no deadlock and no interleaved profile change occurred.
        Assert.Equal(new[] { "work" }, service.SwitchCalls);
    }

    [Fact]
    public async Task ExecuteSerializedAsync_PassesRequiresNewSessionSelectionToOperation()
    {
        var service = new FakeAgyProfileService { IsAvailable = true, ActiveProfile = "personal" };
        var manager = new AccountContextManager(service);

        var selection = await manager.ExecuteSerializedAsync(
            AccountContextRequest.ForAgy("work"),
            (current, _) => Task.FromResult(current));

        Assert.True(selection.IsResolved);
        Assert.True(selection.RequiresNewSession);
        Assert.Equal(new[] { "work" }, service.SwitchCalls);
    }

    [Fact]
    public async Task ExecuteSerializedAsync_ReleasesLockAfterOperationFailure()
    {
        var manager = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        var request = AccountContextRequest.ForCodex("missing");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ExecuteSerializedAsync<AccountContextSelection>(
                request,
                (_, _) => throw new InvalidOperationException("boom")));

        var second = await manager.ExecuteSerializedAsync(
            request,
            (selection, _) => Task.FromResult(selection.IsResolved));

        Assert.False(second);
    }

    [Fact]
    public async Task ListAgyContexts_FiltersInvalidProfileNamesAndAvailability()
    {
        var unavailable = new AccountContextManager(new FakeAgyProfileService { IsAvailable = false });
        Assert.Empty(await unavailable.ListAgyContextsAsync());

        var service = new FakeAgyProfileService
        {
            IsAvailable = true,
            Profiles =
            [
                new AgyProfileSummary("work", IsActive: true),
                new AgyProfileSummary("bad name", IsActive: false)
            ]
        };

        var manager = new AccountContextManager(service);
        var contexts = await manager.ListAgyContextsAsync();

        var context = Assert.Single(contexts);
        Assert.Equal("work", context.ProfileName);
    }

    private sealed class FakeAgyProfileService : IAgyProfileService
    {
        public bool IsAvailable { get; set; } = true;

        public string? ExecutablePath { get; set; } = @"C:\tools\agy-profile.cmd";

        public string? AvailabilityBlocker => IsAvailable ? null : MissingUtilityBlocker;

        public string? ActiveProfile { get; set; } = "personal";

        public IReadOnlyList<AgyProfileSummary> Profiles { get; set; } =
        [
            new AgyProfileSummary("personal", IsActive: false),
            new AgyProfileSummary("work", IsActive: false)
        ];

        public AgyProfileSwitchResult SwitchResult { get; set; } = AgyProfileSwitchResult.Success("work");

        public List<string> SwitchCalls { get; } = new();

        public Func<Task>? BeforeSwitchAsync { get; set; }

        public bool ListCalled { get; private set; }

        public Task<string?> GetActiveProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ActiveProfile);

        public Task<IReadOnlyList<AgyProfileSummary>> ListProfilesAsync(
            CancellationToken cancellationToken = default)
        {
            ListCalled = true;

            return Task.FromResult(Profiles);
        }

        public async Task<AgyProfileSwitchResult> SwitchProfileAsync(
            string profileName,
            CancellationToken cancellationToken = default)
        {
            SwitchCalls.Add(profileName);

            if (BeforeSwitchAsync is not null)
            {
                await BeforeSwitchAsync().ConfigureAwait(false);
            }

            if (SwitchResult.IsSwitched)
            {
                ActiveProfile = SwitchResult.ActiveProfile;
            }

            return SwitchResult;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "LLMWorkGUI.Tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
