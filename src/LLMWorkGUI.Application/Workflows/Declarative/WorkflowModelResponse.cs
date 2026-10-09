using System.Security.Cryptography;
using System.Text;

namespace LLMWorkGUI.Application.Workflows.Declarative;

/// <summary>Bounded answer text, excluding reasoning. Content and hash are data, never a verdict or identity proof.</summary>
public sealed class WorkflowModelResponse
{
    public const int MaximumUtf8Bytes = 64 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public WorkflowModelResponse(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (content.Length > MaximumUtf8Bytes)
            throw new ArgumentException("Reviewer response exceeds the storage bound.", nameof(content));
        var bytes = Utf8.GetBytes(content);
        if (bytes.Length > MaximumUtf8Bytes)
            throw new ArgumentException("Reviewer response exceeds the UTF-8 storage bound.", nameof(content));
        Content = content;
        Utf8Bytes = bytes.Length;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
    }

    public string Content { get; }
    public string Sha256 { get; }
    public int Utf8Bytes { get; }
    public override string ToString() => $"Reviewer response ({Utf8Bytes} UTF-8 bytes; SHA-256 {Sha256})";
}
