using System.Collections;
using System.Text;
using LLMWorkGUI.Application.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowSecretScannerTests
{
    [Theory]
    [InlineData("../secret.env")]
    [InlineData("/secret.env")]
    [InlineData("a/../secret.env")]
    [InlineData("C:\\secret.env")]
    public async Task Review_ScannerRejectsPathsThatCannotIdentifyArchiveMembers(string path)
    {
        await Assert.ThrowsAsync<WorkflowValidationException>(() => _scanner.ScanFilesAsync(
            new Dictionary<string, byte[]> { [path] = Encoding.UTF8.GetBytes("password = private-value") }));
    }

    [Theory]
    [InlineData("secrets.env", "./secrets.env")]
    [InlineData("config/secrets.env", "config\\secrets.env")]
    [InlineData("secrets.env", "SECRETS.env")]
    public async Task Review_ScannerCannotCollapseSecretFileIntoCleanCanonicalAlias(string first, string second)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [first] = Encoding.UTF8.GetBytes("password = private-value"),
            [second] = Encoding.UTF8.GetBytes("ordinary clean documentation")
        };
        await Assert.ThrowsAsync<WorkflowValidationException>(() => _scanner.ScanFilesAsync(files));
    }

    [Fact]
    public async Task Review_ScannerObservesCancellationAfterInputAdmissionBeforeMatching()
    {
        using var cancellation = new CancellationTokenSource();
        var files = new CancelAfterAdmissionFiles(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _scanner.ScanFilesAsync(files, cancellation.Token));
        Assert.True(files.AdmissionCompleted);
    }

    private sealed class CancelAfterAdmissionFiles(CancellationTokenSource cancellation)
        : IReadOnlyDictionary<string, byte[]>
    {
        private readonly Dictionary<string, byte[]> _files = new()
        {
            ["config.env"] = Encoding.UTF8.GetBytes(new string('a', 32 * 1024) + " password = private-value")
        };
        public bool AdmissionCompleted { get; private set; }
        public int Count => _files.Count;
        public IEnumerable<string> Keys => _files.Keys;
        public IEnumerable<byte[]> Values => _files.Values;
        public byte[] this[string key] => _files[key];
        public bool ContainsKey(string key) => _files.ContainsKey(key);
        public bool TryGetValue(string key, out byte[] value) => _files.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator()
        {
            foreach (var pair in _files)
                yield return pair;
            AdmissionCompleted = true;
            cancellation.Cancel();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
