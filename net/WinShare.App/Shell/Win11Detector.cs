using System.Runtime.InteropServices;

namespace WinShare.App.Shell;

/// <summary>
/// Win11 检测（对齐 electron/main.ts isWin11：版本三元组第三段 build ≥ 22000）。
/// WPF 侧读 .NET 报告的 Windows 版本（net10 上报真实 build，如 10.0.26100）；
/// 22000 即 Win11 22H2 基线。
/// 用途对齐 main.ts：Win11 下启用亚克力材质（WPF 侧 = DWM 系统背景 DWMSBT_MAINWINDOW + 圆角）。
/// </summary>
public static class Win11Detector
{
    public const int Win11MinBuild = 22000;

    public static bool IsWin11(Version? osVersion = null)
    {
        var v = osVersion ?? Environment.OSVersion.Version;
        return v.Build >= Win11MinBuild;
    }
}

/// <summary>
/// DWM 窗口外观（Win11 亚克力背景对齐 main.ts setBackgroundMaterial('acrylic')）：
/// DWMWA_SYSTEMBACKDROP_TYPE=36 → DWMSBT_MAINWINDOW(4)；DWMWA_BORDER_RADIUS=33 → 1。
/// 仅 Win11 调用；失败静默（回退系统默认外观）。
/// </summary>
public static class DwmBackdrop
{
    const int DWMWA_BORDER_RADIUS = 33;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 36;
    const int DWMSBT_MAINWINDOW = 4;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static bool TryApply(IntPtr hwnd)
    {
        try
        {
            var radius = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_RADIUS, ref radius, sizeof(int));
            var backdrop = DWMSBT_MAINWINDOW;
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
        }
        catch
        {
            return false; // 旧系统/权限不足：忽略，保持系统默认
        }
    }
}
