using System.Text.Json;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.StarCliProxy;

public sealed class StarCliProxyYamlTests
{
    [Fact]
    public async Task QuotedHostAndProviderCannotInjectYamlKeys()
    {
        using var directory = new TestDirectory();
        const string injected = "host\"\nauth:\n  enabled: false\n#";
        var path = await StarCliProxyConfigWriter.WriteAsync(directory.Root, "config.yaml", injected,
            12345, [injected], "synthetic-admin", "synthetic-api", CancellationToken.None);
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Contains("  host: " + JsonSerializer.Serialize(injected), lines);
        Assert.DoesNotContain("  enabled: false", lines);
        Assert.Equal(1, lines.Count(line => line == "auth:"));
    }
}
