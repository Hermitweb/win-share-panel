namespace WinShare.Core.Contracts;

/// <summary>
/// 应用 PATH 注册与登录快捷方式（对应 electron/services/pathRegistration.ts 导出：
/// getUserPath / setUserPath / broadcastPathChange / registerAppInUserPath /
/// unregisterAppFromUserPath / isAppInUserPath / setLoginShortcut /
/// getLoginShortcut / readServicePendingMarker。
/// 本地 PathRegistration 记录见 PathRegistrationTypes.cs。
/// </summary>
public interface IPathRegistrationService
{
    /// <summary>TS: getUserPath()（HKCU\Environment\Path 读取；缺失返回空串）。</summary>
    Task<string> GetUserPathAsync();

    Task SetUserPathAsync(string value);

    /// <summary>TS: broadcastPathChange()，是否成功广播 WM_SETTINGCHANGE。</summary>
    Task<bool> BroadcastPathChangeAsync();

    Task<PathRegistration> RegisterAppInUserPathAsync();

    Task<PathRegistration> UnregisterAppFromUserPathAsync();

    Task<bool> IsAppInUserPathAsync();

    /// <summary>TS: setLoginShortcut(enabled)，返回说明文本。</summary>
    Task<string> SetLoginShortcutAsync(bool enabled);

    Task<bool> GetLoginShortcutAsync();

    /// <summary>TS: readServicePendingMarker()（服务安装挂起标记）。</summary>
    Task<bool> ReadServicePendingMarkerAsync();
}
