using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

namespace Spotlight.App;

public partial class SearchWindow : Window
{
    private readonly SearchEngine _engine;
    private readonly IndexStore _store;
    private readonly Settings _settings;
    private readonly ObservableCollection<ResultItem> _items = new();
    private CancellationTokenSource? _searchCts;
    private string _lastQuery = "";

    // Parameterless constructor for the XAML previewer.
    public SearchWindow() : this(null!, null!, new Settings()) { }

    public SearchWindow(SearchEngine engine, IndexStore store, Settings settings)
    {
        _engine = engine;
        _store = store;
        _settings = settings;
        InitializeComponent();
        Results.ItemsSource = _items;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None };

        Query.TextChanged += (_, _) => QueueSearch();
        Results.SelectionChanged += (_, _) => UpdatePreview();
        Results.DoubleTapped += (_, _) => OpenSelected(reveal: false);
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Deactivated += (_, _) => { if (IsVisible) HideSearch(); };
    }

    // ---------------------------------------------------------------- show / hide

    public void Toggle()
    {
        if (IsVisible && IsActive) HideSearch();
        else ShowSearch();
    }

    public void ShowSearch()
    {
        PlaceOnActiveScreen();
        Show();
        Activate();
        Platform.ForceForeground(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        Query.Focus();
        Query.SelectAll();
    }

    public void HideSearch()
    {
        _searchCts?.Cancel();
        if (!_noticeSticky) Notice.IsVisible = false;
        Hide();
        HiddenAway?.Invoke();
    }

    /// <summary>Horizontally centered, a fifth of the way down the screen under the mouse, like Spotlight.</summary>
    private void PlaceOnActiveScreen()
    {
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        int x = area.X + (int)((area.Width - Width * scale) / 2);
        int y = area.Y + (int)(area.Height * 0.2);
        Position = new PixelPoint(x, y);
    }

    public void SetIndexStatus(IndexStatus s) => Status.Text = s.Describe();

    private bool _noticeSticky;

    /// <summary>Raised after the window hides; the app uses it to release memory while idle.</summary>
    public event Action? HiddenAway;

    public void ClearNotice() => Notice.IsVisible = false;

    /// <summary>Shows a message under the search box. Non-sticky messages disappear when the window hides.</summary>
    public void SetNotice(string text, bool sticky = true)
    {
        Notice.Text = text;
        Notice.IsVisible = true;
        _noticeSticky = sticky;
    }

    // ---------------------------------------------------------------- searching

    private void QueueSearch()
    {
        var query = Query.Text ?? "";
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(query))
        {
            _items.Clear();
            ResultsArea.IsVisible = false;
            return;
        }
        // Short debounce so fast typing doesn't run a query per keystroke.
        Task.Delay(40, cts.Token).ContinueWith(_ =>
        {
            List<SearchResult> results;
            try { results = _engine.Search(query, _settings.MaxResults, cts.Token); }
            catch (OperationCanceledException) { return; }
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                _lastQuery = query;
                ShowResults(results);
            });
        }, cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void ShowResults(List<SearchResult> results)
    {
        _items.Clear();
        foreach (var r in results) _items.Add(new ResultItem(r));
        ResultsArea.IsVisible = _items.Count > 0;
        if (_items.Count > 0) Results.SelectedIndex = 0;
    }

    private void UpdatePreview()
    {
        if (Results.SelectedItem is not ResultItem item) return;
        var r = item.Result;
        PreviewIcon.Source = item.Icon;
        PreviewGlyph.Text = item.Icon == null ? item.Glyph : "";
        PreviewName.Text = r.Kind == FileKind.Calculator ? r.Value : r.Name;
        PreviewDetails.Text = item.Details;
        PreviewPath.Text = r.Path;
        PreviewSnippet.Inlines = Highlight(r.Snippet ?? "", _lastQuery);
        PreviewSnippet.IsVisible = !string.IsNullOrEmpty(r.Snippet);
        if (item.Icon == null)
            item.PropertyChanged += (_, _) => { if (Results.SelectedItem == item) { PreviewIcon.Source = item.Icon; PreviewGlyph.Text = ""; } };
    }

    /// <summary>Bolds every occurrence of the query terms inside the snippet.</summary>
    private static InlineCollection Highlight(string text, string query)
    {
        var inlines = new InlineCollection();
        var terms = TextTokenizer.QueryTerms(query).Where(t => t.Length > 0).ToList();
        int i = 0;
        while (i < text.Length)
        {
            int best = -1, len = 0;
            foreach (var t in terms)
            {
                int k = text.IndexOf(t, i, StringComparison.OrdinalIgnoreCase);
                if (k >= 0 && (best < 0 || k < best)) { best = k; len = t.Length; }
            }
            if (best < 0) { inlines.Add(new Run(text[i..])); break; }
            if (best > i) inlines.Add(new Run(text[i..best]));
            inlines.Add(new Run(text.Substring(best, len)) { FontWeight = FontWeight.Bold, Background = (IBrush?)Application.Current?.FindResource(Application.Current.ActualThemeVariant, "HighlightBrush") });
            i = best + len;
        }
        return inlines;
    }

    // ---------------------------------------------------------------- keyboard

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Escape:
                if (!string.IsNullOrEmpty(Query.Text)) Query.Text = "";
                else HideSearch();
                e.Handled = true;
                break;
            case Key.Down:
                Move(+1); e.Handled = true; break;
            case Key.Up:
                Move(-1); e.Handled = true; break;
            case Key.PageDown:
                Move(+8); e.Handled = true; break;
            case Key.PageUp:
                Move(-8); e.Handled = true; break;
            case Key.Enter:
                OpenSelected(reveal: ctrl); e.Handled = true; break;
            case Key.C when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (Results.SelectedItem is ResultItem it) _ = Clipboard?.SetTextAsync(it.Result.Kind == FileKind.Calculator ? it.Result.Value : it.Result.Path);
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        int i = Math.Clamp(Results.SelectedIndex + delta, 0, _items.Count - 1);
        Results.SelectedIndex = i;
        Results.ScrollIntoView(i);
    }

    private void OpenSelected(bool reveal)
    {
        if (Results.SelectedItem is not ResultItem item) return;
        var r = item.Result;
        if (r.Kind == FileKind.Calculator)
        {
            _ = Clipboard?.SetTextAsync(r.Value);
            HideSearch();
            return;
        }
        try
        {
            if (reveal) Platform.Reveal(r.Path);
            else Platform.Open(r.Path);
        }
        catch (Exception ex)
        {
            SetNotice($"无法打开：{ex.Message}");
            return;
        }
        // Learn from the choice in the background; the indexer may hold the write lock briefly.
        var query = _lastQuery;
        Task.Run(() => { try { _store.RecordLaunch(r.Id, query); } catch (Exception) { } });
        HideSearch();
    }
}
