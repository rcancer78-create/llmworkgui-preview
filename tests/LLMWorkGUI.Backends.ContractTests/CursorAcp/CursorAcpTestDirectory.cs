namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

internal sealed class CursorAcpTestDirectory : IDisposable
{
    public CursorAcpTestDirectory()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "llmworkgui-cursor-acp-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string GetPath(params string[] segments) =>
        Path.Combine([Root, .. segments]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
