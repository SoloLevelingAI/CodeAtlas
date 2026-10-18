using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CodeAtlas.Automation;
using CodeAtlas.Contracts;
using CodeAtlas.Exploration;
using Microsoft.Win32;
using CodeAtlas.Framework;

namespace CodeAtlas.Desktop;

/// <summary>原生桌面视图适配器；业务分析通过工作区端口执行，UI 只处理呈现与定位。</summary>
public partial class MainWindow : Window
{
    private readonly DesktopWorkspace workspace;
    private readonly string sessionName;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private bool compact;
    private bool detailsOpen;
    private bool locating;
    private int locateVersion;
    private bool closed;
    private double detailWidth = 420;
    private ExplorerSession Session => workspace.Session;

    /// <summary>绑定稳定的会话对象，订阅界面相关状态变化。</summary>
    public MainWindow(DesktopWorkspace workspace, string sessionName)
    {
        this.workspace = workspace; this.sessionName = sessionName;
        InitializeComponent(); DataContext = workspace;
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); Session.Search(SearchBox.Text); SearchPanel.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible; };
        scrollTimer.Tick += (_, _) => { scrollTimer.Stop(); TrackVisibleRoot(); };
        Session.PropertyChanged += OnSessionChanged;
        Session.LocateRequested += RequestLocate;
    }

    /// <summary>打开可选的启动报告；没有文件时保留可操作的空工作区。</summary>
    public async Task StartAsync(string? report)
    {
        if (report is not null) await workspace.OpenAsync(report);
    }

    /// <summary>由本机控制适配器在 UI 线程执行白名单指令。</summary>
    public async Task<object?> ExecuteControlAsync(ControlRequest request)
    {
        Dictionary<string, string> a = request.Arguments;
        string Need(string key) => a.GetValueOrDefault(key) ?? throw new ArgumentException("缺少参数：" + key);
        string[] allowed = request.Command switch
        {
            "status" or "undo" or "redo" or "cancel" => [], "open" => ["report"], "analyze" => ["root"],
            "select" or "locate" => ["id"], "expand" => ["id", "expanded"], "search" => ["query"],
            "view" => ["mode"], "capture" => ["out"], "window" => ["width", "height", "detail-width"], "scroll" => ["offset"],
            _ => throw new NotSupportedException("暂不支持桌面指令：" + request.Command)
        };
        if (a.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))) throw new ArgumentException("指令包含不支持的参数。");
        switch (request.Command)
        {
            case "open": await workspace.OpenAsync(Need("report")); break;
            case "analyze":
                if (workspace.IsBusy) throw new InvalidOperationException("已有操作正在运行。");
                _ = RunRemoteAnalysisAsync(Need("root"));
                return new { accepted = true, message = "使用 ui status 查询进度，ui cancel 取消。" };
            case "cancel": workspace.Cancel(); break;
            case "select": case "locate":
                Session.SetView("tree"); Session.Select(Need("id"));
                if (Session.Selected is { } target) await LocateAsync(target);
                break;
            case "expand":
                if (!bool.TryParse(Need("expanded"), out bool expanded)) throw new ArgumentException("expanded 必须为 true 或 false。");
                Session.Expand(Need("id"), expanded); break;
            case "search":
                searchTimer.Stop(); SearchBox.Text = Need("query"); searchTimer.Stop(); Session.Search(SearchBox.Text);
                SearchPanel.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                return new { limit = 100, results = Session.SearchResults.Select(Describe) };
            case "view": Session.SetView(Need("mode")); break;
            case "undo": Session.Undo(); break;
            case "redo": Session.Redo(); break;
            case "window":
                if (!int.TryParse(Need("width"), out int width) || !int.TryParse(Need("height"), out int height) || width is < 900 or > 3840 || height is < 650 or > 2160)
                    throw new ArgumentException("窗口宽度需要 900–3840，高度需要 650–2160。");
                if (a.TryGetValue("detail-width", out string? panelWidth))
                {
                    if (!int.TryParse(panelWidth, out int requested) || requested < 320 || requested > width - 500)
                        throw new ArgumentException("详情宽度至少 320，且须给目录保留 500 像素。");
                    detailWidth = requested;
                }
                Width = width; Height = height; ApplyPanelLayout(); break;
            case "scroll":
                if (!double.TryParse(Need("offset"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double offset) || !double.IsFinite(offset) || offset < 0)
                    throw new ArgumentException("offset 必须是非负有限数值。");
                FindTreeScroll()?.ScrollToVerticalOffset(offset); await Task.Delay(120); TrackVisibleRoot(); break;
            case "capture":
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                return new { path = Capture(Need("out")), width = RootSurface.ActualWidth, height = RootSurface.ActualHeight };
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        return new
        {
            session = sessionName, busy = workspace.IsBusy, workspace.Error, workspace.Status,
            report = Session.Index?.Report.Id, Session.Query, Session.View, Session.CanUndo, Session.CanRedo,
            history = Session.History.Count, materialized = Session.Index?.MaterializedCount,
            selected = Session.Selected is { } item ? Describe(item) : null,
            currentModule = Session.CurrentModule?.Id,
            modules = Session.Roots.Select(r => new { r.Id, r.Title, r.IsExpanded, r.IsCurrent }),
            window = new { Width = ActualWidth, Height = ActualHeight, compact, detailWidth = DetailColumn.ActualWidth, preferredDetailWidth = detailWidth, sourceWrap = SourcePreview.TextWrapping.ToString(), sourceHorizontalScroll = SourcePreview.HorizontalScrollBarVisibility.ToString() },
            viewport = new { offset = FindTreeScroll()?.VerticalOffset, extent = FindTreeScroll()?.ExtentHeight }
        };
    }

    /// <summary>为自动化输出无循环引用的节点摘要。</summary>
    private static object Describe(ExplorerItem item) => new { item.Id, item.Title, item.Summary, item.Kind, item.Role, item.Source, item.Inputs, item.Outputs, item.IsExpanded };

    /// <summary>控制服务故障不关闭工作窗口。</summary>
    public void ReportControlFailure(string message) => Title = "Code Atlas · CLI 连接不可用：" + message;

    /// <summary>从文件选择器打开快照。</summary>
    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Code Atlas 报告 (*.json)|*.json", Title = "打开分析报告" };
        if (dialog.ShowDialog(this) == true) await GuardAsync(() => workspace.OpenAsync(dialog.FileName));
    }

    /// <summary>选择待分析源码目录，后端只读取源码，不运行目标项目。</summary>
    private async void OnAnalyze(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择源码仓库" };
        if (dialog.ShowDialog(this) == true) await GuardAsync(() => workspace.AnalyzeAsync(dialog.FolderName));
    }
    /// <summary>取消耗时任务。</summary>
    private void OnCancel(object sender, RoutedEventArgs e) => workspace.Cancel();
    /// <summary>显示树视图。</summary>
    private void OnTree(object sender, RoutedEventArgs e) => Session.SetView("tree");
    /// <summary>显示已聚合的模块关系。</summary>
    private void OnGraph(object sender, RoutedEventArgs e) => Session.SetView("graph");
    /// <summary>撤销上次浏览动作。</summary>
    private void OnUndo(object sender, RoutedEventArgs e) => Session.Undo();
    /// <summary>重做上次浏览动作。</summary>
    private void OnRedo(object sender, RoutedEventArgs e) => Session.Redo();

    /// <summary>选择只更新详情，不把主目录替换成另一个页面。</summary>
    private void OnTreeSelection(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ExplorerItem item && !item.Id.StartsWith("placeholder:", StringComparison.Ordinal)) Session.Select(item.Id, locate: false);
    }

    /// <summary>显式展开动作进入撤销栈，不重新生成整棵树。</summary>
    [AtlasComponent("atlas.explore.desktop.state", ArchitecturalRole.Presentation)]
    private void OnToggleNode(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ExplorerItem item) Session.Expand(item.Id, !item.IsExpanded);
        e.Handled = true;
    }

    /// <summary>小地图回到对应目录分组，保留已展开子项。</summary>
    private void OnMinimap(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ExplorerItem item) { Session.SetView("tree"); Session.Select(item.Id); }
    }

    /// <summary>短暂防抖后查找用途与名称，普通鼠标移动没有后台请求。</summary>
    [AtlasComponent("atlas.explore.desktop.search", ArchitecturalRole.Presentation)]
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (workspace is null) return;
        searchTimer.Stop(); searchTimer.Start();
    }

    /// <summary>选择搜索结果后展开其真实祖先，回到主目录中的对应位置。</summary>
    [AtlasComponent("atlas.explore.desktop.search", ArchitecturalRole.Presentation)]
    private void OnSearchSelection(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not ExplorerItem item) return;
        Session.SetView("tree"); Session.Select(item.Id); SearchBox.Clear(); SearchPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>窗口变窄时将详情改为按需显示的原生侧面板。</summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyPanelLayout();

    /// <summary>按窗口可用空间应用阅读比例，窄窗口保留用户的宽度偏好。</summary>
    private void ApplyPanelLayout()
    {
        if (WorkspaceGrid is null) return;
        compact = ActualWidth < 1180;
        DetailColumn.MinWidth = compact ? 0 : 320;
        DetailColumn.Width = new GridLength(compact ? 0 : Math.Min(detailWidth, Math.Max(320, ActualWidth - 500)));
        WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 16);
        PanelSplitter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ShowDetails.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(DetailPanel, compact ? 0 : 2); Grid.SetColumnSpan(DetailPanel, compact ? 3 : 1);
        DetailPanel.Width = compact ? Math.Min(detailWidth, ActualWidth - 80) : double.NaN;
        DetailPanel.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        DetailPanel.Visibility = !compact || detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        CloseDetails.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        if (workspace is not null && Session.View == "graph") DrawGraph();
    }
    /// <summary>保留拖动后的面板宽度；目录和源码对象不重新创建。</summary>
    private void OnPanelResized(object sender, DragCompletedEventArgs e)
    {
        if (!compact) detailWidth = DetailColumn.ActualWidth;
        if (Session.View == "graph") DrawGraph();
    }
    /// <summary>双击分隔条恢复默认阅读比例。</summary>
    private void OnResetPanel(object sender, MouseButtonEventArgs e)
    { detailWidth = 420; ApplyPanelLayout(); }
    /// <summary>按需显示窄窗口详情。</summary>
    private void OnShowDetails(object sender, RoutedEventArgs e) { detailsOpen = true; DetailPanel.Visibility = Visibility.Visible; }
    /// <summary>关闭详情浮层不清除当前选择。</summary>
    private void OnHideDetails(object sender, RoutedEventArgs e) { detailsOpen = false; if (compact) DetailPanel.Visibility = Visibility.Collapsed; }

    /// <summary>展示当前快照的带行号源码摘录，不能误读成磁盘上的实时版本。</summary>
    private void OnSource(object sender, RoutedEventArgs e)
    {
        if (!Session.CanViewSource || Session.Selected is null) return;
        var source = new TextBox { Text = Session.Source, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 13,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(16), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(244, 247, 245)) };
        new Window { Owner = this, Title = "源码摘录（只读 / 自动换行） · " + Session.Selected.SourceLabel, Width = Math.Min(1000, ActualWidth - 60), Height = Math.Min(650, ActualHeight - 50),
            MinWidth = 500, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = source }.Show();
    }

    /// <summary>按当前报告展示规范、诊断证据与待执行的整理路线。</summary>
    private void OnArchitecture(object sender, RoutedEventArgs e)
    {
        new ArchitectureWindow(Session.Index?.Report, id =>
        {
            Session.SetView("tree"); Session.Select(id); Activate();
        }) { Owner = this }.Show();
    }
    /// <summary>保留所有已报告的问题和支持限制，不把未支持项隐藏成通过。</summary>
    private void OnFindings(object sender, RoutedEventArgs e)
    {
        if (Session.Index is not { } index) return;
        var tabs = new TabControl { Margin = new Thickness(14) };
        tabs.Items.Add(new TabItem { Header = $"问题与限制 ({index.Report.Findings.Length})", Content = new DataGrid {
            ItemsSource = index.Report.Findings.Select(f => new { f.Severity, f.Rule, f.Message, Path = f.Source.Path, f.Source.Line }),
            IsReadOnly = true, AutoGenerateColumns = true, EnableRowVirtualization = true, EnableColumnVirtualization = true } });
        tabs.Items.Add(new TabItem { Header = "支持范围", Content = new DataGrid { ItemsSource = index.Report.Support, IsReadOnly = true, AutoGenerateColumns = true } });
        new Window { Owner = this, Title = "质量与分析边界", Width = Math.Min(1100, ActualWidth - 40), Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = tabs }.Show();
    }

    /// <summary>处理少量状态通知；索引不变时不重建关系图。</summary>
    [AtlasComponent("atlas.explore.desktop.state", ArchitecturalRole.Presentation)]
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerSession.View))
        {
            DirectoryTree.Visibility = Session.View == "tree" ? Visibility.Visible : Visibility.Collapsed;
            GraphScroll.Visibility = Session.View == "graph" ? Visibility.Visible : Visibility.Collapsed;
            if (Session.View == "graph") DrawGraph();
        }
        if (e.PropertyName == nameof(ExplorerSession.Index)) { DrawGraph(); scrollTimer.Start(); }
    }

    /// <summary>绘制根模块聚合图；节点数量受限且明确标注，完整数据仍在目录与搜索中。</summary>
    [AtlasComponent("atlas.explore.map", ArchitecturalRole.Presentation)]
    private void DrawGraph()
    {
        if (GraphCanvas is null || Session.Index is not { } index) return;
        GraphCanvas.Children.Clear();
        double width = Math.Max(480, DirectoryTree.ActualWidth > 1 ? DirectoryTree.ActualWidth : WorkspaceGrid.ActualWidth - (compact ? 0 : 396));
        int columns = width > 700 ? 2 : 1; double cardWidth = (width - 60) / columns - 18;
        ExplorerItem[] roots = Session.Roots.Take(80).ToArray();
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal);
        for (int i = 0; i < roots.Length; i++) positions[roots[i].Id] = new Point(24 + i % columns * (cardWidth + 24), 26 + i / columns * 130);
        GraphCanvas.Width = width; GraphCanvas.Height = Math.Max(350, Math.Ceiling((double)roots.Length / columns) * 130 + 65);
        foreach (ModuleLink edge in index.ModuleLinks.Take(300))
        {
            if (!positions.TryGetValue(edge.From, out Point a) || !positions.TryGetValue(edge.To, out Point b)) continue;
            Point start = new(a.X + cardWidth / 2, a.Y + 96), end = new(b.X + cardWidth / 2, b.Y);
            if (a.Y >= b.Y) { start.Y = a.Y; end.Y = b.Y + 96; }
            var shape = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(Color.FromRgb(159, 191, 171)), StrokeThickness = 1.3,
                Data = new PathGeometry([new PathFigure(start, [new BezierSegment(new Point(start.X, (start.Y + end.Y) / 2), new Point(end.X, (start.Y + end.Y) / 2), end, true)], false)]),
                ToolTip = $"{edge.Count} 条已解析静态关系（非运行时调用）", IsHitTestVisible = true };
            GraphCanvas.Children.Add(shape);
        }
        foreach (ExplorerItem item in roots)
        {
            Point point = positions[item.Id];
            var label = new StackPanel(); label.Children.Add(new TextBlock { Text = item.Title, FontSize = 16, FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = item.Summary, FontSize = 11, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, MaxHeight = 35, Margin = new Thickness(0, 8, 0, 0) });
            var button = new Button { Content = label, Width = cardWidth, Height = 96, DataContext = item, HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += OnMinimap; Canvas.SetLeft(button, point.X); Canvas.SetTop(button, point.Y); GraphCanvas.Children.Add(button);
        }
        var note = new TextBlock { Text = $"静态引用聚合 · {Math.Min(80, Session.Roots.Count)}/{Session.Roots.Count} 模块 · 最多显示 300 条连线 · 完整元素可搜索", FontSize = 10, Foreground = (Brush)FindResource("Muted") };
        Canvas.SetLeft(note, 22); Canvas.SetTop(note, GraphCanvas.Height - 30); GraphCanvas.Children.Add(note);
    }

    /// <summary>把源于会话的定位请求转换为虚拟化树的容器定位。</summary>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    private async void RequestLocate(ExplorerItem item)
    {
        try { await LocateAsync(item); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { ReportControlFailure("定位失败：" + error.Message); }
    }

    /// <summary>逐层实现目标路径，最后只滚动目录容器。</summary>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    private async Task LocateAsync(ExplorerItem target)
    {
        int version = ++locateVersion;
        var path = new Stack<ExplorerItem>();
        for (ExplorerItem? item = target; item is not null; item = item.Parent) path.Push(item);
        ItemsControl parent = DirectoryTree;
        locating = true;
        try
        {
            while (path.TryPop(out ExplorerItem? item))
            {
                parent.UpdateLayout();
                TreeViewItem? container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (container is null)
                {
                    AtlasVirtualizingPanel? panel = Descendants<AtlasVirtualizingPanel>(parent).FirstOrDefault(p => ItemsControl.GetItemsOwner(p) == parent);
                    int i = parent.Items.IndexOf(item); if (i < 0) return;
                    panel?.Realize(i);
                    await Dispatcher.InvokeAsync(() => parent.UpdateLayout(), DispatcherPriority.Loaded);
                    if (version != locateVersion || closed) return;
                    container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                }
                if (container is null) return;
                if (path.Count > 0) { item.IsExpanded = true; container.IsExpanded = true; parent = container; }
                else
                {
                    container.BringIntoView();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                    if (FindTreeScroll() is { } scroll)
                    {
                        double y = container.TransformToAncestor(scroll).Transform(new Point()).Y;
                        scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + y - 14));
                    }
                }
            }
        }
        finally { if (version == locateVersion) { locating = false; scrollTimer.Start(); } }
    }

    /// <summary>不让普通选择自动把整个界面滚到另一处；显式定位例外。</summary>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    private void OnBringIntoView(object sender, RequestBringIntoViewEventArgs e) { if (!locating) e.Handled = true; }
    /// <summary>滚动位置变化仅触发小地图的轻量同步。</summary>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    private void OnTreeScroll(object sender, ScrollChangedEventArgs e) { if (!locating && e.VerticalChange != 0) { scrollTimer.Stop(); scrollTimer.Start(); } }
    /// <summary>读取当前可见根容器，不重新分析源码或遍历方法。</summary>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    private void TrackVisibleRoot()
    {
        if (locating || Session.View != "tree" || FindTreeScroll() is not { } scroll) return;
        double anchor = Math.Min(64, scroll.ViewportHeight / 3);
        foreach (ExplorerItem item in Session.Roots)
        {
            if (DirectoryTree.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            double y = container.TransformToAncestor(scroll).Transform(new Point()).Y;
            if (y + container.ActualHeight > anchor && y < scroll.ViewportHeight) { Session.TrackVisibleModule(item); return; }
        }
    }
    /// <summary>找到主目录的滚动容器。</summary>
    private ScrollViewer? FindTreeScroll() => Descendants<ScrollViewer>(DirectoryTree).FirstOrDefault();
    /// <summary>仅遍历已实现的视觉元素。</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>处理键盘搜索、历史与树展开；不覆盖文本框自身的编辑撤销。</summary>
    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is not TextBox && e.Key is Key.Z or Key.Y)
        { if (e.Key == Key.Z) Session.Undo(); else Session.Redo(); e.Handled = true; }
        else if (DirectoryTree.IsKeyboardFocusWithin && Session.Selected is { } item && e.Key is Key.Left or Key.Right)
        { Session.Expand(item.Id, e.Key == Key.Right); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchPanel.Visibility = Visibility.Collapsed; detailsOpen = false; if (compact) DetailPanel.Visibility = Visibility.Collapsed; }
    }

    /// <summary>导出原生窗口客户区的 PNG，用于真实视觉回归。</summary>
    private string Capture(string output)
    {
        string path = System.IO.Path.GetFullPath(output);
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("截图输出必须是 .png。");
        RootSurface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)RootSurface.ActualWidth), Math.Max(1, (int)RootSurface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(RootSurface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path); encoder.Save(stream); return path;
    }

    /// <summary>在窗口内报告操作失败，不中断当前阅读会话。</summary>
    private async Task GuardAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    /// <summary>远程分析的错误保留在可查询状态中，不弹出阻塞自动化的对话框。</summary>
    [AtlasComponent("atlas.explore.workspace", ArchitecturalRole.Presentation)]
    private async Task RunRemoteAnalysisAsync(string root)
    {
        try { await workspace.AnalyzeAsync(root); }
        catch (Exception) { /* DesktopWorkspace exposes the failure through Error and Status. */ }
    }

    /// <summary>释放事件与待执行定位，停止当前分析。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true; locateVersion++; searchTimer.Stop(); scrollTimer.Stop(); workspace.Dispose();
        Session.PropertyChanged -= OnSessionChanged; Session.LocateRequested -= RequestLocate;
    }
}
