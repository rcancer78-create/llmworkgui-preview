namespace LLMWorkGUI.IntegrationTests;

internal sealed class TestDirectory : IDisposable
{
    private const int MaxDeleteAttempts = 10;

    public TestDirectory()
    {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "LLMWorkGUI.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string GetPath(string fileName)
    {
        return System.IO.Path.Combine(Root, fileName);
    }

    /// <summary>
    /// Removes the temporary directory.
    ///
    /// SQLite releases the underlying OS file handle only once the owning connection has been
    /// finalized, so resetting the connection pool is not by itself sufficient to guarantee that
    /// <c>llmworkgui.db</c> is closed by the time this method runs. Pending finalizers are therefore
    /// drained before the first delete attempt and again between retries, with a growing backoff.
    ///
    /// Cleanup is best-effort by contract: this method never throws. A leftover directory under the
    /// user's temp folder is harmless, whereas throwing from teardown would be reported by xUnit as a
    /// failure of whichever test happened to execute last in the class, making the suite
    /// non-deterministic and unusable as a release gate.
    /// </summary>
    public void Dispose()
    {
        DrainPendingFinalizers();

        for (var attempt = 0; attempt < MaxDeleteAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                if (attempt == MaxDeleteAttempts - 1)
                {
                    return;
                }

                WaitBeforeRetry(attempt);
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt == MaxDeleteAttempts - 1)
                {
                    return;
                }

                WaitBeforeRetry(attempt);
            }
        }
    }

    private static void WaitBeforeRetry(int attempt)
    {
        Thread.Sleep(50 * (attempt + 1));
        DrainPendingFinalizers();
    }

    private static void DrainPendingFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
