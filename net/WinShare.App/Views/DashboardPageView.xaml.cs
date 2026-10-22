using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinShare.App.Dashboard;
using WinShare.App.State;
using WinShare.App.Views.Charts;

namespace WinShare.App.Views;

/// <summary>
/// 仪表板页（对齐 src/pages/Dashboard.tsx）：
/// 界面逻辑全部在 DashboardViewModel；本文件只做视图装配 + 数据到控件映射。
/// 导出走 VM 命令（PNG=图表区 RenderTargetBitmap+PngBitmapEncoder；CSV=CsvExporter 逐字段对齐 TS）。
/// </summary>
public partial class DashboardPageView : UserControl
{
    DashboardViewModel? _vm;

    public DashboardPageView()
    {
        InitializeComponent();
        // 图表区注册进 PNG 捕获器（页面销毁后注册仍指向旧控件 → 重建页即重新注册）
        ChartPngCapture.Register("topshares", TopChart);
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += async (_, _) =>
        {
            _vm = AppServices.DashboardViewModel;
            // VM 的 PropertyChanged 可能在后台线程（RefreshAsync 续体/采样器）触发；
            // 控件赋值必须回 UI 线程，否则 InvalidOperationException 会被 ApplyVm 的 catch 吞掉，
            // 表现为「卡片只剩标题、动态内容永不出现」——曾据此误判为渲染/Effect 问题。
            _vm.PropertyChanged += (_, _) =>
            {
                if (Dispatcher.CheckAccess()) ApplyVm();
                else Dispatcher.BeginInvoke(new Action(ApplyVm));
            };
            await _vm.RefreshAsync();
            ApplyVm();
            // 首帧就绪 → 揭掉主窗口的全屏加载遮罩（比定时器更准：真正画完才揭）
            if (Window.GetWindow(this) is MainWindow mw) mw.HideLoading();
            _vm.StartAutoRefresh();
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
        var ok = TryFindResource("ColorSuccess") is Color cOk;
        var bad = TryFindResource("ColorDanger") is Color cBad;
        var statusColor = _vm.ServiceRunning ? (ok ? cOk : Color.FromRgb(0x52, 0xC4, 0x1A))
            : (bad ? cBad : Color.FromRgb(0xFF, 0x4D, 0x4F));
        TxtService.Foreground = new SolidColorBrush(statusColor);
        SvcTag.Background = new SolidColorBrush(Color.FromArgb(0x26, statusColor.R, statusColor.G, statusColor.B));

        // 需要关注（AlertEvaluator 命中项；动作按钮 → 导航透传 attention）
        AttentionList.ItemsSource = _vm.AttentionItems;
        AttentionEmpty.Text = _vm.AttentionItems.Count == 0
            ? "暂无需要关注项（已按当前告警规则检查 SMB1 / 访客策略 / 账户口令 / 磁盘水位）"
            : $"共 {_vm.AttentionItems.Count} 项需要关注";
        AttentionEmpty.Visibility = Visibility.Visible; // 始终可见：宁可多一行说明，也不留空白卡
        var skipped = _vm.SkippedSourcesText;
        SkippedNote.Text = skipped.Length > 0
            ? $"因数据不可用而跳过的检查：{skipped}（未命中不代表该项安全，请修复后刷新）"
            : "";
        SkippedNote.Visibility = skipped.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 趋势（当前 WPF 自绘；LiveCharts2(SkiaSharpWPF) 渲染器接入点见 DOCS.md）
        TrendChart.Points = _vm.TrendRecent;
        TrendChart.Mode = _vm.TrendMode;
        TrendEmptyAll.Visibility = _vm.TrendAllEmpty ? Visibility.Visible : Visibility.Collapsed;
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

    void BuildProtocolGrid()
    {
        if (_vm is null) return;
        ProtocolGrid.Children.Clear();
        bool hasRows = _vm.ProtocolRows.Count > 0;
        // 空态用 XAML 静态控件（运行期集合渲染在某些容器下不可见，静态节点必定绘制）
        if (ProtocolEmpty is not null) ProtocolEmpty.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        if (!hasRows) return;
        foreach (var row in _vm.ProtocolRows)
        {
            // 纵向 1 列（B4 决策）：每协议一行，行内标签 + 计数
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var color = WpfColors.ToBrush(ProtocolPalette.Color(row.Protocol));
            var tag = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 1, 10, 1),
                Background = color,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6),
            };
            tag.Child = new TextBlock
            {
                Text = ProtocolPalette.Label(row.Protocol),
                FontSize = 13,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };
            p.Children.Add(tag);
            // 计数行：共享数 + 会话（同一行内的成对短文本，允许横向）
            var counts = new StackPanel { Orientation = Orientation.Horizontal };
            counts.Children.Add(new TextBlock
            {
                Text = "共享数",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            counts.Children.Add(new TextBlock
            {
                Text = row.Shares.ToString(),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = color,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            });
            counts.Children.Add(new TextBlock
            {
                Text = $"会话 {row.Sessions}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            p.Children.Add(counts);
            ProtocolGrid.Children.Add(p);
        }
    }

    // ===== 命令转发（按钮 → VM） =====

    async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.RefreshAsync();
        ApplyVm();
    }

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

    /// <summary>「需要关注」动作按钮 → 跨页导航（对齐 TS navigate(to, { state: attention })）。</summary>
    void OnAttentionAction(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is AlertEvaluator.AttentionItem item)
        {
            AppServices.Navigation.Navigate(item.route, item.attention);
        }
    }
}
