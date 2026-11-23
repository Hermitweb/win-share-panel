using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinShare.App.Dashboard;
using WinShare.App.State;
using WinShare.App.Views.Charts;
using WinShare.Core.Contracts;

namespace WinShare.App.Views;

/// <summary>
/// 仪表板页（对齐 src/pages/Dashboard.tsx）：
/// 界面逻辑全部在 DashboardViewModel；本文件只做视图装配 + 数据到控件映射。
/// 导出走 VM 命令（PNG=图表区 RenderTargetBitmap+PngBitmapEncoder；CSV=CsvExporter 逐字段对齐 TS）。
/// </summary>
public partial class DashboardPageView : UserControl
{
    DashboardViewModel? _vm;

    // 保活后页面会被重复挂载（Loaded 再次触发）。没有这个守卫时，每次重挂载都会
    // 再订阅一次 PropertyChanged + 再起一轮自动刷新，N 次重挂载后一次 VM 变更触发 N 次
    // ApplyVm()（每次都重建协议网格/磁盘列表并重绘两张图表）→ UI 线程被淹没，表现为「点回仪表盘卡死」。
    bool _wired;
    bool _applyQueued;
    System.ComponentModel.PropertyChangedEventHandler? _vmHandler;

    public DashboardPageView()
    {
        InitializeComponent();
        // 图表区注册进 PNG 捕获器（页面销毁后注册仍指向旧控件 → 重建页即重新注册）
        ChartPngCapture.Register("topshares", TopChart);
    }

    /// <summary>
    /// 代码级滚动（滚轮由 MainWindow 转发）：滚动与内容渲染彻底分离——
    /// 内容（~6 张卡）挂载后只渲染一次；CanContentScroll=False 的像素滚动由
    /// WPF 合成器平移已光栅化的内容（ScrollContentPresenter 的 presentation
    /// transform），滚轮 tick 不触发任何重渲染/重布局。偏移状态由 ScrollViewer
    /// 单一代码路径读写（本方法 + 系统滚动条），无其它实现。
    /// </summary>
    public void ScrollBy(double delta)
    {
        PageScroll.ScrollToVerticalOffset(Math.Max(0, PageScroll.VerticalOffset - delta));
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += async (_, _) =>
        {
            if (_wired) return;
            _wired = true;
            _vm = AppServices.DashboardViewModel;
            // VM 的 PropertyChanged 可能在后台线程（RefreshAsync 续体/采样器）触发；
            // 控件赋值必须回 UI 线程，否则 InvalidOperationException 会被 ApplyVm 的 catch 吞掉，
            // 表现为「卡片只剩标题、动态内容永不出现」——曾据此误判为渲染/Effect 问题。
            // 一次数据刷新会改 ~10 个属性；逐个触发 ApplyVm 等于把整页（协议网格/磁盘列表/两张图）
            // 重建 10 遍。改为每个调度周期只合并成一次（默认 10s 刷新一次、单次 ~7s，
            // 不合并的话 UI 线程大半时间都在重建 → 整体卡顿）。
            // 保存 handler 以便退订：DashboardViewModel 是静态单例，页面每次重建都会重新订阅；
            // 不退订的话访问 N 次就累积 N 个 handler，VM 每轮刷新触发 N 次 ApplyVm
            // （绝大多数打在已销毁的页面上）→ 常驻卡顿，且随访问次数变严重。
            _vmHandler = (_, _) =>
            {
                if (_applyQueued) return;
                _applyQueued = true;
                Dispatcher.BeginInvoke(new Action(() => { _applyQueued = false; ApplyVm(); }),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
            _vm.PropertyChanged += _vmHandler;
            await _vm.RefreshAsync();
            ApplyVm();
            // 首帧就绪 → 揭掉主窗口的全屏加载遮罩（比定时器更准：真正画完才揭）
            if (Window.GetWindow(this) is MainWindow mw) mw.HideLoading();
            _vm.StartAutoRefresh();
        };
        // 卸载时退订（关键修复）：VM 是单例，不退订就会累积 handler。
        // 不再在 Unloaded/IsVisibleChanged 里启停共享 VM 的定时器 —— 旧页的 Unloaded 可能
        // 晚于新页的 IsVisibleChanged 触发，会把新页刚启动的定时器又停掉（两边互抢）。
        // 定时器由 VM 常驻（默认 60s 一轮，开销可忽略），页面只在挂载期间接收通知。
        Unloaded += (_, _) =>
        {
            if (_vm is not null && _vmHandler is not null)
            {
                _vm.PropertyChanged -= _vmHandler;
                _vmHandler = null;
            }
        };
    }

    /// <summary>UI 线程守卫：任何线程调用都安全（非 UI 线程自动转投 Dispatcher）。</summary>
    void ApplyVm()
    {
        if (_vm is null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyVm));
            return;
        }
        try { ApplyVmCore(); }
        // 不再静默：布局/绑定异常必须能在 app.log 里按 error 级别看到（曾因降级为 Warn 而漏判）
        catch (Exception e)
        {
            Shell.LogSink.Error("dashboard", "ApplyVm 渲染失败：" + e);
        }
    }

    void ApplyVmCore()
    {

        TxtShareCount.Text = _vm.ShareCount.ToString();
        TxtSessions.Text = _vm.ActiveSessions.ToString();
        TxtOpenFiles.Text = _vm.OpenFiles.ToString();
        TxtService.Text = _vm.ServiceStatusText;
        // v2.2.0 第二轮：零共享引导（有共享即隐藏；数据未就绪时不弹，避免闪现）
        NoSharesGuide.Visibility = _vm.ShareCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        var ok = TryFindResource("ColorSuccess") is Color cOk;
        var bad = TryFindResource("ColorDanger") is Color cBad;
        var statusColor = _vm.ServiceRunning ? (ok ? cOk : Color.FromRgb(0x52, 0xC4, 0x1A))
            : (bad ? cBad : Color.FromRgb(0xFF, 0x4D, 0x4F));
        TxtService.Foreground = new SolidColorBrush(statusColor);
        SvcTag.Background = new SolidColorBrush(Color.FromArgb(0x26, statusColor.R, statusColor.G, statusColor.B));

        // 需要关注（AlertEvaluator 命中项；动作按钮 → 导航透传 attention）
        AttentionList.ItemsSource = _vm.AttentionItems;
        AttentionEmpty.Text = _vm.AttentionItems.Count == 0
            ? "暂无需要关注项"
            : $"共 {_vm.AttentionItems.Count} 项需要关注";
        AttentionEmpty.Visibility = Visibility.Visible; // 始终可见：宁可多一行说明，也不留空白卡
        var skipped = _vm.SkippedSourcesText;
        // v2.2.0 第五批：正文只留计数，明细进 Tooltip（此前整串来源常驻占一行）
        int skippedCount = skipped.Length == 0
            ? 0
            : skipped.Split(new[] { ',', '，', '、', ';' }, StringSplitOptions.RemoveEmptyEntries).Length;
        SkippedNote.Text = skippedCount > 0 ? $"已跳过 {skippedCount} 项" : "";
        SkippedChip.Visibility = skippedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkippedChip.ToolTip = skippedCount > 0
            ? $"因数据不可用而跳过的检查：{skipped}（未命中不代表该项安全，请修复后刷新）"
            : null;

        // 趋势（当前 WPF 自绘；LiveCharts2(SkiaSharpWPF) 渲染器接入点见 DOCS.md）
        TrendChart.Points = _vm.TrendRecent;
        TrendChart.Mode = _vm.TrendMode;
        // v2.3：两个空态互斥——都命中时（无任何采样点）只显示「暂无采样点」，
        // 否则两行居中提示叠印在一起（用户截图实证）
        TrendEmptyAll.Visibility = (_vm.TrendAllEmpty && !_vm.TrendWindowEmpty) ? Visibility.Visible : Visibility.Collapsed;
        // v2.3：图表控件不再自绘空态——外层显示任一空态时关闭，避免与 XAML 文案叠印
        TrendChart.DrawEmptyText = !(_vm.TrendAllEmpty || _vm.TrendWindowEmpty);
        if (_vm.TrendWindowEmpty)
        {
            TrendEmptyWindow.Text = "最近 24 小时没有采样点（更早的历史点已超出显示窗口）";
            TrendEmptyWindow.Visibility = Visibility.Visible;
        }
        else
        {
            TrendEmptyWindow.Visibility = Visibility.Collapsed;
        }

        TopChart.Items = _vm.TopShares;
        BuildProtocolGrid();
        BuildDiskList();

        var tip = _vm.ErrorText;
        StatusTip.Text = tip.Length == 0 ? "" : tip;
        StatusTip.Visibility = tip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        TrendChart.InvalidateVisual();
        TopChart.InvalidateVisual();

    }

    /// <summary>
    /// 磁盘水位卡（对齐 TS Dashboard.tsx#546-583：VM 已按等级排序，这里只绑 ItemsSource +
    /// 空态切换；进度条/文案/等级标签全在 XAML DataTemplate，避免代码后置搬控件）。
    /// </summary>
    void BuildDiskList()
    {
        if (_vm is null) return;
        var rows = _vm.DiskRows;
        DiskList.ItemsSource = rows;
        var empty = rows.Count == 0;
        DiskList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        DiskEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    // v2.3 协议磁贴 hue 配色（对齐参考稿品牌色卡；顺序与 ProtocolPalette.TagColor 一致：
    // SMB=蓝 / NFS=紫 / FTP=绿 / WebDAV=橙）。标签文字色仍走 ProtocolPalette（与 Electron 对齐）。
    static readonly string[] HueGradientKeys = { "BrushHueBlueGradient", "BrushHuePurpleGradient", "BrushHueGreenGradient", "BrushHueOrangeGradient" };
    static readonly string[] HueSolidKeys = { "BrushHueBlue", "BrushHuePurple", "BrushHueGreen", "BrushHueOrange" };
    static readonly string[] HueSoftKeys = { "BrushHueBlueSoft", "BrushHuePurpleSoft", "BrushHueGreenSoft", "BrushHueOrangeSoft" };

    static int HueIndexOf(Protocol p) => ProtocolPalette.TagColor(p) switch
    {
        "blue" => 0, "purple" => 1, "green" => 2, "orange" => 3, _ => 0,
    };

    void BuildProtocolGrid()
    {
        if (_vm is null) return;
        ProtocolGrid.Children.Clear();
        bool hasRows = _vm.ProtocolRows.Count > 0;
        // 空态用 XAML 静态控件（运行期集合渲染在某些容器下不可见，静态节点必定绘制）
        if (ProtocolEmpty is not null) ProtocolEmpty.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        if (!hasRows) return;
        // v2.2.0 第五批：自适应 4 列栅格（此前两个 240px 定宽纵列只占卡内左半，右侧约 45% 空白）。
        // 用「星号列 Grid + 固定行高」而非 UniformGrid：后者在纵向 StackPanel（高度无限测量）
        // 下算出的 BoundingRectangle=∞（仓库 SettingsUi 注释已记载该坑）。
        int colCount = Math.Max(1, Math.Min(4, _vm.ProtocolRows.Count));
        var grid = new Grid();
        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rowCount = (int)Math.Ceiling(_vm.ProtocolRows.Count / (double)colCount);
        for (int r = 0; r < rowCount; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int idx = 0;
        foreach (var row in _vm.ProtocolRows)
        {
            // 磁贴 = 柔和 hue 底 + 渐变字母芯片（首字母，白色必可辨）+ 协议名 + 共享/会话计数
            int hue = HueIndexOf(row.Protocol);
            var hueGradient = TryFindResource(HueGradientKeys[hue]) as Brush ?? new SolidColorBrush(Color.FromRgb(0x35, 0x84, 0xEC));
            var hueSolid = TryFindResource(HueSolidKeys[hue]) as Brush ?? hueGradient;
            var hueSoft = TryFindResource(HueSoftKeys[hue]) as Brush ?? new SolidColorBrush(Color.FromRgb(0xE4, 0xF0, 0xFE));
            var fog = TryFindResource("BrushFog") as Brush ?? new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB0));
            var text = TryFindResource("BrushText") as Brush ?? Brushes.Black;

            var tile = new Border
            {
                CornerRadius = TryFindResource("RadiusCard") is CornerRadius tcr ? tcr : new CornerRadius(12),
                Padding = new Thickness(12, 10, 10, 10),
                Background = hueSoft,
                Margin = new Thickness(0, 0, 14, 12),
            };
            var tileGrid = new Grid();
            tileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var chip = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
                Background = hueGradient, VerticalAlignment = VerticalAlignment.Center,
            };
            chip.Child = new TextBlock
            {
                Text = ProtocolPalette.Label(row.Protocol).Substring(0, 1),
                FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            tileGrid.Children.Add(chip);
            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = ProtocolPalette.Label(row.Protocol),
                FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = text,
            });
            var counts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            counts.Children.Add(new TextBlock
            {
                Text = "共享 ",
                FontSize = 11,
                Foreground = fog,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 2),
            });
            counts.Children.Add(new TextBlock
            {
                Text = row.Shares.ToString(),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = hueSolid,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });
            counts.Children.Add(new TextBlock
            {
                Text = $"会话 {row.Sessions}",
                FontSize = 11,
                Foreground = fog,
                VerticalAlignment = VerticalAlignment.Center,
            });
            info.Children.Add(counts);
            Grid.SetColumn(info, 1);
            tileGrid.Children.Add(info);
            tile.Child = tileGrid;
            // 4 列栅格：按行优先填充（col = idx % colCount，row = idx / colCount）
            Grid.SetColumn(tile, idx % colCount);
            Grid.SetRow(tile, idx / colCount);
            grid.Children.Add(tile);
            idx++;
        }
        ProtocolGrid.Children.Add(grid);
    }

    // ===== 命令转发（按钮 → VM） =====

    async void OnExportPng(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.ExportPngCommand.ExecuteAsync();
        ApplyVm();
    }

    void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ExportCsvCommand.Execute(null);
        ApplyVm();
    }

    void OnTrendMode(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.TrendMode = ModeSessions.IsChecked == true ? "sessions"
            : ModeOpenFiles.IsChecked == true ? "openFiles"
            : "both";
        ApplyVm();
    }

    /// <summary>零共享引导（v2.2.0 第二轮）：直达共享管理页（该页空态含「新建共享」入口）。</summary>
    void OnGoCreateShare(object sender, RoutedEventArgs e)
    {
        AppServices.Navigation.Navigate("/shares");
    }

    // ===== 统计卡点击跳转（v2.2.0 第二轮第三批：数字卡此前不可交互） =====

    void OnStatShares(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => AppServices.Navigation.Navigate("/shares");

    void OnStatSessions(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => AppServices.Navigation.Navigate("/sessions");

    void OnStatFiles(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => AppServices.Navigation.Navigate("/sessions");

    void OnStatService(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => AppServices.Navigation.Navigate("/settings");

    /// <summary>v2.3 四轮：协议入口卡 → 服务配置页（对应协议配置）。</summary>
    void OnProtoConfig(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => AppServices.Navigation.Navigate("/settings");

    /// <summary>「需要关注」动作按钮：v2.2.0 可自动修复项（SMB1/访客）一键执行——确认后直接写配置并刷新，
    /// 不再让用户自己跳设置页找开关；其余项保持跨页导航（对齐 TS navigate(to, { state: attention })）。</summary>
    async void OnAttentionAction(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.Tag is not AlertEvaluator.AttentionItem item) return;
        if (item.fixKind is null)
        {
            AppServices.Navigation.Navigate(item.route, item.attention);
            return;
        }
        var smb = WinShare.App.Settings.SettingsServiceSlots.SmbService;
        if (smb is null)
        {
            Shell.ToastHost.Show(Shell.ToastLevel.Danger, "SMB 配置服务未就绪：请重启应用后重试（详情见应用日志）");
            return;
        }
        bool smb1 = item.fixKind == "smb1";
        string title = smb1 ? "关闭 SMB1 协议" : "关闭访客访问";
        string detail = smb1
            ? "将写入 SMB 服务器配置关闭 SMB1。依赖 SMB1 的旧设备（Windows XP / 老 NAS）将无法访问共享，确认继续？"
            : "将关闭「访客用户访问」与「不安全来宾登录」：无需凭据的匿名访问将被拒绝，依赖匿名访问的设备会失效，确认继续？";
        var res = MessageBox.Show(detail, title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (res != MessageBoxResult.OK) return;
        try
        {
            var cfg = await smb.GetConfigAsync();
            cfg = smb1
                ? cfg with { enableSMB1Protocol = false }
                : cfg with { enableGuestUserAccess = false, enableInsecureGuestLogons = false };
            await smb.SetConfigAsync(cfg);
            Shell.ToastHost.Show(Shell.ToastLevel.Success, smb1
                ? "SMB1 已关闭：旧的依赖设备将无法连接（如需恢复可在设置页 SMB 选项卡开启）"
                : "访客访问已关闭：匿名探测共享将被拒绝（如需恢复可在设置页 SMB 选项卡开启）");
            if (_vm is not null) await _vm.RefreshAsync();
        }
        catch (Exception ex)
        {
            Shell.ToastHost.Show(Shell.ToastLevel.Danger, Common.ErrorText.Describe(ex, title));
        }
    }
}
