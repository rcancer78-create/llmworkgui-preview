using LLMWorkGUI.Infrastructure.StarCliProxy;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.StarCliProxy;

public sealed class WindowsStarCliProxyExecutableResolverContractTests
{
    [Fact]
    public void LocalAppDataFallbackFindsTheSupportedCmdWrapperWithoutReadingConfiguration()
    {
        var localAppData = @"C:\Users\alice\AppData\Local";
        var expected = Path.Combine(
            localAppData,
            WindowsStarCliProxyExecutableResolver.InstallDirectoryName,
            "star-cliproxy.cmd");
        var reads = new List<string>();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expected };

        var resolver = Create(
            name =>
            {
                reads.Add(name);
                return name switch
                {
                    "LOCALAPPDATA" => localAppData,
                    "PATH" => @"C:\Windows\System32",
                    _ => null,
                };
            },
            existing.Contains);

        var result = resolver.Resolve();

        Assert.True(result.IsAvailable);
        Assert.Equal(expected, result.ExecutablePath);
        Assert.DoesNotContain("STAR_CLIPROXY_CONFIG", reads);
        Assert.DoesNotContain("STAR_CLIPROXY_API_KEY", reads);
    }

    [Fact]
    public void ExplicitEnvironmentPathWinsOverPathAndLocalAppData()
    {
        var explicitPath = @"C:\tools\star-cliproxy.exe";
        var pathCandidate = @"C:\bin\star-cliproxy.cmd";
        var localCandidate = @"C:\Users\alice\AppData\Local\star-cliproxy\star-cliproxy.ps1";
        var existing = new HashSet<string>(
            [explicitPath, pathCandidate, localCandidate],
            StringComparer.OrdinalIgnoreCase);

        var resolver = Create(
            name => name switch
            {
                WindowsStarCliProxyExecutableResolver.ExecutablePathVariable => explicitPath,
                "PATH" => @"C:\bin",
                "LOCALAPPDATA" => @"C:\Users\alice\AppData\Local",
                _ => null,
            },
            existing.Contains);

        var result = resolver.Resolve();

        Assert.True(result.IsAvailable);
        Assert.Equal(explicitPath, result.ExecutablePath);
    }

    [Fact]
    public void PathLookupSupportsCmdBeforeLocalAppDataFallback()
    {
        var pathCandidate = @"C:\bin\star-cliproxy.cmd";
        var localCandidate = @"C:\Users\alice\AppData\Local\star-cliproxy\star-cliproxy.exe";
        var existing = new HashSet<string>([pathCandidate, localCandidate], StringComparer.OrdinalIgnoreCase);

        var resolver = Create(
            name => name switch
            {
                "PATH" => @"C:\bin",
                "LOCALAPPDATA" => @"C:\Users\alice\AppData\Local",
                _ => null,
            },
            existing.Contains);

        var result = resolver.Resolve();

        Assert.True(result.IsAvailable);
        Assert.Equal(pathCandidate, result.ExecutablePath);
    }

    [Fact]
    public void MissingGatewayIsReportedWithoutInventingAnExecutable()
    {
        var resolver = Create(
            name => name switch
            {
                "PATH" => @"C:\bin",
                "LOCALAPPDATA" => @"C:\Users\alice\AppData\Local",
                _ => null,
            },
            _ => false);

        var result = resolver.Resolve();

        Assert.False(result.IsAvailable);
        Assert.Null(result.ExecutablePath);
    }

    private static WindowsStarCliProxyExecutableResolver Create(
        Func<string, string?> environment,
        Func<string, bool> fileExists) =>
        new(environment, fileExists);
}
