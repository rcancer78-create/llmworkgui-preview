using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

internal sealed class FakeCursorExecutableResolver : ICursorExecutableResolver
{
    private readonly Func<CancellationToken, Task<CursorAcpExecutableResolution>> _handler;

    public FakeCursorExecutableResolver(Func<CancellationToken, Task<CursorAcpExecutableResolution>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
    }

    public int ResolveCount { get; private set; }

    public Task<CursorAcpExecutableResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        ResolveCount++;
        return _handler(cancellationToken);
    }

    public static FakeCursorExecutableResolver Found(string executablePath, string version) =>
        new(_ => Task.FromResult(CursorAcpExecutableResolution.Found(executablePath, version)));

    public static FakeCursorExecutableResolver Missing() =>
        new(_ => Task.FromResult(CursorAcpExecutableResolution.Missing()));

    public static FakeCursorExecutableResolver Unresolved(string blocker) =>
        new(_ => Task.FromResult(CursorAcpExecutableResolution.Unresolved(blocker)));

    public static FakeCursorExecutableResolver Throwing(Exception exception) =>
        new(_ => Task.FromException<CursorAcpExecutableResolution>(exception));
}
