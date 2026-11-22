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
        Shell.UiTestHooks.Prepare(this);

        // 滚轮跟随鼠标：WPF 把 MouseWheel 路由给键盘焦点元素——启动后焦点在侧栏导航
        // ListView（仅横向滚动），滚轮从它向上冒泡永远不经过页面内容区的 ScrollViewer
        // → 全应用滚轮失效（v2.0.1 用户实测「页面无法滚动」）。在窗口 Preview 隧道口
        // 截获，转发给鼠标下方的第一个 ScrollViewer。
        PreviewMouseWheel += OnPreviewMouseWheel;

        BuildNavDisplays();

        // 初始页：仪表板（TS 默认路由 '/'）。订阅必须先于导航：否则首航的 RouteChanged
        // 没有接收者，NavList 的选中胶囊不会同步（v2.3 实测：初始态导航无高亮）
        _nav.RouteChanged += OnRouteChanged;
        _nav.Navigate("/");
        PageHost.Content = _nav.CurrentView;

        // v2.2.0 第四轮：窗口尺寸 → 共享页断点（窄窗隐藏次要列，避免「全部」页签横向滚动 6.9 倍）
        SizeChanged += OnWindowSizeChanged;
        OnWindowSizeChanged(this, null!);

        // Toast 浮层（右下角；全局反馈通道，见 ToastHost）
        ToastHost.Attach(ToastLayer);

        // 非管理员启动期检测（纯 C# WindowsPrincipal，瞬时）：提示条常驻可关，提权入口与设置页共用
        if (!Elevation.IsAdmin())
        {
            AdminBanner.Visibility = Visibility.Visible;
            LogSink.Warn("main", "[elevation] 非管理员身份运行：需管理员的操作将失败（提示条已展示）");
        }

        // v2.2.0 第二轮：顶栏使用模式徽标（常驻提示当前模式——新手用户能看见「为什么高级字段收起」）
        AppShell.OnModeChanged += OnModeChangedForBadge;
        UpdateModeBadge(AppServices.SettingsStore.Load().advancedMode);
        UpdateThemeMenuState();

        // v2.3 底部状态栏：版本号取自程序集（三段式，与 csproj Version 一致）
        FooterVersion.Text = "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "?");
        // 主题在设置页切换时也要同步「更多」菜单的勾选态
        AppServices.Bus.Subscribe<Events.ThemeChanged>(_ => Dispatcher.BeginInvoke(UpdateThemeMenuState));

        // v2.2.0 安装与运行配置：任务栏显示控制 + 关闭按钮行为（启动初始化 + 设置卡变更跟随）
        var winState = AppServices.SettingsStore.Load();
        AppShell.ApplyWindowBehavior(winState.showInTaskbar, winState.closeToTray);
        AppShell.OnWindowBehaviorChanged += OnWindowBehaviorChangedForApp;

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
        // 关闭行为（v2.2.0 安装与运行配置）：closeToTray=true 最小化到托盘驻留；false 直接退出
        bool closeTipShown = false;
        Closing += (_, e) =>
        {
            if (!AppShell.IsQuitting && AppShell.CloseToTrayEnabled)
            {
                e.Cancel = true;
                Hide();
                // 首次关闭提示一次（新手常误以为应用「没关掉」——托盘语义要说破）
                if (!closeTipShown)
                {
                    closeTipShown = true;
                    AppShell.ShowBalloon("WinShare Panel", "已最小化到系统托盘，共享服务保持运行；右键托盘图标可完全退出。");
                }
            }
            // closeToTray=false → 不取消关闭：正常退出（托盘 Quit 清理由 OnExit 兜底）
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

    void OnModeChangedForBadge(bool advanced) => Dispatcher.BeginInvoke(() => UpdateModeBadge(advanced));

    /// <summary>窗口行为变更跟随（任务栏图标显隐即时生效）。</summary>
    void OnWindowBehaviorChangedForApp(bool showTaskbar, bool closeToTray)
        => Dispatcher.BeginInvoke(() => ShowInTaskbar = showTaskbar);

    /// <summary>顶栏模式徽标 + 「更多」菜单文案随模式翻转。</summary>
    void UpdateModeBadge(bool advanced)
    {
        SetStatusHint(advanced ? "专家模式" : "新手模式");
        TitleStatus.ToolTip = advanced
            ? "当前为专家模式：高级参数已展开（可在「应用设置」或本菜单切换）"
            : "当前为新手模式：只显示常用项，高级参数已收起（可在「应用设置」或本菜单切换）";
        // v2.2.0 第五批：模式以两枚可勾选项呈现当前值（替代"切换到…"把状态藏在动词里）
        if (MenuModeNovice is not null) MenuModeNovice.IsChecked = !advanced;
        if (MenuModeExpert is not null) MenuModeExpert.IsChecked = advanced;
    }

    /// <summary>「更多」菜单主题项勾选态跟随当前主题。</summary>
    void UpdateThemeMenuState()
    {
        bool dark = AppServices.SettingsStore.Load().theme == WinShare.Core.Contracts.ThemeMode.Dark;
        if (MenuThemeLight is not null) MenuThemeLight.IsChecked = !dark;
        if (MenuThemeDark is not null) MenuThemeDark.IsChecked = dark;
    }

    /// <summary>直接切到新手模式（可勾选项）。</summary>
    async void OnMenuModeNovice(object sender, RoutedEventArgs e) => await ApplyUsageMode(false);

    /// <summary>直接切到专家模式（可勾选项）。</summary>
    async void OnMenuModeExpert(object sender, RoutedEventArgs e) => await ApplyUsageMode(true);

    async Task ApplyUsageMode(bool advanced)
    {
        var vm = new Settings.UsageModeCardViewModel(AppServices.SettingsStore);
        string? err = await vm.ApplyModeAsync(advanced);
        if (err is not null)
        {
            Shell.ToastHost.Show(Shell.ToastLevel.Danger, err);
            return;
        }
        Shell.ToastHost.Show(Shell.ToastLevel.Success,
            advanced ? "已切换到专家模式：高级参数已展开" : "已切换到新手模式：高级参数已收起（各页重新进入后生效）");
        UpdateModeBadge(advanced);
    }

    void BuildNavDisplays()
    {
        NavList.ItemsSource = AppServices.Registry.Pages
            .Select(p => new NavDisplay(p.route, p.glyph, AppServices.I18n.T(p.titleKey)))
            .ToList();
    }

    /// <summary>
    /// 滚轮转发：仪表盘走渲染级滚动（其 ScrollViewer 已弃用），其余页面把滚轮
    /// 原样重抛给鼠标下方的第一个 ScrollViewer，走 WPF 原生滚轮处理。
    /// </summary>
    void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        for (var d = Mouse.DirectlyOver as System.Windows.DependencyObject;
             d is not null;
             d = d is System.Windows.Media.Visual v ? System.Windows.Media.VisualTreeHelper.GetParent(v) : null)
        {
            if (d is Views.DashboardPageView dash)
            {
                dash.ScrollBy(e.Delta);
                e.Handled = true;
                return;
            }
            if (d is System.Windows.Controls.ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                // 事件转发：把滚轮原样重抛到目标 ScrollViewer，走 WPF 原生滚轮处理
                // （程序化 ScrollToVerticalOffset/LineDown 在仪表盘的 viewer 上实测不落盘——
                //  ScrollContentPresenter 状态与属性值脱节，根因待查，见 2.0.2 遗留）
                var fwd = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = System.Windows.UIElement.MouseWheelEvent,
                    Source = e.OriginalSource,
                };
                sv.RaiseEvent(fwd);
                e.Handled = true;
                return;
            }
        }
    }

    void OnRouteChanged(string route)
    {
        PageHost.Content = _nav.CurrentView;
        OnWindowSizeChanged(this, null!); // 新页面按当前宽度重算断点
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

    /// <summary>
    /// v2.2.0 第四轮：窗口尺寸 → 共享页断点状态（窄窗隐藏次要列）。
    /// 宽度经侧栏 216 + 页面边距 40 折算为页面区可用宽；VM 属性驱动列 Visibility。
    /// </summary>
    void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // v2.2.0 第四轮：平板断点——窗口 <1280 侧栏收窄为图标栏（72px），标题隐藏
        // v2.2.0 第五批：阈值取 Breakpoints 单一来源（此前此处 1100、表格处 1280/1024 三套并存）
        var compact = WinShare.App.Shell.Breakpoints.IsCompactNav(ActualWidth);
        NavRail.Width = compact ? 72 : 216;
        // 标题显隐走 ItemContainerStyle 的 Tag 触发器（DataTemplate 内控件不在 code-behind 作用域）
        foreach (var item in NavList.Items)
        {
            if (NavList.ItemContainerGenerator.ContainerFromItem(item) is System.Windows.Controls.ListViewItem li)
                li.Tag = compact ? compact : null;
        }
        var pageWidth = ActualWidth - NavRail.Width - 40; // 侧栏 + 页面左右边距
        Shares.SharesServices.AvailableWidth = pageWidth;
        var vm = (PageHost.Content as Views.Shares.SharesPageView)?.Vm;
        vm?.RefreshBreakpoint();
    }

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
        // 点击标题栏上的交互控件（按钮/输入框等）不进入拖拽：
        // DragMove 是模态循环（按住直到抬起），会吞掉按钮的 Click（v2.3 实测：
        // 「更多」菜单点击无反应；焦点环出现=收到按下，Click 从未触发）
        if (e.Source is Button || e.Source is TextBox || e.Source is ComboBox
            || e.Source is CheckBox || e.Source is RadioButton || e.Source is ContextMenu
            || e.Source is ListView || e.Source is ListViewItem)
            return;
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

    /// <summary>标题栏状态胶囊 + 底部状态栏文案（如「已连接本机 SMB」/「专家模式」），空串则隐藏。</summary>
    public void SetStatusHint(string? text)
    {
        if (TitleStatus is null) return;
        TitleStatus.Text = text ?? "";
        if (FooterStatus is not null) FooterStatus.Text = string.IsNullOrWhiteSpace(text) ? "就绪" : text;
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

    void OnMenuThemeLight(object sender, RoutedEventArgs e)
    {
        AppServices.Theme.Apply(ThemeModeOf(false));
        UpdateThemeMenuState();
    }

    void OnMenuThemeDark(object sender, RoutedEventArgs e)
    {
        AppServices.Theme.Apply(ThemeModeOf(true));
        UpdateThemeMenuState();
    }

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

    // ===== 非管理员提示条（v2.2.0） =====

    /// <summary>提权重启（与「应用设置 → 关于」按钮共用 Elevation.RelaunchAndExitAsync）。</summary>
    async void OnRelaunchAsAdmin(object sender, RoutedEventArgs e)
    {
        string? err = await Shell.Elevation.RelaunchAndExitAsync();
        if (err is not null) Shell.ToastHost.Show(Shell.ToastLevel.Danger, err);
    }

    void OnDismissAdminBanner(object sender, RoutedEventArgs e)
        => AdminBanner.Visibility = Visibility.Collapsed;
}
