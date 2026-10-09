using LLMWorkGUI.App.ViewModels;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class DesktopDiffReviewRegressionTests
{
    [Fact]
    public void SelectingDiffAfterArtifactCannotExposeThePreviousArtifact()
    {
        var viewer = new DiffArtifactViewerViewModel();
        viewer.LoadArtifact("previous.txt", "Previous event content");
        viewer.LoadDiff("@@ -1 +1 @@\n-old\n+new", "Current diff");
        viewer.ShowArtifactCommand.Execute(null);

        Assert.False(viewer.HasArtifact);
        Assert.Empty(viewer.ArtifactContent);
        Assert.False(viewer.HasContent);
    }

    [Fact]
    public void SelectingArtifactAfterDiffCannotExposeThePreviousDiff()
    {
        var viewer = new DiffArtifactViewerViewModel();
        viewer.LoadDiff("@@ -1 +1 @@\n-old\n+new", "Previous diff");
        viewer.LoadArtifact("current.txt", "Current artifact content");
        viewer.ShowDiffCommand.Execute(null);

        Assert.False(viewer.HasDiff);
        Assert.Empty(viewer.RawDiffText);
        Assert.Empty(viewer.UnifiedLines);
        Assert.Empty(viewer.SideBySideRows);
        Assert.False(viewer.HasContent);
    }

    [Theory]
    [InlineData("@@ -999999999999 +1 @@")]
    [InlineData("@@ -1 +999999999999 @@")]
    public void UntrustedOverflowingHunkNumbersDoNotFaultTheViewer(string header)
    {
        var viewer = new DiffArtifactViewerViewModel();

        var exception = Record.Exception(() => viewer.LoadDiff(header + "\n-old\n+new"));

        Assert.Null(exception);
        Assert.Equal(header, viewer.UnifiedLines[0].Text);
        Assert.All(viewer.UnifiedLines, line =>
        {
            Assert.False(line.OldLineNumber < 0);
            Assert.False(line.NewLineNumber < 0);
        });
    }

    [Theory]
    [InlineData("diff --git a/second.txt b/second.txt\nindex 111..222 100644\n")]
    [InlineData("")]
    public void MultipleFileSectionsKeepHeadersOutOfChangeCounts(string secondMetadata)
    {
        var viewer = new DiffArtifactViewerViewModel();
        viewer.LoadDiff("--- a/first.txt\n+++ b/first.txt\n@@ -1 +1 @@\n-old-one\n+new-one\n"
            + secondMetadata + "--- a/second.txt\n+++ b/second.txt\n@@ -20 +30 @@\n-old-two\n+new-two");

        Assert.Equal(4, viewer.UnifiedLines.Count(line => line.Kind == DiffLineKind.FileHeader));
        Assert.Equal(2, viewer.AddedLineCount);
        Assert.Equal(2, viewer.RemovedLineCount);
        var changedSecond = Assert.Single(viewer.UnifiedLines, line => line.Text == "new-two");
        Assert.Equal(30, changedSecond.NewLineNumber);
        Assert.Equal(20, Assert.Single(viewer.UnifiedLines, line => line.Text == "old-two").OldLineNumber);
    }

    [Fact]
    public void FileHeaderLookingContentInsideAnUnfinishedHunkStaysAChange()
    {
        var viewer = new DiffArtifactViewerViewModel();
        viewer.LoadDiff("--- a/file.txt\n+++ b/file.txt\n@@ -1,2 +1,2 @@\n--- actual deleted text\n+++ actual added text\n shared");

        Assert.Equal(2, viewer.UnifiedLines.Count(line => line.Kind == DiffLineKind.FileHeader));
        Assert.Equal(1, viewer.RemovedLineCount);
        Assert.Equal(1, viewer.AddedLineCount);
        Assert.Equal("-- actual deleted text", Assert.Single(viewer.UnifiedLines, line => line.IsDeletion).Text);
        Assert.Equal("++ actual added text", Assert.Single(viewer.UnifiedLines, line => line.IsAddition).Text);
    }
}
