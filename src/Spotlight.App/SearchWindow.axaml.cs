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
    private SearchEngine _engine;
    private IndexStore _store;
    private readonly Settings _settings;
    private readonly ObservableCollection<ResultItem> _items = new();
    private CancellationTokenSource? _searchCts;
    private string _lastQuery = "";
    private CancellationTokenSource? _previewCts;

    private static readonly (string label, FileKind[]? kinds)[] KindFilters =
    {
        ("全部", null),
        ("应用", new[] { FileKind.App }),
        ("文件夹", new[] { FileKind.Folder }),
        ("PDF", new[] { FileKind.Pdf }),
        ("文稿", new[] { FileKind.Document }),
        ("表格", new[] { FileKind.Spreadsheet }),
        ("演示", new[] { FileKind.Presentation }),
        ("图片", new[] { FileKind.Image }),
        ("音视频", new[] { FileKind.Audio, FileKind.Video }),
        ("文本代码", new[] { FileKind.Text, FileKind.Code }),
        ("其他", new[] { FileKind.Archive, FileKind.Other }),
    };

    private static readonly (string label, SortBy sort)[] SortOptions =
    {
        ("相关", SortBy.Relevance), ("日期", SortBy.Modified), ("名称", SortBy.Name), ("大小", SortBy.Size), ("格式", SortBy.Kind),
    };

    private readonly List<Button> _kindButtons = new();
    private readonly List<Button> _sortButtons = new();
    private int _kindIndex;
    private SortBy _sort = SortBy.Relevance;
    private bool _descending = true;

    private SearchFilter CurrentFilter() => new(KindFilters[_kindIndex].kinds, _sort, _descending, _settings.MatchAnyTerm);

    private Button? _fuzzyButton;

    private void BuildFilterBar()
    {
        for (int i = 0; i < KindFilters.Length; i++)
        {
            int index = i;
            var b = new Button { Content = KindFilters[i].label, Classes = { "chip" } };
            b.Click += (_, _) => SetKind(index);
            _kindButtons.Add(b);
            KindChips.Children.Add(b);
        }
        // "模糊匹配": several words find files holding any of them (normally all are needed).
        // A checkbox look and a divider, so it does not read as one more file type.
        SortChips.Children.Insert(0, new Border { Width = 1, Height = 16, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = (IBrush?)Application.Current?.FindResource(Application.Current.ActualThemeVariant, "DividerBrush") });
        _fuzzyButton = new Button { Content = "☐ 模糊", Classes = { "chip" }, Margin = new Thickness(0, 0, 8, 0) };
        ToolTip.SetTip(_fuzzyButton, "开启后，输入多个关键词时只要命中其中一个就会列出，命中越多排得越前（Ctrl+M）");
        _fuzzyButton.Click += (_, _) => ToggleFuzzy();
        SortChips.Children.Insert(1, _fuzzyButton);
        foreach (var (label, sort) in SortOptions)
        {
            var b = new Button { Content = label, Classes = { "chip" }, Tag = label };
            b.Click += (_, _) =>
            {
                // Picking the current order again flips its direction.
                if (_sort == sort && sort != SortBy.Relevance) _descending = !_descending;
                else { _sort = sort; _descending = SearchFilter.DefaultDescending(sort); }
                RefreshChips();
                QueueSearch();
            };
            _sortButtons.Add(b);
            SortChips.Children.Add(b);
        }
        RefreshChips();
    }

    private void SetKind(int index)
    {
        _kindIndex = (index + KindFilters.Length) % KindFilters.Length;
        RefreshChips();
        QueueSearch();
    }

    private void ToggleFuzzy()
    {
        _settings.MatchAnyTerm = !_settings.MatchAnyTerm;
        try { _settings.Save(); } catch (Exception) { }
        RefreshChips();
        QueueSearch();
    }

    private void RefreshChips()
    {
        if (_fuzzyButton != null)
        {
            _fuzzyButton.Classes.Set("on", _settings.MatchAnyTerm);
            _fuzzyButton.Content = _settings.MatchAnyTerm ? "☑ 模糊" : "☐ 模糊";
        }
        for (int i = 0; i < _kindButtons.Count; i++) _kindButtons[i].Classes.Set("on", i == _kindIndex);
        for (int i = 0; i < _sortButtons.Count; i++)
        {
            var (label, sort) = SortOptions[i];
            bool on = sort == _sort;
            _sortButtons[i].Classes.Set("on", on);
            _sortButtons[i].Content = on && sort != SortBy.Relevance ? label + (_descending ? " ↓" : " ↑") : label;
        }
    }
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

        BuildFilterBar();
        Query.TextChanged += (_, _) => QueueSearch();
        Results.SelectionChanged += (_, _) => UpdatePreview();
        Results.DoubleTapped += (_, _) => OpenSelected(reveal: false);
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // A preview handler taking the keyboard focus is not the user leaving.
        Closing += (_, e) =>
        {
            // Alt+F4 or the taskbar: hide like Esc. Only quitting the app really closes the window.
            if (e.CloseReason != WindowCloseReason.ApplicationShutdown)
            {
                e.Cancel = true;
                HideSearch();
            }
        };
        Deactivated += (_, _) =>
        {
            if (!IsVisible || (OperatingSystem.IsWindows() && PreviewHandlerHost.IsForeground(TopHandle))) return;
            // Starting a previewer can bring its Office program forward for a moment; take the
            // foreground back instead of treating that as the user leaving.
            if (DateTime.UtcNow < _handlerStartedUntil)
            {
                Dispatcher.UIThread.Post(() => { Activate(); Platform.ForceForeground(TopHandle); Query.Focus(); });
                return;
            }
            HideSearch();
        };
        PrevPage.Click += (_, _) => TurnPage(-1);
        NextPage.Click += (_, _) => TurnPage(+1);
        NextHit.Click += (_, _) => StepHit(+1);
        PrevHit.Click += (_, _) => StepHit(-1);
        PreviewScroll.AddHandler(PointerWheelChangedEvent, OnPreviewWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _keyboardGuard.Tick += (_, _) =>
        {
            if (OperatingSystem.IsWindows() && Handler.IsVisible && PreviewHandlerHost.KeepKeyboard(TopHandle)) Query.Focus();
        };
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

    /// <summary>Points the window at a reopened index (after it was moved to another folder).</summary>
    public void Rebind(SearchEngine engine, IndexStore store)
    {
        _engine = engine;
        _store = store;
        _items.Clear();
        ResultsArea.IsVisible = false;
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
        _keyboardGuard.Start();
    }

    // Keeps the keys with the search window while an Office document is on show (see KeepKeyboard).
    private readonly DispatcherTimer _keyboardGuard = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public void HideSearch()
    {
        _searchCts?.Cancel();
        _keyboardGuard.Stop();
        _openWhenReady = null;
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
        FilterBar.IsVisible = !string.IsNullOrWhiteSpace(query);
        if (string.IsNullOrWhiteSpace(query))
        {
            _items.Clear();
            ResultsArea.IsVisible = false;
            return;
        }
        var filter = CurrentFilter();
        // Short debounce so fast typing doesn't run a query per keystroke.
        Task.Delay(40, cts.Token).ContinueWith(_ =>
        {
            SearchResults results;
            bool loosened = false;
            try
            {
                results = _engine.Search(query, filter, _settings.MaxResults, cts.Token);
                // Nothing holds every word: show what holds some of them rather than an empty window.
                if (results.Count == 0 && !filter.AnyTerm && TextTokenizer.QueryTerms(query).Count > 1)
                {
                    results = _engine.Search(query, filter with { AnyTerm = true }, _settings.MaxResults, cts.Token);
                    loosened = results.Count > 0;
                }
            }
            catch (OperationCanceledException) { return; }
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                _lastQuery = query;
                ShowResults(results);
                if (loosened) ResultCount.Text = $"没有同时含全部关键词的，以下 {results.Total:N0} 项含其中部分";
                if (_openWhenReady is { } reveal) { _openWhenReady = null; OpenSelected(reveal); }
            });
        }, cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void ShowResults(SearchResults results, long? keepSelected = null)
    {
        ResultCount.Text = results.Total > results.Count
            ? $"共 {results.Total:N0}{(results.AtLeast ? "+" : "")} 项，显示前 {results.Count} 项"
            : $"共 {results.Count} 项";
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
        var filter = CurrentFilter();
        Task.Run(() =>
        {
            SearchResults results;
            try { results = _engine.Search(query, filter, _settings.MaxResults); }
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
        _hitIndex = 0;
        _text = null;
        UpdateHitInfo(item);
        LoadPagePreview(item);
    }

    // ---------------------------------------------------------------- page preview

    private DateTime _handlerStartedUntil;

    private IntPtr TopHandle => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    private void LoadPagePreview(ResultItem item, int? page = null, bool allowHandler = true, bool atEnd = false, int box = 0)
    {
        _previewCts?.Cancel();
        if (_previewItem != item) ClearPreviewImage();
        var cts = _previewCts = new CancellationTokenSource();
        var r = item.Result;
        if (r.Kind == FileKind.Calculator || PagePreview.IsCloudOnly(r.Path)) return;
        // Only content hits have words worth marking; a name match shows the first page.
        var terms = r.ContentMatch ? TextTokenizer.QueryTerms(_lastQuery) : new List<string>();
        if (allowHandler && UsesHandler(r)) { LoadHandler(item, cts); return; }
        if (SheetPreview.CanPreview(r.Path)) { LoadSheet(item, terms, cts, _hitIndex); return; }
        if (r.Kind is FileKind.Text or FileKind.Code || System.IO.Path.GetExtension(r.Path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            LoadText(item, terms, cts);
            return;
        }
        if (!PagePreview.CanPreview(r)) return;
        // Right after the results first appear the pane may not be laid out yet: use the window's width.
        double pane = PreviewScroll.Bounds.Width > 100 ? PreviewScroll.Bounds.Width : Width - 341;
        double width = Math.Max(200, pane - PreviewScroll.Padding.Left - PreviewScroll.Padding.Right - 14);
        double scale = RenderScaling > 0 ? RenderScaling : 1;
        int pixels = (int)(width * scale);
        _ = Task.Run(async () =>
        {
            // Arrowing through the list: render only where the user stops. Page turns go straight on.
            if (page == null) await Task.Delay(90, cts.Token);
            var img = await PagePreview.RenderAsync(r, terms, pixels, cts.Token, page);
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || Results.SelectedItem != item || !IsVisible) { img?.Bitmap.Dispose(); return; }
                ShowPreviewImage(item, img, width, scale, atEnd, box);
            });
        }, cts.Token);
    }

    // ---------------------------------------------------------------- Office documents (preview handlers)

    private static bool UsesHandler(SearchResult r) =>
        OperatingSystem.IsWindows() && r.Kind is FileKind.Document or FileKind.Presentation
        && PreviewHandlerHost.HandlerFor(r.Path) != null;

    private void LoadHandler(ResultItem item, CancellationTokenSource cts)
    {
        if (!OperatingSystem.IsWindows()) return;
        _previewItem = item;
        PreviewScroll.IsVisible = false;
        Handler.IsVisible = true;
        HandlerCaption.IsVisible = true;
        HandlerName.Text = item.Result.Name;
        HandlerSnippet.Inlines = Highlight(item.Result.Snippet ?? item.Result.Path, _lastQuery);
        // Office takes a moment to start; only load the document the user stops on.
        Task.Delay(220, cts.Token).ContinueWith(_ => Dispatcher.UIThread.Post(async () =>
        {
            if (cts.IsCancellationRequested || Results.SelectedItem != item || !IsVisible) return;
            _handlerStartedUntil = DateTime.UtcNow.AddSeconds(8);
            bool shown = Handler.HasWindow && await Handler.LoadAsync(item.Result.Path);
            // Office activates a hidden window of its own a moment after drawing.
            _handlerStartedUntil = DateTime.UtcNow.AddSeconds(4);
            if (cts.IsCancellationRequested || Results.SelectedItem != item) return;
            // The handler usually takes the keyboard; typing must keep going to the search box.
            PreviewHandlerHost.TakeFocusBack(TopHandle);
            Query.Focus();
            if (!shown)
            {
                ClearPreviewImage();
                LoadPagePreview(item, allowHandler: false); // thumbnail instead
            }
        }), cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- spreadsheets

    private void LoadSheet(ResultItem item, List<string> terms, CancellationTokenSource cts, int skip = 0)
    {
        var path = item.Result.Path;
        _ = Task.Run(async () =>
        {
            await Task.Delay(80, cts.Token);
            SheetView? sheet;
            try { sheet = SheetPreview.Read(path, terms, cts.Token, skip); }
            catch (Exception e) when (e is not OperationCanceledException) { sheet = null; }
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || Results.SelectedItem != item) return;
                if (sheet == null || sheet.Rows.Count == 0 || sheet.ColumnCount == 0) { LoadText(item, terms, cts); return; }
                _previewItem = item;
                ShowSheet(sheet, terms);
            });
        }, cts.Token);
    }

    private void ShowSheet(SheetView sheet, List<string> terms)
    {
        SheetGrid.Children.Clear();
        SheetGrid.RowDefinitions.Clear();
        SheetGrid.ColumnDefinitions.Clear();
        var line = new SolidColorBrush(Color.Parse("#E1E1E1"));
        var headBrush = new SolidColorBrush(Color.Parse("#F3F3F3"));
        var hitBrush = (IBrush?)Application.Current?.FindResource(Application.Current.ActualThemeVariant, "PageHitBrush") ?? Brushes.Yellow;
        var text = new SolidColorBrush(Color.Parse("#1D1D1F"));
        var dim = new SolidColorBrush(Color.Parse("#6E6E73"));
        for (int c = 0; c <= sheet.ColumnCount; c++) SheetGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r <= sheet.Rows.Count; r++) SheetGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        Control? focusCell = null;
        Border Cell(int row, int col, string value, IBrush? background, IBrush foreground, bool right = false)
        {
            var cell = new Border
            {
                Background = background,
                BorderBrush = line,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(6, 2),
                MaxWidth = 160,
                Child = new TextBlock
                {
                    Text = value, FontSize = 11, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = right ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left,
                },
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            SheetGrid.Children.Add(cell);
            return cell;
        }

        var currentRow = new SolidColorBrush(Color.Parse("#FFF3E0"));
        Cell(0, 0, "", headBrush, dim);
        for (int c = 0; c < sheet.ColumnCount; c++) Cell(0, c + 1, SheetPreview.ColumnName(c), headBrush, dim);
        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            bool isCurrent = r == sheet.Current;
            Cell(r + 1, 0, (isCurrent ? "▶ " : "") + sheet.RowNumbers[r], isCurrent ? CurrentTextHit : headBrush, isCurrent ? text : dim, right: true);
            for (int c = 0; c < sheet.ColumnCount; c++)
            {
                var v = sheet.Rows[r][c];
                bool hit = v.Length > 0 && terms.Any(t => v.Contains(t, StringComparison.OrdinalIgnoreCase));
                bool number = v.Length > 0 && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
                bool current = r == sheet.Current;
                var cell = Cell(r + 1, c + 1, v, hit ? (current ? CurrentTextHit : hitBrush) : current ? currentRow : Brushes.White, text, number);
                if (hit && current) focusCell ??= cell;
            }
        }
        SheetLabel.Text = sheet.SheetCount > 1 ? $"工作表「{sheet.SheetName}」（共 {sheet.SheetCount} 个）" : $"工作表「{sheet.SheetName}」";
        SheetFrame.IsVisible = true;
        IconPanel.IsVisible = false;
        // The matching cell may be far to the right: scroll the sheet sideways to it.
        if (focusCell != null) Dispatcher.UIThread.Post(() => focusCell.BringIntoView(), DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- text files

    private void LoadText(ResultItem item, List<string> terms, CancellationTokenSource cts)
    {
        var path = item.Result.Path;
        _ = Task.Run(async () =>
        {
            await Task.Delay(60, cts.Token);
            string? text;
            try { text = PagePreview.ReadText(path, cts.Token); }
            catch (Exception e) when (e is not OperationCanceledException) { text = null; }
            var hits = text == null ? new List<int>() : SearchEngine.HitOffsets(text, terms, 5000);
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || Results.SelectedItem != item || string.IsNullOrEmpty(text)) return;
                _previewItem = item;
                _text = text;
                _textHits = hits;
                _hitIndex = 0;
                ShowTextWindow();
                UpdateHitInfo(item);
                TextFrame.IsVisible = true;
                // The snippet repeats what the text view shows.
                PreviewSnippet.IsVisible = false;
            });
        }, cts.Token);
    }

    // ---------------------------------------------------------------- page turning

    private DateTime _lastWheelTurn;

    private void TurnPage(int step, bool atEnd = false)
    {
        if (_preview is not { Page: { } page, PageCount: { } count } || _previewItem is not { } item) return;
        int target = page + step;
        if (target < 1 || target > count || Results.SelectedItem != item) return;
        LoadPagePreview(item, target, atEnd: atEnd);
    }

    private void JumpToHit(int step)
    {
        if (_preview is not { Page: { } page } || _previewItem is not { } item || !item.Result.ContentMatch) return;
        var terms = TextTokenizer.QueryTerms(_lastQuery);
        var path = item.Result.Path;
        PageLabel.Text = "正在查找…";
        _ = Task.Run(() =>
        {
            int? next;
            try { next = PagePreview.FindHitPage(path, page, step, terms, CancellationToken.None); }
            catch (Exception) { next = null; }
            Dispatcher.UIThread.Post(() =>
            {
                if (Results.SelectedItem != item || _previewItem != item) return;
                if (next is { } p) LoadPagePreview(item, p);
                else if (_preview is { } img) PageLabel.Text = $"第 {img.Page} 页，共 {img.PageCount} 页 · {(step > 0 ? "后面" : "前面")}没有更多命中";
            });
        });
    }

    /// <summary>Wheel past the bottom of the page: next page; past the top: previous one.</summary>
    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!PageFrame.IsVisible || _preview?.Page == null) return;
        double max = PreviewScroll.Extent.Height - PreviewScroll.Viewport.Height;
        bool down = e.Delta.Y < 0;
        bool atEdge = down ? PreviewScroll.Offset.Y >= max - 1 : PreviewScroll.Offset.Y <= 1;
        if (!atEdge) return;
        e.Handled = true;
        if (DateTime.UtcNow - _lastWheelTurn < TimeSpan.FromMilliseconds(450)) return;
        _lastWheelTurn = DateTime.UtcNow;
        TurnPage(down ? +1 : -1, atEnd: !down);
    }

    private void ShowPreviewImage(ResultItem item, PreviewImage? img, double width, double scale, bool atEnd = false, int box = 0)
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
        _marks.Clear();
        foreach (var hit in img.Highlights.OrderBy(b => Math.Round(b.Y, 3)).ThenBy(b => b.X))
        {
            var mark = new Rectangle { Width = hit.Width * w + 4, Height = hit.Height * h + 4, Fill = brush, RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(mark, hit.X * w - 2);
            Canvas.SetTop(mark, hit.Y * h - 2);
            HitLayer.Children.Add(mark);
            _marks.Add(mark);
        }
        PageFrame.IsVisible = true;
        IconPanel.IsVisible = false;
        PageLabel.Text = img.Page is { } p ? $"第 {p} 页，共 {img.PageCount} 页" : "";
        PageNav.IsVisible = img.Page != null;
        PrevPage.IsEnabled = img.Page > 1;
        NextPage.IsEnabled = img.Page < img.PageCount;
        NextHit.IsVisible = PrevHit.IsVisible = item.Result.ContentMatch;
        if (atEnd)
            Dispatcher.UIThread.Post(() => PreviewScroll.Offset = new Vector(0, PreviewScroll.Extent.Height), DispatcherPriority.Background);
        else if (img.Highlights.Count > 0) ScrollToHighlight(box);
        else Dispatcher.UIThread.Post(() => PreviewScroll.Offset = default, DispatcherPriority.Background);
    }

    /// <summary>Brings the n-th marked word of the page (top to bottom) into view, a little below the top edge.</summary>
    private void ScrollToHighlight(int n)
    {
        if (_marks.Count == 0) return;
        var current = _marks[Math.Clamp(n, 0, _marks.Count - 1)];
        // The hit Ctrl+↑/↓ moved to stands out from the others, and a ring closes in on it.
        var brush = (IBrush?)Application.Current?.FindResource(Application.Current.ActualThemeVariant, "PageHitBrush") ?? Brushes.Yellow;
        foreach (var m in _marks) m.Fill = m == current ? CurrentPageHit : brush;
        RingAround(current);
        double y = Canvas.GetTop(current);
        Dispatcher.UIThread.Post(() => PreviewScroll.Offset = new Vector(0, Math.Max(0, PageFrame.Bounds.Y + y - 60)), DispatcherPriority.Background);
    }

    private readonly List<Rectangle> _marks = new();
    private static readonly IBrush CurrentPageHit = new SolidColorBrush(Color.Parse("#8CFF9500"));
    private static readonly IBrush CurrentTextHit = new SolidColorBrush(Color.Parse("#FFFF9F0A"));
    private static readonly IBrush FocusRing = new SolidColorBrush(Color.Parse("#FF9500"));
    private Rectangle? _ring;
    private DispatcherTimer? _ringTimer;

    /// <summary>A ring that shrinks onto the mark in a third of a second: shows where the focus went.</summary>
    private void RingAround(Rectangle mark)
    {
        var ring = _ring ??= new Rectangle { Stroke = FocusRing, StrokeThickness = 2.5, RadiusX = 4, RadiusY = 4, IsHitTestVisible = false };
        if (!HitLayer.Children.Contains(ring)) HitLayer.Children.Add(ring);
        double x = Canvas.GetLeft(mark), y = Canvas.GetTop(mark), w = mark.Width, h = mark.Height;
        var started = DateTime.UtcNow;
        _ringTimer?.Stop();
        var timer = _ringTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        void Frame()
        {
            double t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / 320);
            double pad = 3 + 22 * (1 - t) * (1 - t);
            ring.Width = w + 2 * pad;
            ring.Height = h + 2 * pad;
            Canvas.SetLeft(ring, x - pad);
            Canvas.SetTop(ring, y - pad);
            ring.Opacity = 0.35 + 0.65 * t;
            if (t >= 1) timer.Stop();
        }
        timer.Tick += (_, _) => Frame();
        Frame();
        timer.Start();
    }

    private DispatcherTimer? _flashTimer;

    /// <summary>Makes the "第 n 处" line light up briefly, so a step is noticed even when the view barely moves.</summary>
    private void FlashHitInfo(string? text = null)
    {
        if (text != null) HitInfo.Text = text;
        HitInfo.Foreground = FocusRing;
        HitInfo.FontWeight = FontWeight.SemiBold;
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer?.Stop();
            HitInfo.ClearValue(TextBlock.FontWeightProperty);
            HitInfo.Foreground = (IBrush?)Application.Current?.FindResource(Application.Current.ActualThemeVariant, "TextSecondary");
        };
        _flashTimer.Start();
    }

    /// <summary>Ctrl+↑ on the first hit or Ctrl+↓ on the last: say so instead of doing nothing.</summary>
    private void NoMoreHits(int step) =>
        FlashHitInfo(step > 0 ? "已经是最后一处命中" : "已经是第一处命中");

    // ---------------------------------------------------------------- hit to hit (Ctrl+↑/↓)

    private int _hitIndex;
    private string? _text;            // text view: the whole text
    private List<int> _textHits = new();

    /// <summary>"文件内 12 处命中 · 第 3 处" above the preview.</summary>
    private void UpdateHitInfo(ResultItem item)
    {
        var r = item.Result;
        int total = _text != null ? _textHits.Count : r.HitCount;
        if (!r.ContentMatch || total <= 0) { HitInfo.Text = r.ContentMatch ? "文件名和正文都匹配" : ""; return; }
        bool steppable = r.HitPages != null || _text != null || SheetPreview.CanPreview(r.Path);
        HitInfo.Text = steppable
            ? $"文件内 {total:N0} 处命中 · 第 {_hitIndex + 1} 处      Ctrl+↑↓ 切换"
            : $"文件内 {total:N0} 处命中";
    }

    private void StepHit(int step)
    {
        if (Results.SelectedItem is not ResultItem item) return;
        if (!item.Result.ContentMatch) { FlashHitInfo("只有文件名匹配，文件内没有可切换的命中"); return; }
        var r = item.Result;
        if (_text != null && _previewItem == item)
        {
            int next = _hitIndex + step;
            if (next < 0 || next >= _textHits.Count) { NoMoreHits(step); return; }
            _hitIndex = next;
            ShowTextWindow();
        }
        else if (r.Kind == FileKind.Pdf && r.HitPages is { Count: > 0 } pages)
        {
            int next = _hitIndex + step;
            if (next < 0 || next >= pages.Count) { NoMoreHits(step); return; }
            _hitIndex = next;
            int page = pages[next];
            int box = next - pages.ToList().IndexOf(page); // which hit on that page
            if (_previewItem == item && _preview?.Page == page) ScrollToHighlight(box);
            else LoadPagePreview(item, page, box: box);
        }
        else if (r.Kind == FileKind.Pdf)
        {
            JumpToHit(step); // index from before 0.4.0: find the page in the PDF itself
            return;
        }
        else if (SheetPreview.CanPreview(r.Path))
        {
            int next = _hitIndex + step;
            if (next < 0) { NoMoreHits(step); return; }
            if (_sheetBusy) return; // still reading for the previous press
            _sheetBusy = true;
            HitInfo.Text = "正在查找…";
            var terms = TextTokenizer.QueryTerms(_lastQuery);
            var path = r.Path;
            _ = Task.Run(() =>
            {
                SheetView? sheet;
                try { sheet = SheetPreview.Read(path, terms, CancellationToken.None, next); }
                catch (Exception) { sheet = null; }
                Dispatcher.UIThread.Post(() =>
                {
                    _sheetBusy = false;
                    if (Results.SelectedItem != item) return;
                    if (sheet == null || sheet.Current < 0) { UpdateHitInfo(item); NoMoreHits(step); return; }
                    _hitIndex = next;
                    ShowSheet(sheet, terms);
                    UpdateHitInfo(item);
                    FlashHitInfo();
                });
            });
            return;
        }
        else { FlashHitInfo("这种文件的预览不能逐处切换，按 Enter 打开后查看"); return; }
        UpdateHitInfo(item);
        FlashHitInfo();
    }

    private bool _sheetBusy;

    private void ShowTextWindow()
    {
        if (_text == null) return;
        int at = _hitIndex < _textHits.Count ? _textHits[_hitIndex] : -1;
        PreviewText.Inlines = Highlight(PagePreview.TextWindow(_text, at, out int local), _lastQuery, local);
        PreviewScroll.Offset = default;
    }

    private void ClearPreviewImage()
    {
        HitLayer.Children.Clear();
        _marks.Clear();
        PageImage.Source = null;
        PageFrame.IsVisible = false;
        PageNav.IsVisible = false;
        TextFrame.IsVisible = false;
        PreviewText.Inlines = null;
        SheetFrame.IsVisible = false;
        SheetGrid.Children.Clear();
        IconPanel.IsVisible = true;
        if (OperatingSystem.IsWindows()) Handler.Unload();
        Handler.IsVisible = false;
        HandlerCaption.IsVisible = false;
        PreviewScroll.IsVisible = true;
        _preview?.Bitmap.Dispose();
        _preview = null;
        _previewItem = null;
    }

    /// <summary>Bolds every occurrence of the query terms inside the snippet.</summary>
    private static InlineCollection Highlight(string text, string query, int current = -1)
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
            // The occurrence Ctrl+↑/↓ is on: solid orange; the others: light yellow.
            inlines.Add(best == current
                ? new Run(text.Substring(best, len)) { FontWeight = FontWeight.Bold, Background = CurrentTextHit, Foreground = Brushes.Black }
                : new Run(text.Substring(best, len)) { FontWeight = FontWeight.Bold, Background = (IBrush?)Application.Current?.FindResource(Application.Current.ActualThemeVariant, "HighlightBrush") });
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
            case Key.Tab when FilterBar.IsVisible:
                SetKind(_kindIndex + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1));
                e.Handled = true;
                break;
            case Key.Down when ctrl:
                StepHit(+1); e.Handled = true; break;
            case Key.Up when ctrl:
                StepHit(-1); e.Handled = true; break;
            case Key.Down:
                Move(+1); e.Handled = true; break;
            case Key.Up:
                Move(-1); e.Handled = true; break;
            case Key.PageDown:
                Move(+8); e.Handled = true; break;
            case Key.PageUp:
                Move(-8); e.Handled = true; break;
            case Key.Enter:
                // Typed and pressed Enter at once: the list still shows the previous query's results
                // (or none). Open the first result for what was typed as soon as it arrives.
                if ((Query.Text ?? "").Trim() != _lastQuery.Trim() && !string.IsNullOrWhiteSpace(Query.Text)) _openWhenReady = ctrl;
                else OpenSelected(reveal: ctrl);
                e.Handled = true;
                break;
            case Key.M when ctrl:
                ToggleFuzzy(); e.Handled = true; break;
            case Key.Right when ctrl && PageNav.IsVisible:
                TurnPage(+1); e.Handled = true; break;
            case Key.Left when ctrl && PageNav.IsVisible:
                TurnPage(-1); e.Handled = true; break;
            // No pages to turn (text, spreadsheets): Ctrl+←/→ step through the hits like Ctrl+↑/↓.
            case Key.Right when ctrl && Results.SelectedItem is ResultItem { Result.ContentMatch: true }:
                StepHit(+1); e.Handled = true; break;
            case Key.Left when ctrl && Results.SelectedItem is ResultItem { Result.ContentMatch: true }:
                StepHit(-1); e.Handled = true; break;
            case Key.C when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (Results.SelectedItem is ResultItem it) _ = Clipboard?.SetTextAsync(it.Result.Kind == FileKind.Calculator ? it.Result.Value : it.Result.Path);
                e.Handled = true;
                break;
        }
    }

    private bool? _openWhenReady;

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
            else if (r.ContentMatch || (_previewItem == item && _preview?.Page > 1))
            {
                // Open at the page on screen (the hit, or where the user turned to), else the indexed one.
                int? page = (_previewItem == item ? _preview?.Page : null) ?? r.Page;
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
