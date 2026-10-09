using LLMWorkGUI.Infrastructure.Agy;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Agy;

public sealed class WindowsAgyProfileExecutableResolverTests
{
    private const string LocalAppData = @"C:\Users\user\AppData\Local";

    [Fact]
    public void Resolve_WhenUtilityIsOnPath_ReturnsPathEntryBeforeLocalAppData()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\tools\agy-profile\agy-profile.cmd",
            Path.Combine(LocalAppData, "agy-profile", "agy-profile.cmd")
        };

        var resolver = CreateResolver(
            pathVariable: @"C:\tools\agy-profile;C:\other",
            fileExists: files.Contains);

        var resolution = resolver.Resolve();

        Assert.True(resolution.IsAvailable);
        Assert.Equal(@"C:\tools\agy-profile\agy-profile.cmd", resolution.ExecutablePath);
    }

    [Fact]
    public void Resolve_SkipsQuotedAndEmptyPathEntries()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\agy tools\agy-profile.cmd"
        };

        var resolver = CreateResolver(
            pathVariable: @";;  ;""C:\Program Files\agy tools""",
            fileExists: files.Contains);

        Assert.Equal(@"C:\Program Files\agy tools\agy-profile.cmd", resolver.Resolve().ExecutablePath);
    }

    [Fact]
    public void Resolve_WhenOnlyPs1OnPath_IsStillDetected()
    {
        var resolver = CreateResolver(
            pathVariable: @"C:\tools",
            fileExists: candidate => string.Equals(
                candidate,
                @"C:\tools\agy-profile.ps1",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"C:\tools\agy-profile.ps1", resolver.Resolve().ExecutablePath);
    }

    [Fact]
    public void Resolve_FallsBackToLocalAppDataCommandScript()
    {
        var expected = Path.Combine(LocalAppData, "agy-profile", "agy-profile.cmd");

        var resolver = CreateResolver(
            pathVariable: null,
            fileExists: candidate => string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, resolver.Resolve().ExecutablePath);
    }

    [Fact]
    public void Resolve_FallsBackToLocalAppDataPowerShellScript()
    {
        var expected = Path.Combine(LocalAppData, "agy-profile", "agy-profile.ps1");

        var resolver = CreateResolver(
            pathVariable: string.Empty,
            fileExists: candidate => string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, resolver.Resolve().ExecutablePath);
    }

    [Fact]
    public void Resolve_WhenNothingExists_ReportsUnavailable()
    {
        var resolver = CreateResolver(pathVariable: @"C:\tools", fileExists: _ => false);

        var resolution = resolver.Resolve();

        Assert.False(resolution.IsAvailable);
        Assert.Null(resolution.ExecutablePath);
    }

    [Fact]
    public void Resolve_WhenLocalAppDataIsMissing_DoesNotThrow()
    {
        var resolver = new WindowsAgyProfileExecutableResolver(
            localAppDataProvider: () => null,
            pathProvider: () => null,
            fileExists: _ => true);

        Assert.False(resolver.Resolve().IsAvailable);
    }

    [Fact]
    public void Constructor_RejectsNullProviders()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new WindowsAgyProfileExecutableResolver(null!, () => null, _ => false));
        Assert.Throws<ArgumentNullException>(() =>
            new WindowsAgyProfileExecutableResolver(() => null, null!, _ => false));
        Assert.Throws<ArgumentNullException>(() =>
            new WindowsAgyProfileExecutableResolver(() => null, () => null, null!));
    }

    private static WindowsAgyProfileExecutableResolver CreateResolver(
        string? pathVariable,
        Func<string, bool> fileExists)
    {
        return new WindowsAgyProfileExecutableResolver(
            localAppDataProvider: () => LocalAppData,
            pathProvider: () => pathVariable,
            fileExists: fileExists);
    }
}
