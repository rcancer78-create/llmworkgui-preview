using System.Globalization;
using System.Windows;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// One entry of the workflow archive tree. The preview service returns a flat node list, so the tree is
/// reconstructed by grouping paths and flattened again with an indentation depth for the virtualized list.
/// </summary>
public sealed class WorkflowTreeNodeViewModel : ObservableObject
{
    private const double IndentPerLevel = 14d;

    public WorkflowTreeNodeViewModel(WorkflowTreeNode node, int depth)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (depth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "Tree depth must not be negative.");
        }

        Node = node;
        Depth = depth;
        Indent = new Thickness(depth * IndentPerLevel, 0, 0, 0);
    }

    public WorkflowTreeNode Node { get; }

    public string Path => Node.Path;

    public string Name => Node.Name;

    public bool IsDirectory => Node.IsDirectory;

    public long SizeBytes => Node.SizeBytes;

    public int Depth { get; }

    public Thickness Indent { get; }

    public string KindDisplay => IsDirectory ? "Directory" : "File";

    public string SizeDisplay => FormatSize(SizeBytes);

    private static string FormatSize(long sizeBytes)
    {
        if (sizeBytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sizeBytes} B");
        }

        if (sizeBytes < 1024 * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sizeBytes / 1024d:0.#} KiB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{sizeBytes / (1024d * 1024d):0.#} MiB");
    }
}
