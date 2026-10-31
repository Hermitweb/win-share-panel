using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinShare.App.Events;
using WinShare.App.Navigation;
using WinShare.App.Shell;

namespace WinShare.App;

/// <summary>
/// 主窗口壳（对齐 electron/main.ts 主窗口语义）：
/// - 1200×800 / 最小 960×640（对齐 BrowserWindow 配置）
/// - Win11 检测（对齐 isWin11 build≥22000）→ DWM 亚克力背景（对齐 setBackgroundMaterial('acrylic')）
/// - 关闭 = 最小化到托盘（对齐 main.ts close→hide；AppShell.IsQuitting 放行）
/// - F5 刷新（对齐 useTickEffect hotkey；经事件总线发 RefreshRequested）
/// </summary>
public partial class MainWindow : Window
{
    sealed record NavDisplay(string Route, string Glyph, string Title);

    readonly NavigationService _nav;



    public MainWindow(NavigationService nav)
    {
        _nav = nav;
        InitializeComponent();

        BuildNavDisplays();

        // 初始页：仪表板（TS 默认路由 '/'）
        _nav.Navigate("/");
        PageHost.Content = _nav.CurrentView;
        _nav.RouteChanged += OnRouteChanged;

        // Win11 亚克力背景（对齐 main.ts：win11 → acrylic；失败静默回退系统外观）
        if (Win11Detector.IsWin11())
        {
            Loaded += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                DwmBackdrop.TryApply(hwnd);
                LogSink.Info("main", $"[arch] Win11（build {Environment.OSVersion.Version.Build}）→ DWM 系统背景已启用");
            };
        }
        else
        {
            LogSink.Info("main", $"[arch] 非 Win11（build {Environment.OSVersion.Version.Build}）→ 系统默认外观");
        }

        // 关闭 = 最小化到托盘（对齐 main.ts close → hide）
        Closing += (_, e) =>
        {
            if (!AppShell.IsQuitting)
            {
                e.Cancel = true;
                Hide();
            }
        };

        // F5 全局刷新 / Ctrl+K 命令面板（对齐 TS hotkey 面）
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                AppServices.Bus.Publish(new RefreshRequested());
            }
            else if (e.Key == Key.K &&
                     (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                OnCommandPalette(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
    }

    void BuildNavDisplays()
    {
        NavList.ItemsSource = AppServices.Registry.Pages
            .Select(p => new NavDisplay(p.route, p.glyph, AppServices.I18n.T(p.titleKey)))
            .ToList();
    }

    void OnRouteChanged(string route)
    {
        PageHost.Content = _nav.CurrentView;
        var items = NavList.ItemsSource as System.Collections.IList;
        var idx = 0;
        if (items is not null)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (((NavDisplay)items[i]!).Route == route) { idx = i; break; }
            }
        }
        // 反向同步选中项时必须设守卫：否则会再次触发 OnNavSelectionChanged，
        // 叠加下面的延迟构建就形成导航回环（实测会把页面换成错误的那一页）。
        _navSyncing = true;
        try { NavList.SelectedIndex = idx; }
        finally { _navSyncing = false; }
    }

    bool _navSyncing;

    void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navSyncing) return; // 来自 OnRouteChanged 的反向同步，不是用户点击
        if (NavList.SelectedItem is not NavDisplay nd) return;
        // 【回退延迟构建】把页面构建推到 Background 优先级实测会让页面错位一页
        // （会话监控位置显示成应用设置的内容）——导航选择与路由同步耦合太紧，异步化不安全。
        // 保持同步构建；感知速度改从「降低构造成本」入手。
        _nav.Navigate(nd.Route);
        PageHost.Content = _nav.CurrentView;
    }


    // ===== 窗口控制（无边框自定义标题栏，规范 §9.1） =====
    void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnMaximizeRestore(sender, e);
            return;
        }
        try { DragMove(); } catch { /* 最大化/拖拽竞态忽略 */ }
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // 与 Closing 一致：非退出态收进托盘
        if (!AppShell.IsQuitting) Hide(); else AppShell.Quit();
    }

    void OnTitleMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is { } menu)
        {
            menu.PlacementTarget = btn;
            menu.IsOpen = true;
        }
    }

    // ===== 全屏加载遮罩（承接首屏 2-3s 的 PowerShell 读取） =====
    /// <summary>显示加载遮罩并启动旋转动画（标题文案可定制）。淡入 150ms。</summary>
    public void ShowLoading(string? text = null)
    {
        if (LoadingOverlay is null) return;
        if (!string.IsNullOrWhiteSpace(text)) LoadingText.Text = text;
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingOverlay.Opacity = 0;
        LoadingOverlay.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        StartSpinner();
    }

    /// <summary>
    /// 隐藏加载遮罩并停动画。
    /// 不做淡出：依赖动画 Completed 再 Collapsed 会在动画被覆盖/零跨度时永不触发，
    /// 表现为「数据早加载完了遮罩还挂在上面」（实测踩到过）。揭幕必须是无条件的。
    /// </summary>
    public void HideLoading()
    {
        if (LoadingOverlay is null) return;
        StopSpinner();
        LoadingOverlay.BeginAnimation(OpacityProperty, null); // 取消进行中的淡入
        LoadingOverlay.Opacity = 1;
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    System.Windows.Media.Animation.Storyboard? _spinner;

    // 用正规 Storyboard 承载旋转：原先是空 Storyboard + BeginAnimation（动画实际由依赖属性
    // 系统持有，Stop 时对空 Storyboard 调 Stop() 是空操作）。匀速旋转——加缓动会让
    // spinner 每圈首尾减速，视觉上像卡顿。
    private void StartSpinner()
    {
        if (_spinner is not null) return;
        var rotate = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        var sb = new System.Windows.Media.Animation.Storyboard();
        sb.Children.Add(rotate);
        System.Windows.Media.Animation.Storyboard.SetTarget(rotate, SpinnerRotate);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(rotate,
            new System.Windows.PropertyPath(System.Windows.Media.RotateTransform.AngleProperty));
        sb.Begin();
        _spinner = sb;
    }

    private void StopSpinner()
    {
        if (_spinner is null) return;
        _spinner.Stop();
        SpinnerRotate?.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        _spinner = null;
    }

    /// <summary>标题栏右侧状态文案（如「已连接本机 SMB」/「只读」），空串则隐藏。</summary>
    public void SetStatusHint(string? text)
    {
        if (TitleStatus is null) return;
        TitleStatus.Text = text ?? "";
    }

    // ===== 菜单（对齐 main.ts 主菜单 + 托盘菜单面） =====
    void OnMenuShowWindow(object sender, RoutedEventArgs e) => AppShell.ShowMainWindow();

    void OnMenuOpenLogDir(object sender, RoutedEventArgs e)
    {
        AppShell.OpenLogDirectory();
    }

    void OnMenuQuit(object sender, RoutedEventArgs e)
    {
        AppShell.Quit(); // 清理托盘/采样器/单实例锁（对齐 before-quit）
        Application.Current.Shutdown(); // OnExit 兜底再清一次（幂等，对齐 will-quit）
    }

    void OnMenuRefresh(object sender, RoutedEventArgs e) => AppServices.Bus.Publish(new RefreshRequested());

    void OnMenuThemeLight(object sender, RoutedEventArgs e) => AppServices.Theme.Apply(ThemeModeOf(false));
    void OnMenuThemeDark(object sender, RoutedEventArgs e) => AppServices.Theme.Apply(ThemeModeOf(true));

    static WinShare.Core.Contracts.ThemeMode ThemeModeOf(bool dark)
        => dark ? WinShare.Core.Contracts.ThemeMode.Dark : WinShare.Core.Contracts.ThemeMode.Light;

    void OnMenuAbout(object sender, RoutedEventArgs e)
        => MessageBox.Show(this, "WinShare Panel（WPF 版）· T3a 壳 + 仪表板", "关于",
            MessageBoxButton.OK, MessageBoxImage.Information);

    void OnCommandPalette(object sender, RoutedEventArgs e)
    {
        // 命令面板（对齐 TS Ctrl+K CommandPalette：静态导航 + 操作面）
        var palette = new Shell.CommandPaletteWindow { Owner = this };
        palette.ShowDialog();
    }
}
