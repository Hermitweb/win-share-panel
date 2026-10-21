using System.IO;
using System.Windows;
using WinShare.App.Dashboard;
using WinShare.App.Events;
using WinShare.App.I18n;
using WinShare.App.Navigation;
using WinShare.App.Shell;
using WinShare.App.State;
using WinShare.App.Theming;

namespace WinShare.App;

/// <summary>
/// 组合根服务定位（WPF 单实例 Application 下比容器更轻）：
/// App.OnStartup 装配一次；各视图/ViewModel 从此取依赖。
/// T3b/c/d 服务实现落地后：在 App 组合处注册 ISystemService/IDiskService/...，
/// 本定位器对应槽位换装即全应用点亮（见 DOCS.md 组合根扩展点）。
/// </summary>
public static class AppServices
{
    public static IAppEventBus Bus { get; private set; } = null!;
    public static AppSettingsStore SettingsStore { get; private set; } = null!;
    public static NavigationRegistry Registry { get; private set; } = null!;
    public static NavigationService Navigation { get; private set; } = null!;
    public static ThemeManager Theme { get; private set; } = null!;
    public static I18nManager I18n { get; private set; } = null!;
    public static DashboardViewModel DashboardViewModel { get; private set; } = null!;
    public static Dashboard.HistoryRingBuffer? TrendBuffer { get; set; }

    /// <summary>
    /// 契约服务槽位（T3b/c/d 实现落地后注入）：非 null 时 App 自动启动趋势采样器；
    /// 组合根扩展点=改这里 + BuildDashboardFeed（见 App.xaml.cs）。
    /// </summary>
    public static WinShare.Core.Contracts.ISystemService? SystemService { get; set; }

    public static WinShare.Core.Contracts.ISystemService? ResolveSystemService() => SystemService;

    /// <summary>组合根装配（App.OnStartup 调一次）。</summary>
    public static void Compose(Application app, IDashboardDataFeed feed)
    {
        Bus = new AppEventBus();
        SettingsStore = new AppSettingsStore();
        Registry = new NavigationRegistry();
        Navigation = new NavigationService(Registry);
        Theme = new ThemeManager(app) { };
        Theme.BindStore(SettingsStore);
        I18n = new I18nManager(app);

        // 主题/语言恢复（appstate.json；损坏走默认）
        var initialTheme = SettingsStore.Load().theme;
        Theme.Apply(initialTheme, persist: false);
        I18n.SetCulture("zh"); // 默认中文（TS 版当前界面即中文；语言持久化留 T3d）

        // 仪表板 VM（fileSaver = SaveFileDialog；PNG 捕获 = 图表区 Register 后延迟渲染，见 Views 层）
        var pngCapture = new Views.Charts.ChartPngCapture();
        DashboardViewModel = new DashboardViewModel(
            feed,
            SettingsStore,
            Bus,
            SaveFileWithDialogAsync,
            pngCapture);

        // 事件接线：RefreshRequested → 仪表板刷新（F5 / 菜单 / 托盘）
        Bus.Subscribe<RefreshRequested>(evt => { _ = DashboardViewModel.RefreshAsync(); });
        // 告警触发事件 → 托盘气泡（对齐 TS alertRules 触发面；无气泡则静默）
        Bus.Subscribe<AlertRaised>(evt => AppShell.OnAlertRaised?.Invoke(evt));
    }

    /// <summary>导出文件保存（SaveFileDialog；界面层调，VM 不感知）。</summary>
    public static async Task SaveFileWithDialogAsync(string defaultName, byte[] bytes)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = defaultName,
            Filter = defaultName.EndsWith(".png") ? "PNG 图像|*.png" : "CSV 文件|*.csv",
        };
        if (dlg.ShowDialog() == true)
        {
            await File.WriteAllBytesAsync(dlg.FileName, bytes);
        }
    }
}
