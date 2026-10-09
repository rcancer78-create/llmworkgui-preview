using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.Application.Observability;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>Which document the unified viewer currently shows.</summary>
public enum DiffArtifactViewerMode
{
    Diff,
    Artifact
}

/// <summary>Layout of the diff pane: the classic inline unified view or two aligned columns.</summary>
public enum DiffDisplayMode
{
    Unified,
    SideBySide
}

/// <summary>Classification of one unified diff line.</summary>
public enum DiffLineKind
{
    Meta,
    FileHeader,
    HunkHeader,
    Context,
    Addition,
    Deletion,
    NoNewline
}

/// <summary>Reported change status of an artifact; unknown values stay "Not reported".</summary>
public enum ArtifactChangeStatus
{
    NotReported,
    Unchanged,
    Added,
    Modified,
    Deleted
}

/// <summary>
/// Unified diff and artifact viewer: inline/side-by-side diffs with honest line numbering and an
/// artifact pane showing content, byte size, SHA-256 and change status. Copy actions go through
/// <see cref="IClipboardService"/> so the view model stays testable.
/// </summary>
public sealed class DiffArtifactViewerViewModel : ObservableObject
{
    public const string NotReportedPlaceholder = ObservableRunProjection.NotReportedPlaceholder;

    public const string NoDiffMessage = "Diff не загружен.";

    public const string NoArtifactMessage = "Артефакт не загружен.";

    private readonly IClipboardService? _clipboard;

    private string _title = "Просмотр diff / артефактов";
    private DiffArtifactViewerMode _mode = DiffArtifactViewerMode.Diff;
    private DiffDisplayMode _displayMode = DiffDisplayMode.Unified;
    private string _rawDiffText = string.Empty;
    private string _artifactName = string.Empty;
    private string _artifactContent = string.Empty;
    private long? _artifactSizeBytes;
    private string? _artifactSha256;
    private ArtifactChangeStatus _artifactChangeStatus = ArtifactChangeStatus.NotReported;
    private string _copyNotice = string.Empty;

    public DiffArtifactViewerViewModel(IClipboardService? clipboard = null)
    {
        _clipboard = clipboard;

        ShowDiffCommand = new RelayCommand(() => Mode = DiffArtifactViewerMode.Diff);
        ShowArtifactCommand = new RelayCommand(() => Mode = DiffArtifactViewerMode.Artifact);
        UseUnifiedLayoutCommand = new RelayCommand(() => DisplayMode = DiffDisplayMode.Unified);
        UseSideBySideLayoutCommand = new RelayCommand(() => DisplayMode = DiffDisplayMode.SideBySide);
        CopyDiffCommand = new RelayCommand(CopyDiff);
        CopyContentCommand = new RelayCommand(CopyContent);
        ClearCommand = new RelayCommand(Clear);
    }

    public ObservableCollection<DiffLineViewModel> UnifiedLines { get; } = new();

    public ObservableCollection<DiffSideBySideRowViewModel> SideBySideRows { get; } = new();

    public ICommand ShowDiffCommand { get; }

    public ICommand ShowArtifactCommand { get; }

    public ICommand UseUnifiedLayoutCommand { get; }

    public ICommand UseSideBySideLayoutCommand { get; }

    public ICommand CopyDiffCommand { get; }

    public ICommand CopyContentCommand { get; }

    public ICommand ClearCommand { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public DiffArtifactViewerMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsDiffMode));
                OnPropertyChanged(nameof(IsArtifactMode));
                OnPropertyChanged(nameof(ContentEmptyMessage));
                OnPropertyChanged(nameof(HasContent));
            }
        }
    }

    public bool IsDiffMode => Mode == DiffArtifactViewerMode.Diff;

    public bool IsArtifactMode => Mode == DiffArtifactViewerMode.Artifact;

    public DiffDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (SetProperty(ref _displayMode, value))
            {
                OnPropertyChanged(nameof(IsUnifiedMode));
                OnPropertyChanged(nameof(IsSideBySideMode));
            }
        }
    }

    public bool IsUnifiedMode => DisplayMode == DiffDisplayMode.Unified;

    public bool IsSideBySideMode => DisplayMode == DiffDisplayMode.SideBySide;

    public string RawDiffText => _rawDiffText;

    public bool HasDiff => !string.IsNullOrWhiteSpace(_rawDiffText);

    public int AddedLineCount { get; private set; }

    public int RemovedLineCount { get; private set; }

    public int ContextLineCount { get; private set; }

    public int HunkCount { get; private set; }

    public string DiffSummaryDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"+{AddedLineCount} -{RemovedLineCount} · {HunkCount} hunk(s) · {ContextLineCount} context line(s)");

    public string ArtifactName => _artifactName;

    public string ArtifactNameDisplay => string.IsNullOrWhiteSpace(_artifactName)
        ? NotReportedPlaceholder
        : _artifactName;

    public string ArtifactContent => _artifactContent;

    public bool HasArtifact => !string.IsNullOrWhiteSpace(_artifactContent);

    public string ArtifactSizeDisplay => FormatSize(_artifactSizeBytes);

    public string ArtifactSha256Display => string.IsNullOrWhiteSpace(_artifactSha256)
        ? NotReportedPlaceholder
        : _artifactSha256;

    public ArtifactChangeStatus ArtifactChangeStatus
    {
        get => _artifactChangeStatus;
        private set
        {
            if (SetProperty(ref _artifactChangeStatus, value))
            {
                OnPropertyChanged(nameof(ArtifactChangeStatusDisplay));
            }
        }
    }

    public string ArtifactChangeStatusDisplay => ArtifactChangeStatus switch
    {
        ArtifactChangeStatus.Unchanged => "Без изменений",
        ArtifactChangeStatus.Added => "Добавлен",
        ArtifactChangeStatus.Modified => "Изменён",
        ArtifactChangeStatus.Deleted => "Удалён",
        _ => NotReportedPlaceholder
    };

    public bool HasContent => IsDiffMode ? HasDiff : HasArtifact;

    public string ContentEmptyMessage => IsDiffMode ? NoDiffMessage : NoArtifactMessage;

    public string CopyNotice
    {
        get => _copyNotice;
        private set
        {
            if (SetProperty(ref _copyNotice, value))
            {
                OnPropertyChanged(nameof(HasCopyNotice));
            }
        }
    }

    public bool HasCopyNotice => !string.IsNullOrWhiteSpace(CopyNotice);

    /// <summary>Loads and parses a unified diff; both layouts are prepared in one pass.</summary>
    public void LoadDiff(string unifiedDiff, string? title = null, string? artifactName = null)
    {
        ArgumentNullException.ThrowIfNull(unifiedDiff);

        Clear();

        _rawDiffText = unifiedDiff;
        _artifactName = artifactName ?? string.Empty;
        Title = string.IsNullOrWhiteSpace(title)
            ? string.IsNullOrWhiteSpace(artifactName) ? "Diff" : artifactName
            : title;

        var lines = UnifiedDiffParser.Parse(unifiedDiff);

        UnifiedLines.Clear();
        SideBySideRows.Clear();

        foreach (var line in lines)
        {
            UnifiedLines.Add(line);
        }

        foreach (var row in DiffSideBySideBuilder.Build(lines))
        {
            SideBySideRows.Add(row);
        }

        AddedLineCount = lines.Count(line => line.Kind == DiffLineKind.Addition);
        RemovedLineCount = lines.Count(line => line.Kind == DiffLineKind.Deletion);
        ContextLineCount = lines.Count(line => line.Kind == DiffLineKind.Context);
        HunkCount = lines.Count(line => line.Kind == DiffLineKind.HunkHeader);

        Mode = DiffArtifactViewerMode.Diff;
        CopyNotice = string.Empty;

        OnPropertyChanged(nameof(RawDiffText));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(DiffSummaryDisplay));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ContentEmptyMessage));
    }

    /// <summary>
    /// Loads an artifact. Missing size or hash are computed from the content so the pane never shows
    /// a fabricated value, and the change status stays "Not reported" unless the caller proved it.
    /// </summary>
    public void LoadArtifact(
        string name,
        string content,
        long? sizeBytes = null,
        string? sha256 = null,
        ArtifactChangeStatus changeStatus = ArtifactChangeStatus.NotReported)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        if (sizeBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Artifact size must not be negative.");
        }

        Clear();
        _artifactName = name;
        _artifactContent = content;
        _artifactSizeBytes = sizeBytes ?? Encoding.UTF8.GetByteCount(content);
        _artifactSha256 = string.IsNullOrWhiteSpace(sha256)
            ? "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant()
            : sha256;
        ArtifactChangeStatus = changeStatus;
        Title = name;
        Mode = DiffArtifactViewerMode.Artifact;
        CopyNotice = string.Empty;

        OnPropertyChanged(nameof(ArtifactName));
        OnPropertyChanged(nameof(ArtifactNameDisplay));
        OnPropertyChanged(nameof(ArtifactContent));
        OnPropertyChanged(nameof(HasArtifact));
        OnPropertyChanged(nameof(ArtifactSizeDisplay));
        OnPropertyChanged(nameof(ArtifactSha256Display));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ContentEmptyMessage));
    }

    public void Clear()
    {
        _rawDiffText = string.Empty;
        _artifactName = string.Empty;
        _artifactContent = string.Empty;
        _artifactSizeBytes = null;
        _artifactSha256 = null;
        ArtifactChangeStatus = ArtifactChangeStatus.NotReported;
        UnifiedLines.Clear();
        SideBySideRows.Clear();
        AddedLineCount = 0;
        RemovedLineCount = 0;
        ContextLineCount = 0;
        HunkCount = 0;
        Mode = DiffArtifactViewerMode.Diff;
        DisplayMode = DiffDisplayMode.Unified;
        Title = "Просмотр diff / артефактов";
        CopyNotice = string.Empty;

        OnPropertyChanged(nameof(RawDiffText));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(ArtifactName));
        OnPropertyChanged(nameof(ArtifactNameDisplay));
        OnPropertyChanged(nameof(ArtifactContent));
        OnPropertyChanged(nameof(HasArtifact));
        OnPropertyChanged(nameof(ArtifactSizeDisplay));
        OnPropertyChanged(nameof(ArtifactSha256Display));
        OnPropertyChanged(nameof(DiffSummaryDisplay));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ContentEmptyMessage));
    }

    public void CopyDiff()
    {
        if (!HasDiff || _clipboard is null)
        {
            return;
        }

        _clipboard.SetText(_rawDiffText);
        CopyNotice = "Скопировано в буфер обмена.";
    }

    public void CopyContent()
    {
        if (!HasArtifact || _clipboard is null)
        {
            return;
        }

        _clipboard.SetText(_artifactContent);
        CopyNotice = "Скопировано в буфер обмена.";
    }

    private static string FormatSize(long? sizeBytes)
    {
        if (sizeBytes is not { } size)
        {
            return NotReportedPlaceholder;
        }

        if (size < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{size} B");
        }

        if (size < 1024 * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{size / 1024.0:0.0} KiB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size / (1024.0 * 1024.0):0.0} MiB");
    }
}

/// <summary>One rendered line of the unified diff, with its old/new line numbers when they exist.</summary>
public sealed class DiffLineViewModel
{
    public DiffLineViewModel(
        int index,
        DiffLineKind kind,
        string text,
        int? oldLineNumber,
        int? newLineNumber)
    {
        ArgumentNullException.ThrowIfNull(text);

        Index = index;
        Kind = kind;
        Text = text;
        OldLineNumber = oldLineNumber;
        NewLineNumber = newLineNumber;
    }

    public int Index { get; }

    public DiffLineKind Kind { get; }

    public string Text { get; }

    public int? OldLineNumber { get; }

    public int? NewLineNumber { get; }

    public bool IsAddition => Kind == DiffLineKind.Addition;

    public bool IsDeletion => Kind == DiffLineKind.Deletion;

    public bool IsContext => Kind == DiffLineKind.Context;

    public bool IsHunkHeader => Kind == DiffLineKind.HunkHeader;

    public bool IsFileHeader => Kind == DiffLineKind.FileHeader;

    public bool IsMeta => Kind == DiffLineKind.Meta;

    public bool IsNoNewline => Kind == DiffLineKind.NoNewline;

    public string Marker => Kind switch
    {
        DiffLineKind.Addition => "+",
        DiffLineKind.Deletion => "-",
        DiffLineKind.Context => " ",
        _ => string.Empty
    };

    public string OldLineNumberDisplay => OldLineNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string NewLineNumberDisplay => NewLineNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string GutterDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"{OldLineNumberDisplay,5} {NewLineNumberDisplay,5}");

    public string ChangeKindDisplay => Kind.ToString();
}

/// <summary>One aligned row of the side-by-side layout; either column can be empty for unpaired runs.</summary>
public sealed class DiffSideBySideRowViewModel
{
    public DiffSideBySideRowViewModel(
        DiffLineViewModel? left,
        DiffLineViewModel? right,
        string? fullWidthText = null,
        DiffLineKind fullWidthKind = DiffLineKind.Meta)
    {
        Left = left;
        Right = right;
        FullWidthText = fullWidthText;
        FullWidthKind = fullWidthKind;
    }

    public DiffLineViewModel? Left { get; }

    public DiffLineViewModel? Right { get; }

    public string? FullWidthText { get; }

    public DiffLineKind FullWidthKind { get; }

    public bool IsFullWidth => FullWidthText is not null;

    public bool HasLeft => Left is not null;

    public bool HasRight => Right is not null;

    public bool IsAddition => Left?.IsAddition == true || Right?.IsAddition == true;

    public bool IsDeletion => Left?.IsDeletion == true || Right?.IsDeletion == true;

    public bool IsHunkHeader => IsFullWidth && FullWidthKind == DiffLineKind.HunkHeader;

    public bool IsFileHeader => IsFullWidth && FullWidthKind == DiffLineKind.FileHeader;

    public string LeftLineNumberDisplay => Left?.OldLineNumberDisplay ?? string.Empty;

    public string RightLineNumberDisplay => Right?.NewLineNumberDisplay ?? string.Empty;

    public string LeftMarker => Left?.Marker ?? string.Empty;

    public string RightMarker => Right?.Marker ?? string.Empty;

    public string LeftText => Left?.Text ?? string.Empty;

    public string RightText => Right?.Text ?? string.Empty;
}

/// <summary>
/// Minimal, allocation-conscious parser of unified diff text. File headers, hunk headers, additions,
/// deletions and context lines are recognized; line numbers follow the hunk headers exactly.
/// </summary>
internal static class UnifiedDiffParser
{
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@\s+-(?<old>\d+)(?:,(?<oldCount>\d+))?\s+\+(?<new>\d+)(?:,(?<newCount>\d+))?\s+@@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<DiffLineViewModel> Parse(string unifiedDiff)
    {
        ArgumentNullException.ThrowIfNull(unifiedDiff);

        var lines = new List<DiffLineViewModel>();
        var rawLines = unifiedDiff
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var oldNumber = 0;
        var newNumber = 0;
        var oldRemaining = 0;
        var newRemaining = 0;
        var inHunk = false;

        for (var index = 0; index < rawLines.Length; index++)
        {
            var text = rawLines[index];

            if (index == rawLines.Length - 1 && text.Length == 0)
            {
                break;
            }

            if (text.StartsWith("diff --git ", StringComparison.Ordinal)) inHunk = false;
            var kind = Classify(text, inHunk);
            int? oldLineNumber = null;
            int? newLineNumber = null;

            switch (kind)
            {
                case DiffLineKind.HunkHeader:
                    var match = HunkHeaderRegex.Match(text);
                    if (match.Success && TryReadRange(match, "old", "oldCount", out oldNumber, out oldRemaining)
                        && TryReadRange(match, "new", "newCount", out newNumber, out newRemaining))
                    {
                        inHunk = oldRemaining > 0 || newRemaining > 0;
                    }
                    else
                    {
                        // Untrusted malformed numbers stay visible as raw metadata, without invented
                        // line numbers or an exception escaping onto the dispatcher.
                        kind = DiffLineKind.Meta;
                        inHunk = false;
                    }

                    break;
                case DiffLineKind.Context:
                    if (oldRemaining > 0) { oldLineNumber = oldNumber++; oldRemaining--; }
                    if (newRemaining > 0) { newLineNumber = newNumber++; newRemaining--; }
                    break;
                case DiffLineKind.Deletion:
                    if (oldRemaining > 0) { oldLineNumber = oldNumber++; oldRemaining--; }
                    break;
                case DiffLineKind.Addition:
                    if (newRemaining > 0) { newLineNumber = newNumber++; newRemaining--; }
                    break;
                case DiffLineKind.FileHeader:
                    inHunk = false;
                    break;
            }

            if (oldRemaining == 0 && newRemaining == 0) inHunk = false;

            var displayText = kind is DiffLineKind.Context or DiffLineKind.Addition or DiffLineKind.Deletion
                ? text.Length > 0 ? text[1..] : string.Empty
                : text;

            lines.Add(new DiffLineViewModel(lines.Count, kind, displayText, oldLineNumber, newLineNumber));
        }

        return lines;
    }

    private static bool TryReadRange(Match match, string startName, string countName, out int start, out int count)
    {
        count = 1;
        return int.TryParse(match.Groups[startName].Value, NumberStyles.None, CultureInfo.InvariantCulture, out start)
            && (!match.Groups[countName].Success
                || int.TryParse(match.Groups[countName].Value, NumberStyles.None, CultureInfo.InvariantCulture, out count))
            && (count == 0 || (long)start + count - 1 <= int.MaxValue);
    }

    private static DiffLineKind Classify(string text, bool inHunk)
    {
        if (text.StartsWith("@@", StringComparison.Ordinal))
        {
            return DiffLineKind.HunkHeader;
        }

        if (text.StartsWith("\\ No newline", StringComparison.Ordinal))
        {
            return DiffLineKind.NoNewline;
        }

        if (!inHunk
            && (text.StartsWith("--- ", StringComparison.Ordinal)
                || text.StartsWith("+++ ", StringComparison.Ordinal)))
        {
            return DiffLineKind.FileHeader;
        }

        if (inHunk)
        {
            if (text.StartsWith('+'))
            {
                return DiffLineKind.Addition;
            }

            if (text.StartsWith('-'))
            {
                return DiffLineKind.Deletion;
            }

            if (text.Length == 0 || text.StartsWith(' '))
            {
                return DiffLineKind.Context;
            }
        }

        return DiffLineKind.Meta;
    }
}

/// <summary>Aligns parsed unified diff lines into side-by-side rows, pairing deletion/addition runs.</summary>
internal static class DiffSideBySideBuilder
{
    public static IReadOnlyList<DiffSideBySideRowViewModel> Build(IReadOnlyList<DiffLineViewModel> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var rows = new List<DiffSideBySideRowViewModel>();
        var index = 0;

        while (index < lines.Count)
        {
            var line = lines[index];

            if (line.IsHunkHeader || line.IsFileHeader || line.IsMeta || line.IsNoNewline)
            {
                rows.Add(new DiffSideBySideRowViewModel(null, null, line.Text, line.Kind));
                index++;
                continue;
            }

            if (line.IsContext)
            {
                rows.Add(new DiffSideBySideRowViewModel(line, line));
                index++;
                continue;
            }

            var deletions = new List<DiffLineViewModel>();

            while (index < lines.Count && lines[index].IsDeletion)
            {
                deletions.Add(lines[index++]);
            }

            var additions = new List<DiffLineViewModel>();

            while (index < lines.Count && lines[index].IsAddition)
            {
                additions.Add(lines[index++]);
            }

            var rowCount = Math.Max(deletions.Count, additions.Count);

            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                rows.Add(new DiffSideBySideRowViewModel(
                    rowIndex < deletions.Count ? deletions[rowIndex] : null,
                    rowIndex < additions.Count ? additions[rowIndex] : null));
            }
        }

        return rows;
    }
}
