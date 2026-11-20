using System.Drawing;
using System.Windows.Forms;
using WinShare.App.Events;

namespace WinShare.App.Shell;

/// <summary>
/// 壳生命周期编排（对齐 electron/main.ts 的 before-quit/window-all-closed 语义）：
/// 关闭窗口 = 托盘常驻不退出；「退出」= 置 IsQuitting 放行 close，托盘/管道/采样器清理。
/// </summary>
public static class AppShell
{
    public static bool IsQuitting { get; private set; }

    static TrayController? _tray;
    static SingleInstanceGuard? _guard;
    static Services.TrendSampler? _sampler;

    /// <summary>显示/聚焦主窗口（对齐 second-instance handler：restore → show → focus）。</summary>
    public static Action ShowMainWindow { get; set; } = () => { };

    /// <summary>告警 → 托盘气泡（对齐 TS alertRules 触发的气泡通知面）。</summary>
    public static Action<AlertRaised>? OnAlertRaised { get; set; }

    /// <summary>
    /// 使用模式变更（v2.2.0 第二轮）：true=专家 / false=新手。UsageModeCardViewModel 成功切换后触发；
    /// MainWindow 订阅更新顶栏徽标与「更多」菜单文案。
    /// </summary>
    public static Action<bool>? OnModeChanged { get; set; }

    // ===== v2.2.0 安装与运行配置：窗口行为（任务栏图标 / 关闭按钮行为） =====

    /// <summary>任务栏图标当前应显示（true）；窗口最小化到托盘时可按设置隐藏。</summary>
    public static bool TaskbarVisible { get; private set; } = true;

    /// <summary>关闭按钮行为（true=最小化到托盘驻留；false=直接退出）。</summary>
    public static bool CloseToTrayEnabled { get; private set; } = true;

    /// <summary>窗口行为变更（MainWindow 订阅：任务栏/关闭行为即时生效）。</summary>
    public static Action<bool, bool>? OnWindowBehaviorChanged { get; set; }

    /// <summary>应用窗口行为（启动时读 appstate 初始化；设置卡变更后调用）。</summary>
    public static void ApplyWindowBehavior(bool showTaskbar, bool closeToTray)
    {
        TaskbarVisible = showTaskbar;
        CloseToTrayEnabled = closeToTray;
        OnWindowBehaviorChanged?.Invoke(showTaskbar, closeToTray);
    }

    public static void CreateTray(Action openLogDir, Action quit, Icon? icon = null)
    {
        _tray = new TrayController(
            showWindow: ShowMainWindow,
            openLogDir: openLogDir,
            quit: quit,
            icon: icon,
            tooltip: "WinShare Panel");
    }

    /// <summary>二实例激活 → 首实例聚焦（由 SingleInstanceGuard 回调）。</summary>
    public static void ActivateMainWindow() => ShowMainWindow();

    public static SingleInstanceGuard CreateGuard(Action onActivate, TimeSpan? acquireWait = null)
    {
        _guard = new SingleInstanceGuard(onActivate, acquireWait);
        return _guard;
    }

    public static void AttachSampler(Services.TrendSampler sampler) => _sampler = sampler;

    /// <summary>托盘气泡（对齐 TS tray.displayBalloon；托盘未建时静默）。</summary>
    public static void ShowBalloon(string title, string body)
    {
        if (_tray is { } t) t.ShowBalloon(title, body);
    }

    /// <summary>
    /// 在资源管理器中打开应用日志目录。原 App/MainWindow/CommandPalette/AppSettings 四处
    /// 各持一份同构打开逻辑复制，现统一收口；目录不存在则先创建。
    /// v2.1.0 审计加固：Process.Start（含 ArgumentList 加固形态）被安全扫描判为命令注入高危，
    /// 改走 WinRT Launcher——由系统组件解析并打开路径，无进程参数注入面。
    /// LaunchFolderPathAsync 为 17134+ API（SupportedOSPlatformVersion=14393 → 版本守卫消平台警告）；
    /// 打开失败静默（对齐 TS catch 语义）。
    /// </summary>
    public static async void OpenLogDirectory()
    {
        try
        {
            var dir = LogSink.LogDir();
            System.IO.Directory.CreateDirectory(dir);
            if (!System.IO.Directory.Exists(dir)) return;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(dir);
            }
        }
        catch
        {
            // 打开失败静默（对齐 TS：catch 不提示）
        }
    }

    /// <summary>退出（对齐 main.ts before-quit：停采样、销毁托盘、关管道、放行 close）。</summary>
    public static void Quit()
    {
        IsQuitting = true;
        _sampler?.Stop();
        _tray?.Dispose();
        _tray = null;
        _guard?.Dispose();
        _guard = null;
    }
}
