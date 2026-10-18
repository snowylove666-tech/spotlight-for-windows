using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
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
    private CancellationTokenSource? _previewCts;
    private PreviewImage? _preview;
    private ResultItem? _previewItem;

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
        Opened += (_, _) =>
        {
            // Windows 11 rounds the window and draws its edge; a second rounded border of our own on
            // top of that showed as a doubled outline at the corners.
            if (Platform.RoundCorners(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero))
            {
                Shell.CornerRadius = new CornerRadius(0);
                Shell.BorderThickness = new Thickness(0);
            }
        };
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
        // Page renders are a few MB each; don't keep them while hidden.
        _previewCts?.Cancel();
        ClearPreviewImage();
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

    private void ShowResults(List<SearchResult> results, long? keepSelected = null)
    {
        _items.Clear();
        foreach (var r in results) _items.Add(new ResultItem(r));
        ResultsArea.IsVisible = _items.Count > 0;
        if (_items.Count > 0)
        {
            int keep = keepSelected is { } id ? _items.ToList().FindIndex(i => i.Result.Id == id) : -1;
            Results.SelectedIndex = Math.Max(0, keep);
            if (keep > 0) Results.ScrollIntoView(keep);
        }
        // Documents found by name whose text hasn't been read yet: read them now rather than after the
        // whole backlog, so searching their contents works seconds later.
        var pending = results.Where(r => r.ContentPending).Take(10).Select(r => r.Id).ToList();
        if (pending.Count > 0) ReadContentSoon?.Invoke(pending);
    }

    /// <summary>Asks the indexer to read these documents next.</summary>
    public Action<IReadOnlyList<long>>? ReadContentSoon { get; set; }

    /// <summary>Called when documents asked for with <see cref="ReadContentSoon"/> have been read: if
    /// any of them is on screen, search again so their text matches show up.</summary>
    public void ContentArrived(IReadOnlyList<long> ids)
    {
        if (!IsVisible || !_items.Any(i => ids.Contains(i.Result.Id))) return;
        var query = Query.Text ?? "";
        if (query != _lastQuery || string.IsNullOrWhiteSpace(query)) return;
        long? selected = (Results.SelectedItem as ResultItem)?.Result.Id;
        Task.Run(() =>
        {
            List<SearchResult> results;
            try { results = _engine.Search(query, _settings.MaxResults); }
            catch (Exception) { return; }
            Dispatcher.UIThread.Post(() => { if (query == _lastQuery && IsVisible) ShowResults(results, selected); });
        });
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
        PreviewScroll.Offset = default;
        LoadPagePreview(item);
    }

    // ---------------------------------------------------------------- page preview

    private void LoadPagePreview(ResultItem item)
    {
        _previewCts?.Cancel();
        if (_previewItem != item) ClearPreviewImage();
        if (!PagePreview.CanPreview(item.Result)) return;
        var cts = _previewCts = new CancellationTokenSource();
        var r = item.Result;
        // Only content hits have words worth marking; a name match shows the first page.
        var terms = r.ContentMatch ? TextTokenizer.QueryTerms(_lastQuery) : new List<string>();
        double width = Math.Max(200, PreviewScroll.Bounds.Width - PreviewScroll.Padding.Left - PreviewScroll.Padding.Right - 14);
        double scale = RenderScaling > 0 ? RenderScaling : 1;
        int pixels = (int)(width * scale);
        _ = Task.Run(async () =>
        {
            await Task.Delay(90, cts.Token); // arrowing through the list: render only where the user stops
            var img = await PagePreview.RenderAsync(r, terms, pixels, cts.Token);
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || Results.SelectedItem != item || !IsVisible) { img?.Bitmap.Dispose(); return; }
                ShowPreviewImage(item, img, width, scale);
            });
        }, cts.Token);
    }

    private void ShowPreviewImage(ResultItem item, PreviewImage? img, double width, double scale)
    {
        ClearPreviewImage();
        if (img == null) return;
        _preview = img;
        _previewItem = item;
        var px = img.Bitmap.PixelSize;
        // Pages fill the pane; small pictures are not blown up.
        double w = item.Result.Kind == FileKind.Pdf ? width : Math.Min(width, px.Width / scale);
        double h = w * px.Height / Math.Max(1, px.Width);
        PageImage.Source = img.Bitmap;
        PageImage.Width = HitLayer.Width = w;
        PageImage.Height = HitLayer.Height = h;
        var brush = (IBrush?)Application.Current?.FindResource(Application.Current.ActualThemeVariant, "PageHitBrush") ?? Brushes.Yellow;
        foreach (var box in img.Highlights)
        {
            var mark = new Rectangle { Width = box.Width * w + 4, Height = box.Height * h + 4, Fill = brush, RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(mark, box.X * w - 2);
            Canvas.SetTop(mark, box.Y * h - 2);
            HitLayer.Children.Add(mark);
        }
        PageFrame.IsVisible = true;
        IconPanel.IsVisible = false;
        PageLabel.Text = img.Page is { } p ? $"第 {p} 页，共 {img.PageCount} 页" : "";
        PageLabel.IsVisible = img.Page != null;
        if (img.Highlights.Count > 0)
        {
            // Bring the first hit into view, a little below the top edge.
            double y = img.Highlights.Min(b => b.Y) * h;
            Dispatcher.UIThread.Post(() => PreviewScroll.Offset = new Vector(0, Math.Max(0, PageFrame.Bounds.Y + y - 60)), DispatcherPriority.Background);
        }
    }

    private void ClearPreviewImage()
    {
        HitLayer.Children.Clear();
        PageImage.Source = null;
        PageFrame.IsVisible = false;
        PageLabel.IsVisible = false;
        IconPanel.IsVisible = true;
        _preview?.Bitmap.Dispose();
        _preview = null;
        _previewItem = null;
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
            else if (r.ContentMatch)
            {
                // Open at the hit: the page from the index, or the one the preview found.
                int? page = r.Page ?? (_previewItem == item ? _preview?.Page : null);
                var term = TextTokenizer.QueryTerms(_lastQuery).OrderByDescending(t => t.Length).FirstOrDefault();
                Platform.OpenAt(r.Path, page, term);
            }
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
