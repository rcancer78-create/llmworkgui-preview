using System.Text;
using System.IO.Compression;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowAdaptationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateNullCannotSwitchTheScannedEncodingBeforePreviewOrModelDispatch(bool dispatch)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var body = new string('a', 8192) + "b\0 " + SecretValue + " \n";
        if ((Encoding.UTF8.GetByteCount(body) & 1) != 0) body += " ";
        var bytes = Encoding.UTF8.GetBytes(body);
        Assert.Equal(8193, Array.IndexOf(bytes, (byte)0));
        var scan = await new WorkflowSecretScanner(new SensitiveDataFilter())
            .ScanFilesAsync(new Dictionary<string, byte[]> { ["README.md"] = bytes });
        // This byte pattern is accepted as BOM-less UTF-16 by the scanner. It must never then
        // be uploaded under a different UTF-8 interpretation exposing its ASCII key.
        Assert.Empty(scan.Findings);
        var raw = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            using var stream = archive.CreateEntry("README.md", CompressionLevel.NoCompression).Open();
            stream.Write(bytes);
        });
        var (_, version) = await SeedWorkflowAsync("version-1", raw);
        if (!dispatch)
        {
            var preview = await _service.PreparePreSendPreviewAsync(version.Id, "acct-1", AdaptationGoal.Balanced);
            Assert.DoesNotContain(SecretValue, preview.PromptPreview, StringComparison.Ordinal);
            Assert.DoesNotContain("README.md", preview.IncludedFiles);
            return;
        }
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var result = await _service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        try
        {
            Assert.All(_modelInvoker.Requests.Single().Messages,
                message => Assert.DoesNotContain(SecretValue, message.Content, StringComparison.Ordinal));
        }
        finally { await _service.DiscardSessionAsync(result.SessionId); }
    }
}
