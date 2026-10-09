using LLMWorkGUI.Application.Cli;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal sealed class FakeCliExecutableLocator : ICliExecutableLocator
{
    private readonly Dictionary<string, string?> _paths = new(StringComparer.OrdinalIgnoreCase);

    public Exception? ExceptionToThrow { get; set; }

    public List<string> RequestedNames { get; } = new();

    public FakeCliExecutableLocator WithExecutable(string executableName, string? resolvedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        _paths[executableName] = resolvedPath;

        return this;
    }

    public Task<string?> LocateAsync(string executableName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        cancellationToken.ThrowIfCancellationRequested();
        RequestedNames.Add(executableName);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(_paths.TryGetValue(executableName, out var path) ? path : null);
    }
}
