using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinShare.App.Shell;

/// <summary>
/// 自动化取证钩子：设置环境变量 WSHARE_NOACTIVATE=1 时，弹层窗口以非激活方式显示，
/// 供屏幕取证/回归脚本在不抢用户前台的前提下逐窗截图。
/// 未设置变量时零影响（正常使用的窗口行为不变）。
/// </summary>
public static class UiTestHooks
{
    public static bool NoActivate =>
        Environment.GetEnvironmentVariable("WSHARE_NOACTIVATE") == "1";

    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>在窗口构造器内调用；必须在 Show/ShowDialog 之前设置 ShowActivated。</summary>
    public static void Prepare(Window window)
    {
        if (!NoActivate) return;
        window.ShowActivated = false;
        // 实测（2026-11-20）：.NET 10 WPF 下仅 ShowActivated=false 主窗口仍会在启动 ~2s 后
        // 被前台化（.NET Framework 同代码不复现）。加 WS_EX_NOACTIVATE 兜底：窗口不响应
        // 任何自动激活（鼠标点击也不激活），程序化 UIA/PrintWindow 取证不受影响。
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE,
                GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
