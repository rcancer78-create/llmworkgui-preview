using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LLMWorkGUI.App.Help;

public sealed record HelpHeading(string Title, int Level, Paragraph Paragraph);
public sealed record HelpTextMatch(TextPointer Start, TextPointer End);
public sealed record HelpRenderedGuide(FlowDocument Document, IReadOnlyList<HelpHeading> Headings, HelpSearchIndex Search);

/// <summary>Indexes the actual displayed Runs, preserving character positions across inline styles.</summary>
public sealed class HelpSearchIndex
{
    private readonly string _text;
    private readonly IReadOnlyList<TextPointer> _starts;
    private readonly IReadOnlyList<TextPointer> _ends;

    internal HelpSearchIndex(IEnumerable<Paragraph> paragraphs)
    {
        var text = new StringBuilder();
        var starts = new List<TextPointer>();
        var ends = new List<TextPointer>();
        foreach (var paragraph in paragraphs)
        {
            foreach (var run in paragraph.Inlines.OfType<Run>())
            {
                for (var index = 0; index < run.Text.Length; index++)
                {
                    text.Append(run.Text[index]);
                    starts.Add(run.ContentStart.GetPositionAtOffset(index)!);
                    ends.Add(run.ContentStart.GetPositionAtOffset(index + 1)!);
                }
            }
            text.Append('\n');
            starts.Add(paragraph.ContentEnd);
            ends.Add(paragraph.ContentEnd);
        }
        _text = text.ToString();
        _starts = starts;
        _ends = ends;
    }

    public string Text => _text;

    public IReadOnlyList<HelpTextMatch> Find(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<HelpTextMatch>();
        var matches = new List<HelpTextMatch>();
        for (var offset = 0; offset <= _text.Length - query.Length;)
        {
            var index = _text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0) break;
            matches.Add(new(_starts[index], _ends[index + query.Length - 1]));
            offset = index + query.Length;
        }
        return matches;
    }
}

/// <summary>Our guide subset only; all content is text, never XAML/HTML or executable links.</summary>
public static class HelpMarkdownRenderer
{
    public static HelpRenderedGuide Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var document = new FlowDocument { FontSize = 15, PagePadding = new Thickness(16) };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Theme.Text.Primary");
        var headings = new List<HelpHeading>();
        var paragraphs = new List<Paragraph>();
        var pending = new List<string>();
        var code = new List<string>();
        var inCode = false;
        void AddParagraph(string text, bool literal = false, int level = 0)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            if (literal)
            {
                paragraph.FontFamily = new FontFamily("Consolas");
                paragraph.Padding = new Thickness(10);
                paragraph.SetResourceReference(Paragraph.BackgroundProperty, "Theme.Surface.Alt");
                paragraph.Inlines.Add(new Run(text));
            }
            else AddInlines(paragraph, text);
            if (level > 0)
            {
                paragraph.FontSize = level == 1 ? 26 : level == 2 ? 21 : 17;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 18, 0, 10);
                headings.Add(new(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim(), level, paragraph));
            }
            document.Blocks.Add(paragraph);
            paragraphs.Add(paragraph);
        }
        void Flush()
        {
            if (pending.Count == 0) return;
            AddParagraph(string.Join(" ", pending));
            pending.Clear();
        }
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (raw.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                if (inCode) { AddParagraph(string.Join("\n", code), literal: true); code.Clear(); }
                inCode = !inCode;
                continue;
            }
            if (inCode) { code.Add(raw); continue; }
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }
            var level = 0;
            while (level < line.Length && line[level] == '#') level++;
            if (level is >= 1 and <= 6 && level < line.Length && line[level] == ' ')
            { Flush(); AddParagraph(line[(level + 1)..], level: level); continue; }
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            { Flush(); AddParagraph("• " + line[2..]); continue; }
            var digitEnd = 0;
            while (digitEnd < line.Length && char.IsAsciiDigit(line[digitEnd])) digitEnd++;
            if (digitEnd > 0 && digitEnd + 1 < line.Length && line[digitEnd] == '.' && line[digitEnd + 1] == ' ')
            { Flush(); AddParagraph(line); continue; }
            pending.Add(line);
        }
        Flush();
        if (inCode) AddParagraph(string.Join("\n", code), literal: true);
        return new(document, headings, new HelpSearchIndex(paragraphs));
    }

    private static void AddInlines(Paragraph paragraph, string text)
    {
        var position = 0;
        while (position < text.Length)
        {
            var bold = text.IndexOf("**", position, StringComparison.Ordinal);
            var code = text.IndexOf('`', position);
            var start = bold < 0 ? code : code < 0 ? bold : Math.Min(bold, code);
            if (start < 0) { paragraph.Inlines.Add(new Run(text[position..])); break; }
            if (start > position) paragraph.Inlines.Add(new Run(text[position..start]));
            var marker = start == bold ? "**" : "`";
            var end = text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
            if (end < 0) { paragraph.Inlines.Add(new Run(text[start..])); break; }
            var run = new Run(text[(start + marker.Length)..end]);
            if (marker == "**") run.FontWeight = FontWeights.Bold;
            else run.FontFamily = new FontFamily("Consolas");
            paragraph.Inlines.Add(run);
            position = end + marker.Length;
        }
    }
}
