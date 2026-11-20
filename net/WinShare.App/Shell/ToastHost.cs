using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinShare.App.Views.Settings;

namespace WinShare.App.Shell;

/// <summary>
/// 右下角 Toast 浮层（MainWindow 承载容器，Attach 进 PageHost 右下角）：
/// - 静态 Show 供任意 VM/视图调用（后台线程安全：UI 调度在宿主侧 BeginInvoke）
/// - 全量重建渲染（条目 ≤3，重建成本可忽略）；超时收敛由 500ms 定时器驱动 ToastVm.PruneExpired
/// - 颜色全部 SetResourceReference 活引用（跟随主题热切换，对齐 SettingsUi.Live 惯例）
/// </summary>
public static class ToastHost
{
    /// <summary>全局 Toast 队列（纯逻辑；单测直接 new ToastVm，宿主静态实例只用于生产接线）。</summary>
    public static readonly ToastVm Vm = new();

    static Panel? _layer;
    static DispatcherTimer? _pruneTimer;

    /// <summary>MainWindow 挂载浮层容器（可重复调用，后者覆盖）。</summary>
    public static void Attach(Panel layer)
    {
        _layer = layer;
        if (_pruneTimer is null)
        {
            _pruneTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _pruneTimer.Tick += (_, _) => Vm.PruneExpired(DateTimeOffset.Now);
            _pruneTimer.Start();
        }
        Vm.Changed += Render;
        Render();
    }

    /// <summary>弹一条 Toast（success 4s / warn 6s 自动消失，danger 手动关闭）。</summary>
    public static ToastItem Show(ToastLevel level, string text) => Vm.Push(level, text);

    static void Render()
    {
        Panel? layer = _layer;
        if (layer is null) return;
        layer.Dispatcher.BeginInvoke(() =>
        {
            Panel? live = _layer;
            if (live is null) return;
            live.Children.Clear();
            foreach (var item in Vm.Items) live.Children.Add(BuildToast(item));
            live.Visibility = live.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    static FrameworkElement BuildToast(ToastItem item)
    {
        var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center };
        dot.Live(Shape.FillProperty, LevelBrushKey(item.Level));

        var text = new TextBlock
        {
            Text = item.Text,
            MaxWidth = 360,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Live(TextBlock.ForegroundProperty, "BrushText");

        var close = new Button
        {
            Content = "✕",
            FontSize = 11,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        close.Click += (_, _) => Vm.Dismiss(item.Id);
        close.Live(TextBlock.ForegroundProperty, "BrushFog");

        var row = new StackPanel { Orientation = Orientation.Horizontal, MaxWidth = 430 };
        row.Children.Add(dot);
        row.Children.Add(text);
        row.Children.Add(close);

        var card = new Border
        {
            Child = row,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 9, 8, 9),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };
        card.Live(Border.BackgroundProperty, "BrushCard");
        card.Live(Border.BorderBrushProperty, LevelBrushKey(item.Level));
        return card;
    }

    /// <summary>级别 → 主题 token（静态外观走活引用；语义色映射固定）。</summary>
    public static string LevelBrushKey(ToastLevel level) => level switch
    {
        ToastLevel.Success => "BrushSuccess",
        ToastLevel.Warn => "BrushWarning",
        _ => "BrushDanger",
    };
}
