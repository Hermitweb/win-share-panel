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
        NavList.SelectedIndex = idx;
    }

    void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is NavDisplay nd)
        {
            _nav.Navigate(nd.Route);
            PageHost.Content = _nav.CurrentView;
        }
    }

    // ===== 菜单（对齐 main.ts 主菜单 + 托盘菜单面） =====
    void OnMenuShowWindow(object sender, RoutedEventArgs e) => AppShell.ShowMainWindow();

    void OnMenuOpenLogDir(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogSink.LogDir());
            Process.Start(new ProcessStartInfo(LogSink.LogDir()) { UseShellExecute = true });
        }
        catch { }
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
