using System.IO.Compression;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class SafeArchiveValidatorTests
{
    private readonly SafeArchiveValidator _validator = new();

    [Theory]
    [InlineData("folder/a.txt", "FOLDER/A.txt")]
    [InlineData("folder/a.txt", "folder\\a.txt")]
    [InlineData("folder", "folder/a.txt")]
    [InlineData("folder/a.txt", "FOLDER")]
    [InlineData("folder/", "folder")]
    public void ValidateArchive_RejectsPortablePathCollisions(string first, string second)
    {
        var bytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            archive.CreateEntry(first);
            archive.CreateEntry(second);
        });
        using var archive = Open(bytes);
        Assert.Throws<WorkflowValidationException>(() => _validator.ValidateArchive(archive, bytes.Length));
    }

    [Fact]
    public void ValidateArchive_AcceptsExplicitDirectoryAndChildren()
    {
        var bytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            archive.CreateEntry("folder/");
            archive.CreateEntry("folder/a.txt");
            archive.CreateEntry("folder/b.txt");
        });
        using var archive = Open(bytes);
        _validator.ValidateArchive(archive, bytes.Length);
    }

    [Fact]
    public void ValidateArchive_AcceptsWellFormedArchive()
    {
        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(archive, "workflow.json", "{\"name\":\"demo\"}");
            WorkflowTestArchiveFactory.AddEntry(archive, "prompts/system.md", "You are helpful.");
            WorkflowTestArchiveFactory.AddEntry(archive, "prompts/reviewer.md", "Review carefully.");
        });

        using var archive = Open(archiveBytes);

        _validator.ValidateArchive(archive, archiveBytes.Length);
    }

    [Fact]
    public void ValidateArchive_RejectsArchiveAboveMaximumArchiveSize()
    {
        var archiveBytes = CreateSingleEntryArchive("workflow.json");
        using var archive = Open(archiveBytes);

        Assert.Throws<WorkflowValidationException>(() => _validator.ValidateArchive(
            archive,
            WorkflowImportLimits.MaxArchiveSizeBytes + 1L));
    }

    [Theory]
    [InlineData("../../escape.txt")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll")]
    [InlineData("folder/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\absolute\\evil.txt")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("test.txt:stream")]
    [InlineData("%2e%2e%2fescape.txt")]
    [InlineData("bad\u0001name.txt")]
    [InlineData("folder/NUL.txt")]
    [InlineData("CON")]
    [InlineData("COM1.json")]
    [InlineData("LPT9")]
    public void ValidateArchive_RejectsDangerousEntryPaths(string entryName)
    {
        var archiveBytes = CreateSingleEntryArchive(entryName);
        using var archive = Open(archiveBytes);

        Assert.Throws<WorkflowValidationException>(() => _validator.ValidateArchive(archive, archiveBytes.Length));
    }

    [Fact]
    public void ValidateArchive_RejectsDecompressionBombBeforeInflatingPayload()
    {
        var archiveBytes = CreateSingleEntryArchive(
            "bomb.bin",
            entry => WriteZeroFilled(entry, 5 * 1024 * 1024));

        using var archive = Open(archiveBytes);

        var exception = Assert.Throws<WorkflowValidationException>(
            () => _validator.ValidateArchive(archive, archiveBytes.Length));

        Assert.Contains("compression ratio", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateArchive_RejectsEntryAboveSingleFileLimit()
    {
        var archiveBytes = CreateSingleEntryArchive(
            "large.bin",
            entry => WriteZeroFilled(entry, WorkflowImportLimits.MaxSingleFileBytes + 1));

        using var archive = Open(archiveBytes);

        Assert.Throws<WorkflowValidationException>(() => _validator.ValidateArchive(archive, archiveBytes.Length));
    }

    [Fact]
    public void ValidateArchive_RejectsArchiveAboveFileCountLimit()
    {
        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            for (var i = 0; i <= WorkflowImportLimits.MaxFileCount; i++)
            {
                archive.CreateEntry($"file-{i:D5}.txt");
            }
        });

        using var archive = Open(archiveBytes);

        Assert.Throws<WorkflowValidationException>(() => _validator.ValidateArchive(archive, archiveBytes.Length));
    }

    [Fact]
    public void ValidateArchive_AcceptsArchiveAtExactFileCountLimit()
    {
        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            for (var i = 0; i < WorkflowImportLimits.MaxFileCount; i++)
            {
                archive.CreateEntry($"file-{i:D5}.txt");
            }
        });

        using var archive = Open(archiveBytes);

        _validator.ValidateArchive(archive, archiveBytes.Length);
    }

    [Fact]
    public void ValidateArchive_RejectsArchiveAboveTotalUncompressedSize()
    {
        const int entrySize = 20 * 1024 * 1024;

        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            for (var i = 0; i < 26; i++)
            {
                archive.CreateEntry($"file-{i:D2}.bin");
            }
        });

        archiveBytes = WorkflowTestArchiveFactory.PatchCentralDirectorySizes(archiveBytes, entrySize, entrySize);

        using var archive = Open(archiveBytes);

        var exception = Assert.Throws<WorkflowValidationException>(
            () => _validator.ValidateArchive(archive, archiveBytes.Length));

        Assert.Contains("total uncompressed size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateArchive_RejectsUnixSymlinkEntry()
    {
        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            var entry = archive.CreateEntry("link", CompressionLevel.Optimal);
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
        });

        using var archive = Open(archiveBytes);

        Assert.Throws<WorkflowValidationException>(() => _validator.ValidateArchive(archive, archiveBytes.Length));
    }

    [Fact]
    public async Task ValidateArchive_ExceptionMessagesDoNotLeakFileSystemPaths()
    {
        using var directory = new TestDirectory();
        var archivePath = directory.GetPath("malicious.zip");

        await File.WriteAllBytesAsync(archivePath, CreateSingleEntryArchive("../../escape.txt"));

        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var exception = Assert.Throws<WorkflowValidationException>(
            () => _validator.ValidateArchive(archive, stream.Length));

        Assert.DoesNotContain(directory.Root, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", exception.Message, StringComparison.Ordinal);

        if (exception.InnerException is not null)
        {
            Assert.DoesNotContain(directory.Root, exception.InnerException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":\\", exception.InnerException.Message, StringComparison.Ordinal);
        }
    }

    private static ZipArchive Open(byte[] archiveBytes)
    {
        return new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
    }

    private static byte[] CreateSingleEntryArchive(string entryName)
    {
        return CreateSingleEntryArchive(entryName, static _ => { });
    }

    private static byte[] CreateSingleEntryArchive(string entryName, Action<ZipArchiveEntry> writeEntry)
    {
        return WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            writeEntry(entry);
        });
    }

    private static void WriteZeroFilled(ZipArchiveEntry entry, int sizeBytes)
    {
        using var stream = entry.Open();
        var buffer = new byte[1024 * 1024];
        var remaining = sizeBytes;

        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, buffer.Length);
            stream.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }
}
