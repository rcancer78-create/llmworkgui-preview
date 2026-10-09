using System;
using System.Linq;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Headless behaviour of the unified Diff / Artifact Viewer: unified diff parsing, line numbering,
/// inline and side-by-side layouts, artifact metadata, clipboard actions and honest placeholders.
/// </summary>
public sealed class DiffArtifactViewerViewModelTests
{
    private const string SampleDiff =
        "diff --git a/app.txt b/app.txt\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/app.txt\n" +
        "+++ b/app.txt\n" +
        "@@ -1,4 +1,5 @@\n" +
        " line one\n" +
        "-line two\n" +
        "+line two changed\n" +
        "+line three added\n" +
        " line four";

    [Fact]
    public void LoadDiff_ParsesLineKindsAndNumbersThemFromTheHunkHeader()
    {
        var viewModel = new DiffArtifactViewerViewModel();

        viewModel.LoadDiff(SampleDiff, title: "app.txt diff");

        Assert.True(viewModel.HasDiff);
        Assert.True(viewModel.IsDiffMode);
        Assert.Equal("app.txt diff", viewModel.Title);
        Assert.Equal(10, viewModel.UnifiedLines.Count);

        Assert.Equal(DiffLineKind.Meta, viewModel.UnifiedLines[0].Kind);
        Assert.Equal(DiffLineKind.FileHeader, viewModel.UnifiedLines[2].Kind);
        Assert.Equal(DiffLineKind.HunkHeader, viewModel.UnifiedLines[4].Kind);
        Assert.Equal("@@ -1,4 +1,5 @@", viewModel.UnifiedLines[4].Text);

        var context = viewModel.UnifiedLines[5];
        Assert.Equal(DiffLineKind.Context, context.Kind);
        Assert.Equal("1", context.OldLineNumberDisplay);
        Assert.Equal("1", context.NewLineNumberDisplay);

        var deletion = viewModel.UnifiedLines[6];
        Assert.True(deletion.IsDeletion);
        Assert.Equal("-", deletion.Marker);
        Assert.Equal("2", deletion.OldLineNumberDisplay);
        Assert.Equal(string.Empty, deletion.NewLineNumberDisplay);

        var firstAddition = viewModel.UnifiedLines[7];
        Assert.True(firstAddition.IsAddition);
        Assert.Equal("+", firstAddition.Marker);
        Assert.Equal(string.Empty, firstAddition.OldLineNumberDisplay);
        Assert.Equal("2", firstAddition.NewLineNumberDisplay);

        var secondAddition = viewModel.UnifiedLines[8];
        Assert.Equal("3", secondAddition.NewLineNumberDisplay);

        Assert.Equal("+2 -1 · 1 hunk(s) · 2 context line(s)", viewModel.DiffSummaryDisplay);
    }

    [Fact]
    public void LoadDiff_BuildsAlignedSideBySideRows()
    {
        var viewModel = new DiffArtifactViewerViewModel();

        viewModel.LoadDiff(SampleDiff);

        Assert.True(viewModel.IsUnifiedMode);
        Assert.Equal(9, viewModel.SideBySideRows.Count);

        // Five full-width header rows, then the aligned lines.
        Assert.All(viewModel.SideBySideRows.Take(5), row => Assert.True(row.IsFullWidth));

        var contextRow = viewModel.SideBySideRows[5];
        Assert.False(contextRow.IsFullWidth);
        Assert.Same(contextRow.Left, contextRow.Right);
        Assert.Equal("line one", contextRow.LeftText);

        // The deletion is paired with the first addition; the second addition stays right-only.
        var pairedRow = viewModel.SideBySideRows[6];
        Assert.Equal("line two", pairedRow.LeftText);
        Assert.Equal("-", pairedRow.LeftMarker);
        Assert.Equal("line two changed", pairedRow.RightText);
        Assert.Equal("+", pairedRow.RightMarker);

        var unpairedRow = viewModel.SideBySideRows[7];
        Assert.False(unpairedRow.HasLeft);
        Assert.True(unpairedRow.HasRight);
        Assert.Equal("line three added", unpairedRow.RightText);

        viewModel.UseSideBySideLayoutCommand.Execute(null);
        Assert.True(viewModel.IsSideBySideMode);
        Assert.False(viewModel.IsUnifiedMode);

        viewModel.UseUnifiedLayoutCommand.Execute(null);
        Assert.True(viewModel.IsUnifiedMode);
    }

    [Fact]
    public void LoadDiff_WithoutHunkHeader_KeepsAllLinesAsMeta()
    {
        var viewModel = new DiffArtifactViewerViewModel();

        viewModel.LoadDiff("Binary files a/logo.png and b/logo.png differ");

        Assert.Single(viewModel.UnifiedLines);
        Assert.Equal(DiffLineKind.Meta, viewModel.UnifiedLines[0].Kind);
        Assert.Equal("+0 -0 · 0 hunk(s) · 0 context line(s)", viewModel.DiffSummaryDisplay);
    }

    [Fact]
    public void LoadArtifact_ReportsNameMetadataAndAComputedSha256()
    {
        var viewModel = new DiffArtifactViewerViewModel();

        viewModel.LoadArtifact(
            "plan.json",
            "{\"stage\":\"plan\"}",
            changeStatus: ArtifactChangeStatus.Modified);

        Assert.True(viewModel.IsArtifactMode);
        Assert.False(viewModel.IsDiffMode);
        Assert.True(viewModel.HasArtifact);
        Assert.Equal("plan.json", viewModel.ArtifactNameDisplay);
        Assert.Equal("Изменён", viewModel.ArtifactChangeStatusDisplay);
        Assert.Equal("16 B", viewModel.ArtifactSizeDisplay);

        // The hash is computed from the content and looks like a SHA-256 fingerprint.
        Assert.StartsWith("sha256:", viewModel.ArtifactSha256Display, StringComparison.Ordinal);
        Assert.Equal(7 + 64, viewModel.ArtifactSha256Display.Length);
    }

    [Fact]
    public void LoadArtifact_PrefersProvidedHashAndSize_AndKeepsUnknownStatusHonest()
    {
        var viewModel = new DiffArtifactViewerViewModel();

        viewModel.LoadArtifact("model.bin", "abcdefgh", sizeBytes: 8, sha256: "sha256:deadbeef");

        Assert.Equal("8 B", viewModel.ArtifactSizeDisplay);
        Assert.Equal("sha256:deadbeef", viewModel.ArtifactSha256Display);
        Assert.Equal(DiffArtifactViewerViewModel.NotReportedPlaceholder, viewModel.ArtifactChangeStatusDisplay);

        Assert.Throws<ArgumentException>(() => viewModel.LoadArtifact("  ", "content"));
        Assert.Throws<ArgumentNullException>(() => viewModel.LoadArtifact("name", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => viewModel.LoadArtifact("name", "content", sizeBytes: -1));
    }

    [Fact]
    public void CopyCommands_WriteThroughTheClipboardAndReportTheAction()
    {
        var clipboard = new RecordingClipboard();
        var viewModel = new DiffArtifactViewerViewModel(clipboard);

        viewModel.LoadDiff(SampleDiff);
        viewModel.CopyDiffCommand.Execute(null);

        Assert.Equal(SampleDiff, clipboard.LastText);
        Assert.Equal("Скопировано в буфер обмена.", viewModel.CopyNotice);
        Assert.True(viewModel.HasCopyNotice);

        viewModel.LoadArtifact("plan.json", "{\"stage\":\"plan\"}");
        viewModel.CopyContentCommand.Execute(null);

        Assert.Equal("{\"stage\":\"plan\"}", clipboard.LastText);
        Assert.Equal("Скопировано в буфер обмена.", viewModel.CopyNotice);
    }

    [Fact]
    public void WithoutAClipboardOrContent_CopyCommandsAreSafeNoOps()
    {
        var viewModel = new DiffArtifactViewerViewModel();

        viewModel.CopyDiffCommand.Execute(null);
        viewModel.CopyContentCommand.Execute(null);

        Assert.Equal(string.Empty, viewModel.CopyNotice);
    }

    [Fact]
    public void Clear_ResetsBothPanesAndTheEmptyMessage()
    {
        var viewModel = new DiffArtifactViewerViewModel(new RecordingClipboard());

        viewModel.LoadDiff(SampleDiff);
        viewModel.LoadArtifact("plan.json", "{\"stage\":\"plan\"}");
        viewModel.Clear();

        Assert.False(viewModel.HasDiff);
        Assert.False(viewModel.HasArtifact);
        Assert.False(viewModel.HasContent);
        Assert.Equal("Просмотр diff / артефактов", viewModel.Title);
        Assert.Equal(DiffArtifactViewerViewModel.NoDiffMessage, viewModel.ContentEmptyMessage);
        Assert.Equal(DiffArtifactViewerViewModel.NotReportedPlaceholder, viewModel.ArtifactSizeDisplay);
        Assert.Equal(DiffArtifactViewerViewModel.NotReportedPlaceholder, viewModel.ArtifactSha256Display);
        Assert.Empty(viewModel.UnifiedLines);
        Assert.Empty(viewModel.SideBySideRows);
    }

    [Fact]
    public void ModeSwitchesAndEmptyMessagesFollowTheActivePane()
    {
        var viewModel = new DiffArtifactViewerViewModel();

        Assert.Equal(DiffArtifactViewerViewModel.NoDiffMessage, viewModel.ContentEmptyMessage);

        viewModel.ShowArtifactCommand.Execute(null);
        Assert.True(viewModel.IsArtifactMode);
        Assert.Equal(DiffArtifactViewerViewModel.NoArtifactMessage, viewModel.ContentEmptyMessage);
        Assert.False(viewModel.HasContent);

        viewModel.ShowDiffCommand.Execute(null);
        Assert.True(viewModel.IsDiffMode);
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? LastText { get; private set; }

        public void SetText(string text)
        {
            LastText = text;
        }
    }
}
