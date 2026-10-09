using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.StarCliProxy;

public sealed class StarCliProxySecretBoundaryTests
{
    private const string Sentinel = "sk-proxy-config-sentinel-7c1e";

    [Fact]
    public void ConfigurationBinding_IgnoresPlaintextProxyApiKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{StarCliProxyOptions.SectionName}:ApiKey"] = Sentinel,
                [$"{StarCliProxyOptions.SectionName}:Hostname"] = "localhost",
                [$"{StarCliProxyOptions.SectionName}:Port"] = "8317"
            })
            .Build();

        var options = new StarCliProxyOptions();
        configuration.GetSection(StarCliProxyOptions.SectionName).Bind(options);

        Assert.Equal("localhost", options.Hostname);
        Assert.Equal(8317, options.Port);
        Assert.Null(options.ApiKeySecretReference);

        foreach (var property in typeof(StarCliProxyOptions).GetProperties())
        {
            if (property.GetValue(options) is string text)
            {
                Assert.DoesNotContain(Sentinel, text);
            }
        }
    }

    [Fact]
    public async Task WriteAsync_ReplacesConfigWithoutExposingPartialOrLegacySecretFile()
    {
        using var directory = new TestDirectory();
        var first = await StarCliProxyConfigWriter.WriteAsync(
            directory.Root, "config.yaml", "127.0.0.1", 11111, ["codex"],
            "synthetic-admin", "synthetic-api", CancellationToken.None);

        var previousIdentity = FileIndex(first);
        var replacement = await StarCliProxyConfigWriter.WriteAsync(
            directory.Root, "config.yaml", "127.0.0.1", 22222, ["codex"],
            "synthetic-admin", "synthetic-api", CancellationToken.None);

        Assert.Equal(first, replacement);
        var published = await File.ReadAllTextAsync(replacement);
        Assert.Contains("port: 22222", published);
        Assert.DoesNotContain("port: 11111", published);
        Assert.Contains("${PROXY_API_KEY}", published);
        Assert.DoesNotContain("synthetic-api", published);
        Assert.False(File.Exists(Path.Combine(directory.Root, StarCliProxyConfigWriter.EnvironmentFileName)));
        Assert.DoesNotContain(
            Directory.GetFiles(directory.Root),
            path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(previousIdentity, FileIndex(replacement));
    }

    private static ulong FileIndex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
        {
            throw new IOException("The configuration file identity could not be read.");
        }

        return ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        out ByHandleFileInformation information);
}
