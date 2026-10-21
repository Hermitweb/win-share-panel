namespace WinShare.Core.Contracts;

/// <summary>
/// WebDAV（IIS）服务器配置与服务控制（对应 electron/services/webdav.ts 导出：
/// getServiceStatus / restartService / startService / stopService /
/// defaultConfig / ensureWebdavSectionsUnlocked / getConfig / setConfig / restoreDefault）。
/// </summary>
public interface IWebdavService
{
    Task<ServiceStatus> GetServiceStatusAsync();

    Task RestartServiceAsync();
    Task StartServiceAsync();
    Task StopServiceAsync();

    /// <summary>TS: defaultConfig()（同步）。</summary>
    WebdavServerConfig DefaultConfig();

    /// <summary>TS: ensureWebdavSectionsUnlocked()（appcmd 解锁 system.webServer/* 配置节）。</summary>
    Task EnsureWebdavSectionsUnlockedAsync();

    Task<WebdavServerConfig> GetConfigAsync();

    Task SetConfigAsync(WebdavServerConfig config);

    Task<WebdavServerConfig> RestoreDefaultAsync();
}
