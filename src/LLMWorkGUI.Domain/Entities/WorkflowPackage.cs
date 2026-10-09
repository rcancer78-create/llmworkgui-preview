using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

public sealed class WorkflowPackage
{
    public WorkflowPackage(
        string id,
        string name,
        string? description,
        IReadOnlyList<string> tags,
        WorkflowSourceType sourceType,
        string originalHash,
        string originalBlobId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(tags);

        Id = DomainGuard.NotBlank(id, nameof(id));
        Name = DomainGuard.NotBlank(name, nameof(name));
        Description = DomainGuard.OptionalNotBlank(description, nameof(description));
        Tags = CopyTags(tags);
        SourceType = sourceType;
        OriginalHash = WorkflowBlobIdGuard.NotBlankBlobId(originalHash, nameof(originalHash));
        OriginalBlobId = WorkflowBlobIdGuard.NotBlankBlobId(originalBlobId, nameof(originalBlobId));

        if (!string.Equals(OriginalHash, OriginalBlobId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Original blob id must be identical to the original hash.",
                nameof(originalBlobId));
        }

        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string Id { get; }

    public string Name { get; }

    public string? Description { get; }

    public IReadOnlyList<string> Tags { get; }

    public WorkflowSourceType SourceType { get; }

    public string OriginalHash { get; }

    public string OriginalBlobId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    private static IReadOnlyList<string> CopyTags(IReadOnlyList<string> tags)
    {
        var copy = DomainGuard.NotNullList(tags, nameof(tags));

        foreach (var tag in copy)
        {
            DomainGuard.NotBlank(tag, nameof(tags));
        }

        return copy;
    }
}

internal static class WorkflowBlobIdGuard
{
    private const string BlobIdPrefix = "sha256:";
    private const int Sha256HexLength = 64;

    public static string NotBlankBlobId(string value, string parameterName)
    {
        DomainGuard.NotBlank(value, parameterName);

        if (!IsValidBlobId(value))
        {
            throw new ArgumentException(
                "Workflow blob id must have the form 'sha256:<64 lowercase hex characters>'.",
                parameterName);
        }

        return value;
    }

    public static bool IsValidBlobId(string value)
    {
        if (!value.StartsWith(BlobIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (value.Length != BlobIdPrefix.Length + Sha256HexLength)
        {
            return false;
        }

        foreach (var character in value.AsSpan(BlobIdPrefix.Length))
        {
            var isHex = character is >= '0' and <= '9' or >= 'a' and <= 'f';

            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
