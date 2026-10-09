using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace LLMWorkGUI.App.Help;

public partial class HelpGuideWindow : Window
{
    private IReadOnlyList<HelpTextMatch> _matches = Array.Empty<HelpTextMatch>();
    private int _matchIndex = -1;

    public HelpGuideWindow() : this(HelpGuideContent.Load()) { }

    public HelpGuideWindow(string markdown)
    {
        InitializeComponent();
        Guide = HelpMarkdownRenderer.Render(markdown);
        GuideReader.Document = Guide.Document;
        ContentsList.ItemsSource = Guide.Headings;
        SearchBox.TextChanged += (_, _) => RefreshSearch();
        SearchBox.PreviewKeyDown += OnSearchKeyDown;
        ContentsList.PreviewKeyDown += OnContentsKeyDown;
        ContentsList.PreviewMouseLeftButtonUp += OnContentsMouseUp;
        PreviewKeyDown += OnWindowKeyDown;
        Loaded += (_, _) => { SearchBox.Focus(); ShowMatch(); };
        Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 32));
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 32));
        RefreshSearch();
    }

    public HelpRenderedGuide Guide { get; }

    private void RefreshSearch()
    {
        _matches = Guide.Search.Find(SearchBox.Text);
        _matchIndex = _matches.Count == 0 ? -1 : 0;
        ShowMatch();
    }

    private void ShowMatch()
    {
        NextMatchButton.IsEnabled = PreviousMatchButton.IsEnabled = _matches.Count > 0;
        if (_matchIndex < 0)
        {
            GuideReader.Selection.Select(GuideReader.Document.ContentStart, GuideReader.Document.ContentStart);
            SearchStatus.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
                ? "Локальное руководство · Ctrl+F — поиск · Enter / Shift+Enter — совпадения · Esc — закрыть"
                : "Совпадений нет.";
            return;
        }
        var match = _matches[_matchIndex];
        GuideReader.Selection.Select(match.Start, match.End);
        RevealMatch(match);
        SearchStatus.Text = $"Совпадение {_matchIndex + 1} из {_matches.Count}";
    }

    private void RevealMatch(HelpTextMatch match)
    {
        if (!GuideReader.IsLoaded) return;
        // A fenced block is one Paragraph/Run. Bringing that whole element into view
        // shows its beginning, not the matched line. Use the character's viewport
        // coordinates plus the current scroll offsets, without moving keyboard focus.
        GuideReader.UpdateLayout();
        var rectangle = match.Start.GetCharacterRect(LogicalDirection.Forward);
        if (rectangle.IsEmpty) return;
        GuideReader.ScrollToVerticalOffset(Math.Max(0,
            GuideReader.VerticalOffset + rectangle.Top + rectangle.Height / 2 - GuideReader.ViewportHeight / 2));
        if (rectangle.Left < 0 || rectangle.Right > GuideReader.ViewportWidth)
            GuideReader.ScrollToHorizontalOffset(Math.Max(0,
                GuideReader.HorizontalOffset + rectangle.Left - GuideReader.ViewportWidth / 2));
    }

    private void MoveMatch(int step)
    {
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + step + _matches.Count) % _matches.Count;
        ShowMatch();
    }

    private void OnPreviousMatch(object sender, RoutedEventArgs e) => MoveMatch(-1);
    private void OnNextMatch(object sender, RoutedEventArgs e) => MoveMatch(1);
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void NavigateSelectedHeading()
    {
        if (ContentsList.SelectedItem is HelpHeading heading) heading.Paragraph.BringIntoView();
    }

    private void OnHeadingSelected(object sender, SelectionChangedEventArgs e) => NavigateSelectedHeading();

    private void OnContentsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        NavigateSelectedHeading();
        e.Handled = true;
    }

    private void OnContentsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target
            && ItemsControl.ContainerFromElement(ContentsList, target) is ListBoxItem)
            NavigateSelectedHeading();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        MoveMatch((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
    }
}
