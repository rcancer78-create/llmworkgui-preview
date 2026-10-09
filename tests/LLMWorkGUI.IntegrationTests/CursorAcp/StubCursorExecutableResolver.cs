using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

internal sealed class StubCursorExecutableResolver : ICursorExecutableResolver
{
    private readonly CursorAcpExecutableResolution _resolution;

    public StubCursorExecutableResolver(CursorAcpExecutableResolution resolution)
    {
        _resolution = resolution;
    }

    public static StubCursorExecutableResolver Found(string executablePath) =>
        new(CursorAcpExecutableResolution.Found(executablePath, "2026.09.15-d2fe57e"));

    public static StubCursorExecutableResolver Missing() =>
        new(CursorAcpExecutableResolution.Missing());

    public Task<CursorAcpExecutableResolution> ResolveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_resolution);
}
