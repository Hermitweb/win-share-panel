using System.Windows;
using WinShare.App.Dashboard;
using WinShare.App.Events;
using WinShare.App.Navigation;
using WinShare.App.Services;
using WinShare.App.Shell;
using WinShare.App.Views;

namespace WinShare.App;

/// <summary>
/// 组合根 + 启动编排（对齐 electron/main.ts 启动面）：
/// - 单实例锁（二实例 → 激活首实例并自退）
/// - 托盘（关闭=最小化到托盘；退出=清理后放行）
/// - Win11 检测 → DWM 亚克力背景
/// - 24h 趋势采样器（服务就绪时启动；未就绪留扩展点）
/// - crashReporter 替代（本地落盘）
/// </summary>
public partial class App : Application
{
    MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var crash = new CrashReporter(msg =>
        {
            // UI 线程崩溃已被 handler 吞掉并落日志；托盘气泡提示
            AppShell.OnAlertRaised?.Invoke(new AlertRaised { kind = "crash", level = "danger", text = "程序发生未处理异常（已记录日志）" });
        });
        _ = crash;

        // 单实例：二实例 → 发激活消息并自退（对齐 requestSingleInstanceLock）
        try
        {
            AppShell.CreateGuard(AppShell.ActivateMainWindow);
        }
        catch (NotFirstInstanceException)
        {
            if (SingleInstanceGuard.TryActivateExistingInstance())
            {
                Shutdown();
                return;
            }
            Shutdown();
            return;
        }

        // 组合根装配（仪表板数据馈送：契约服务槽位为空时走契约空馈送 → 界面全空态）
        AppServices.Compose(this, feed: BuildDashboardFeed());

        // 导航注册：仪表板（本任务模块）+ 5 占位（TODO T3b/c/d）
        RegisterPages();

        // 主窗口
        _window = new MainWindow(AppServices.Navigation);
        ShowMainWindow();

        // 托盘（对齐 createTray：显示主窗口 / 打开日志文件夹 / 退出）
        AppShell.ShowMainWindow = ShowMainWindow;
        AppShell.CreateTray(OpenLogDir, QuitApp);
        // 告警触发事件 → 托盘气泡（对齐 TS alertRules 告警通知面）
        AppShell.OnAlertRaised = evt => AppShell.ShowBalloon("WinShare 告警", evt.text);

        // 24h 趋势采样器（对齐 startHistorySampler；需要 ISystemService 实现）
        StartTrendSampler();

        LogSink.Info("main", $"[startup] 进程位宽 {(Environment.Is64BitProcess ? "64" : "32")}（Windows）→ WPF net10");
    }

    static IDashboardDataFeed BuildDashboardFeed()
    {
        // T3b/c/d 服务实现注册后：换成 new ContractServicesDataFeed(sys, disk, smb, sec)。
        // 当前契约服务无实现 → 契约空馈送（界面如实空态，不崩）。
        return new ContractServicesDataFeed(null, null, null, null);
    }

    void RegisterPages()
    {
        var reg = AppServices.Registry;
        reg.Register(new AppPage { route = "/", titleKey = "Dash.Title", glyph = "◧", factory = new PageFactory(() => new DashboardPageView()) });
        reg.Register(new AppPage { route = "/shares", titleKey = "Nav.Share", glyph = "▤", factory = new PageFactory(() => new PlaceholderPage("/shares", AppServices.I18n.T("Nav.Share"))) });
        reg.Register(new AppPage { route = "/users", titleKey = "Nav.Users", glyph = "◉", factory = new PageFactory(() => new PlaceholderPage("/users", AppServices.I18n.T("Nav.Users"))) });
        reg.Register(new AppPage { route = "/sessions", titleKey = "Nav.Sessions", glyph = "≋", factory = new PageFactory(() => new PlaceholderPage("/sessions", AppServices.I18n.T("Nav.Sessions"))) });
        reg.Register(new AppPage { route = "/settings", titleKey = "Nav.Settings", glyph = "⚙", factory = new PageFactory(() => new PlaceholderPage("/settings", AppServices.I18n.T("Nav.Settings"))) });
        reg.Register(new AppPage { route = "/app-settings", titleKey = "Nav.AppSettings", glyph = "◎", factory = new PageFactory(() => new PlaceholderPage("/app-settings", AppServices.I18n.T("Nav.AppSettings"))) });
    }

    void ShowMainWindow()
    {
        if (_window is null) return;
        if (_window.IsLoaded)
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            if (!_window.IsVisible) _window.Show();
            _window.Activate();
            _window.Focus();
        }
        else
        {
            _window.Show();
            _window.Activate();
        }
    }

    void OpenLogDir()
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogSink.LogDir());
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogSink.LogDir()) { UseShellExecute = true });
        }
        catch { }
    }

    void QuitApp()
    {
        AppShell.Quit();
        Shutdown();
    }

    void StartTrendSampler()
    {
        var svc = AppServices.ResolveSystemService();
        if (svc is null)
        {
            LogSink.Info("main", "[historySampler] ISystemService 未注册，采样器留待 T3b/c/d 接服务实现后启动");
            return;
        }
        var source = new SystemServiceTrendSource(svc);
        var buffer = new HistoryRingBuffer(HistoryRingBuffer.DefaultCapacity);
        AppServices.TrendBuffer = buffer;
        var sink = new HistoryPointSink
        {
            Buffer = buffer,
            Persist = p => AppServices.SettingsStore.PushHistory(p),
            Reported = p => AppServices.Bus.Publish(new TrendSampled(p)),
        };
        var sampler = new TrendSampler(source, sink);
        AppShell.AttachSampler(sampler);
        _ = sampler.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppShell.Quit(); // 幂等：托盘/采样器/管道清理（对齐 before-quit/will-quit）
        base.OnExit(e);
    }
}

/// <summary>页面工厂（IPageFactory 轻量实现）。</summary>
public sealed class PageFactory(Func<System.Windows.Controls.UserControl> creator) : IPageFactory
{
    public System.Windows.Controls.UserControl Create() => creator();
}
