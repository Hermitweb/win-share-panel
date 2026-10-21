using System.Drawing;
using System.IO;
using System.Windows.Forms;
using WinShare.Core.Contracts;

namespace WinShare.App.Shell;

/// <summary>
/// 系统托盘（NotifyIcon + 右键 ContextMenuStrip；对齐 electron/main.ts createTray）：
/// 菜单=显示主窗口 / 分隔 / 打开日志文件夹 / 分隔 / 退出；ToolTip "WinShare Panel"；
/// 单击=显示主窗口。图标缺资源时回退 SystemIcons.Application（不崩）。
/// </summary>
public sealed class TrayController : IDisposable
{
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu;
    readonly Action _showWindow;
    readonly Action _openLogDir;
    readonly Action _quit;

    public TrayController(Action showWindow, Action openLogDir, Action quit,
        Icon? icon = null, string tooltip = "WinShare Panel")
    {
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _openLogDir = openLogDir ?? throw new ArgumentNullException(nameof(openLogDir));
        _quit = quit ?? throw new ArgumentNullException(nameof(quit));

        _menu = new ContextMenuStrip();
        _menu.Items.Add("显示主窗口", null, (_, _) => _showWindow());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("打开日志文件夹", null, (_, _) => _openLogDir());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => _quit());

        _icon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip, // Win32 tooltip 63 字符上限
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => _showWindow();
    }

    /// <summary>对齐 main.ts window:balloon：气泡通知（displayBalloon 降级语义由 WPF 窗口闪烁承接）。</summary>
    public void ShowBalloon(string title, string body)
    {
        try
        {
            _icon.ShowBalloonTip(3000, title, body, ToolTipIcon.Info);
        }
        catch
        {
            // 气泡不可用时忽略（与 main.ts catch 降级一致）
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
