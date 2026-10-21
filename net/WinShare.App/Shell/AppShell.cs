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

    public static SingleInstanceGuard CreateGuard(Action onActivate)
    {
        _guard = new SingleInstanceGuard(onActivate);
        return _guard;
    }

    public static void AttachSampler(Services.TrendSampler sampler) => _sampler = sampler;

    /// <summary>托盘气泡（对齐 TS tray.displayBalloon；托盘未建时静默）。</summary>
    public static void ShowBalloon(string title, string body)
    {
        if (_tray is { } t) t.ShowBalloon(title, body);
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
