using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Infrastructure.Data;

namespace LLMWorkGUI.Infrastructure.Hosting;

/// <summary>
/// Records an unfinished primary GUI lifetime beside its database. The marker survives process kill;
/// only a successful graceful shutdown clears it. Secondary/view-only instances never change it.
/// It carries a random ownership token, not prompts, credentials or native execution identity.
/// </summary>
public sealed class ApplicationRunMarker
{
    private readonly string _path;
    private readonly IApplicationInstanceGuard _instanceGuard;
    private readonly string _ownershipToken = Guid.NewGuid().ToString("N");
    private bool _begun;

    public ApplicationRunMarker(ISqliteConnectionFactory connectionFactory, IApplicationInstanceGuard instanceGuard)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _instanceGuard = instanceGuard ?? throw new ArgumentNullException(nameof(instanceGuard));
        _path = connectionFactory.DatabasePath + ".running";
    }

    public bool Begin()
    {
        if (_instanceGuard.IsViewOnly || !_instanceGuard.IsPrimarySupervisor) return false;
        _instanceGuard.EnsureSupervisorPermitted();
        if (_begun) throw new InvalidOperationException("The application lifetime has already begun.");
        var wasInterrupted = File.Exists(_path);
        // Replace only while the primary supervisor owns the database's single-instance guard.
        var temporary = _path + "." + _ownershipToken + ".tmp";
        try
        {
            WithFileAccessRetry(() => File.WriteAllText(temporary, _ownershipToken));
            WithFileAccessRetry(() =>
            {
                _instanceGuard.EnsureSupervisorPermitted();
                File.Move(temporary, _path, overwrite: true);
            });
            _begun = true;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { /* Best-effort cleanup must not replace the original startup failure. */ }
        }
        return wasInterrupted;
    }

    public void CompleteGracefulShutdown()
    {
        if (!_begun || _instanceGuard.IsViewOnly || !_instanceGuard.IsPrimarySupervisor) return;
        _instanceGuard.EnsureSupervisorPermitted();
        // A stale owner must never erase a later lifetime's marker.
        WithFileAccessRetry(() =>
        {
            if (File.Exists(_path) && string.Equals(File.ReadAllText(_path), _ownershipToken, StringComparison.Ordinal))
                File.Delete(_path);
        });
        _begun = false;
    }

    private static void WithFileAccessRetry(Action operation)
    {
        // Windows scanners/readers can briefly deny a rename or delete after a handle is closed.
        // Keep the old marker intact and bound startup/shutdown delay to 375 ms per operation.
        for (var attempt = 0; ; attempt++)
        {
            try { operation(); return; }
            catch (Exception exception) when (attempt < 4
                && exception is IOException or UnauthorizedAccessException
                && (exception.HResult & 0xffff) is 5 or 32 or 33)
            { Thread.Sleep(25 << attempt); }
        }
    }
}
