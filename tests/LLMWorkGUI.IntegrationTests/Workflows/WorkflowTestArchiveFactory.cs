using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LLMWorkGUI.IntegrationTests.Workflows;

internal static class WorkflowTestArchiveFactory
{
    private const uint CentralDirectoryFileHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;

    public static byte[] CreateArchive(Action<ZipArchive> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            configure(archive);
        }

        return buffer.ToArray();
    }

    public static void AddEntry(ZipArchive archive, string name, string content)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    public static void AddZeroFilledEntry(ZipArchive archive, string name, int sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
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

    public static string ToBlobId(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    public static byte[] PatchCentralDirectorySizes(
        byte[] archiveBytes,
        int compressedSizeBytes,
        int uncompressedSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);

        var patched = (byte[])archiveBytes.Clone();
        var endOfCentralDirectory = FindEndOfCentralDirectory(patched);
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(endOfCentralDirectory + 10, 2));
        var position = BinaryPrimitives.ReadInt32LittleEndian(patched.AsSpan(endOfCentralDirectory + 16, 4));

        for (var i = 0; i < entryCount; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(position, 4)) != CentralDirectoryFileHeaderSignature)
            {
                throw new InvalidOperationException("Unexpected ZIP central directory layout.");
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(position + 28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(position + 30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(position + 32, 2));

            BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(position + 20, 4), compressedSizeBytes);
            BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(position + 24, 4), uncompressedSizeBytes);

            position += 46 + nameLength + extraLength + commentLength;
        }

        return patched;
    }

    private static int FindEndOfCentralDirectory(byte[] archiveBytes)
    {
        for (var i = archiveBytes.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(archiveBytes.AsSpan(i, 4)) == EndOfCentralDirectorySignature)
            {
                return i;
            }
        }

        throw new InvalidOperationException("End of central directory record was not found.");
    }
}
