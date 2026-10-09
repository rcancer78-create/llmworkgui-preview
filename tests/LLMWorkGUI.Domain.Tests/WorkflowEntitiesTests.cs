using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class WorkflowEntitiesTests
{
    private static readonly string ValidHash = "sha256:" + new string('a', 64);

    [Fact]
    public void WorkflowSourceType_DefinesCanonicalValues()
    {
        var values = Enum.GetValues<WorkflowSourceType>();

        Assert.Equal(
            new[] { WorkflowSourceType.ZipArchive, WorkflowSourceType.Directory, WorkflowSourceType.SyntheticDraft },
            values);
    }

    [Fact]
    public void WorkflowPackage_Constructor_SetsAllProperties()
    {
        var createdAt = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(5);

        var package = new WorkflowPackage(
            "pkg-1",
            "Release Workflow",
            "Coordinates release tasks",
            new[] { "release", "ci" },
            WorkflowSourceType.ZipArchive,
            ValidHash,
            ValidHash,
            createdAt,
            updatedAt);

        Assert.Equal("pkg-1", package.Id);
        Assert.Equal("Release Workflow", package.Name);
        Assert.Equal("Coordinates release tasks", package.Description);
        Assert.Equal(new[] { "release", "ci" }, package.Tags);
        Assert.Equal(WorkflowSourceType.ZipArchive, package.SourceType);
        Assert.Equal(ValidHash, package.OriginalHash);
        Assert.Equal(ValidHash, package.OriginalBlobId);
        Assert.Equal(createdAt, package.CreatedAtUtc);
        Assert.Equal(updatedAt, package.UpdatedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WorkflowPackage_Constructor_RejectsBlankId(string invalidId)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowPackage(
            invalidId,
            "Workflow",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.Directory,
            ValidHash,
            ValidHash,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WorkflowPackage_Constructor_RejectsBlankName(string invalidName)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowPackage(
            "pkg-1",
            invalidName,
            null,
            Array.Empty<string>(),
            WorkflowSourceType.Directory,
            ValidHash,
            ValidHash,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void WorkflowPackage_Constructor_RejectsBlankDescription()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowPackage(
            "pkg-1",
            "Workflow",
            "   ",
            Array.Empty<string>(),
            WorkflowSourceType.Directory,
            ValidHash,
            ValidHash,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("sha256:abc")]
    [InlineData("md5:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void WorkflowPackage_Constructor_RejectsMalformedOriginalHash(string invalidHash)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowPackage(
            "pkg-1",
            "Workflow",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            invalidHash,
            invalidHash,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void WorkflowPackage_Constructor_RejectsBlobIdThatDiffersFromHash()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowPackage(
            "pkg-1",
            "Workflow",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            ValidHash,
            "sha256:" + new string('b', 64),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void WorkflowPackage_Constructor_RejectsNullTags()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkflowPackage(
            "pkg-1",
            "Workflow",
            null,
            null!,
            WorkflowSourceType.ZipArchive,
            ValidHash,
            ValidHash,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void WorkflowPackage_Constructor_RejectsBlankTags()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowPackage(
            "pkg-1",
            "Workflow",
            null,
            new[] { "valid", "  " },
            WorkflowSourceType.ZipArchive,
            ValidHash,
            ValidHash,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void WorkflowPackage_Constructor_CopiesTagsList()
    {
        var tags = new List<string> { "alpha" };

        var package = new WorkflowPackage(
            "pkg-1",
            "Workflow",
            null,
            tags,
            WorkflowSourceType.ZipArchive,
            ValidHash,
            ValidHash,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        tags.Add("beta");

        Assert.Equal(new[] { "alpha" }, package.Tags);
    }

    [Fact]
    public void WorkflowVersion_Constructor_SetsAllProperties()
    {
        var createdAt = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var activatedAt = createdAt.AddMinutes(1);

        var version = new WorkflowVersion(
            "ver-1",
            "pkg-1",
            3,
            ValidHash,
            ValidHash,
            WorkflowSourceType.SyntheticDraft,
            "{\"entrypoints\":[]}",
            "[]",
            "{}",
            "{\"compatible\":true}",
            "{\"origin\":\"seed\"}",
            createdAt,
            activatedAt);

        Assert.Equal("ver-1", version.Id);
        Assert.Equal("pkg-1", version.WorkflowPackageId);
        Assert.Equal(3, version.VersionNumber);
        Assert.Equal(ValidHash, version.BlobId);
        Assert.Equal(ValidHash, version.OriginalHash);
        Assert.Equal(WorkflowSourceType.SyntheticDraft, version.SourceType);
        Assert.Equal("{\"entrypoints\":[]}", version.EntrypointsJson);
        Assert.Equal("[]", version.DeclaredRolesJson);
        Assert.Equal("{}", version.BindingsJson);
        Assert.Equal("{\"compatible\":true}", version.CompatibilityReportJson);
        Assert.Equal("{\"origin\":\"seed\"}", version.CreationMetadataJson);
        Assert.Equal(createdAt, version.CreatedAtUtc);
        Assert.Equal(activatedAt, version.ActivatedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WorkflowVersion_Constructor_RejectsVersionNumberBelowOne(int invalidVersionNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowVersion(
            "ver-1",
            "pkg-1",
            invalidVersionNumber,
            ValidHash,
            ValidHash,
            WorkflowSourceType.ZipArchive,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WorkflowVersion_Constructor_RejectsBlankPackageId(string invalidPackageId)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowVersion(
            "ver-1",
            invalidPackageId,
            1,
            ValidHash,
            ValidHash,
            WorkflowSourceType.ZipArchive,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            null));
    }

    [Fact]
    public void WorkflowVersion_Constructor_RejectsMalformedBlobId()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowVersion(
            "ver-1",
            "pkg-1",
            1,
            "sha256:not-hex",
            ValidHash,
            WorkflowSourceType.ZipArchive,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            null));
    }
}
