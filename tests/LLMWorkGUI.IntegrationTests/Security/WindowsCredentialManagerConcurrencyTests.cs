using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerConcurrencyTests
{
    [WindowsCredentialFact]
    public async Task IndependentInstancesPreserveParallelOwnersWritesAndRotations()
    {
        var failures = new ConcurrentQueue<string>();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async owner =>
        {
            var api = new WindowsCredentialManagerApi();
            var target = CredentialManagerTarget.Prefix + "concurrency-test-" + Guid.NewGuid().ToString("N");
            try
            {
                for (var cycle = 0; cycle < 30; cycle++)
                {
                    var expected = $"synthetic-concurrent-owner-{owner}-cycle-{cycle}";
                    var write = api.Write(target, expected, CredentialManagerTarget.UserName, CredentialManagerTarget.Comment);
                    // Different targets and instances, overlapping native calls and continuations.
                    await Task.Delay(1);
                    var read = api.Read(target);
                    try
                    {
                        var matches = read.Blob is not null && Encoding.UTF8.GetString(read.Blob) == expected;
                        if (!write.IsSuccess || !read.IsSuccess || !matches)
                        {
                            failures.Enqueue($"Owner {owner}, cycle {cycle}: write={write.Outcome}, read={read.Outcome}, valueMatches={matches}");
                        }
                    }
                    finally
                    {
                        if (read.Blob is not null)
                        {
                            Array.Clear(read.Blob);
                        }
                    }
                }
            }
            finally
            {
                var deleted = api.Delete(target);
                if (deleted.Outcome is not (CredentialManagerOutcome.Success or CredentialManagerOutcome.NotFound))
                {
                    failures.Enqueue($"Owner {owner}: cleanup={deleted.Outcome}");
                }
            }
        }));
        Assert.True(failures.IsEmpty, $"Parallel native credential boundary failed {failures.Count} checks. "
            + string.Join("; ", failures.Take(8)));
    }

    private sealed class WindowsCredentialFactAttribute : FactAttribute
    {
        public WindowsCredentialFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires the real Windows Credential Manager.";
            }
        }
    }
}
