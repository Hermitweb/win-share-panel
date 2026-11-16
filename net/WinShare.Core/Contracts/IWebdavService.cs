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

    /// <summary>修改服务启动类型（Set-Service -StartupType，白名单 Automatic/Manual/Disabled；写后回读校验）。</summary>
    Task SetServiceStartTypeAsync(string startType);

    /// <summary>TS: defaultConfig()（同步）。</summary>
    WebdavServerConfig DefaultConfig();

    /// <summary>TS: ensureWebdavSectionsUnlocked()（appcmd 解锁 system.webServer/* 配置节）。</summary>
    Task EnsureWebdavSectionsUnlockedAsync();

    Task<WebdavServerConfig> GetConfigAsync();

    Task SetConfigAsync(WebdavServerConfig config);

    Task<WebdavServerConfig> RestoreDefaultAsync();

    /// <summary>服务器级 Authoring 规则列表（authoringRules 集合；面板可查看/增删）。</summary>
    Task<IReadOnlyList<WebdavAuthoringRule>> ListAuthoringRulesAsync();

    /// <summary>新增 Authoring 规则（Add-WebConfigurationProperty；写前解锁配置节）。</summary>
    Task AddAuthoringRuleAsync(WebdavAuthoringRuleOpts rule);

    /// <summary>删除 Authoring 规则（按 extension+path 定位；Remove-WebConfigurationProperty -AtElement）。</summary>
    Task RemoveAuthoringRuleAsync(string extension, string path);
}
