using System;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using WinShare.App.Common;
using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Shell;

/// <summary>
/// 管理员提权编排（v2.2.0 此前「以管理员身份重启」按钮必然失败——RelaunchAsAdminAction 未接线）：
/// - IsAdmin：纯 C# WindowsPrincipal（启动期横幅用，不走 PS，瞬时）
/// - RelaunchAndExitAsync：调 Core 提权动作（组合根注入 Process.Start -Verb RunAs）
///   → UAC 取消收敛为提示；成功后旧实例延迟退出（新实例带 --elevated-relaunch 会短窗重试互斥量，
///   见 SingleInstanceGuard）。横幅与应用设置页按钮共用本入口。
/// </summary>
public static class Elevation
{
    /// <summary>提权实例启动参数（App.OnStartup 据此给互斥量 acquisition 短窗重试）。</summary>
    public const string RelaunchArg = "--elevated-relaunch";

    /// <summary>UAC 取消（Win32 1223）经组合根转译的稳定错误码。</summary>
    public const string CancelledCode = "UAC_CANCELLED";

    /// <summary>当前进程是否管理员（任何异常收敛 false → 引导提权，绝不误判）。</summary>
    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 发起提权重启：成功则本实例自动退出（返回 null）；取消/失败返回人话错误文案（本实例存活）。
    /// </summary>
    public static async Task<string?> RelaunchAndExitAsync()
    {
        var sys = SettingsServiceSlots.SystemService;
        if (sys is null) return "系统服务未就绪，暂不能提权重启（详情见应用日志）";
        try
        {
            await sys.RelaunchAsAdminAsync();
        }
        catch (AppException e) when (e.Code == CancelledCode)
        {
            LogSink.Info("main", "UAC 提权被用户取消：应用保持运行");
            return null; // 取消是正常路径：不打扰（静默收敛）
        }
        catch (Exception e)
        {
            return ErrorText.Describe(e, "提权重启");
        }
        // UAC 已接受：新提权实例即将启动。稍候退出旧实例（短暂延迟让 UAC 对话框收场、反馈可见）
        LogSink.Info("main", "UAC 提权成功：退出当前实例，等待提权实例接管");
        ToastHost.Show(ToastLevel.Success, "已获得管理员权限，应用即将以管理员身份重启…");
        await Task.Delay(900);
        Application.Current.Shutdown();
        return null;
    }
}
