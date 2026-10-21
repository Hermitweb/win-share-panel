namespace WinShare.Core.Contracts;

/// <summary>
/// FTP（IIS）服务器配置与服务控制（对应 electron/services/ftp.ts 导出：
/// getServiceStatus / restartService / startService / stopService /
/// defaultConfig / ensureFtpSectionsUnlocked / getConfig / setConfig / restoreDefault）。
/// </summary>
public interface IFtpService
{
    Task<ServiceStatus> GetServiceStatusAsync();

    Task RestartServiceAsync();
    Task StartServiceAsync();
    Task StopServiceAsync();

    /// <summary>TS: defaultConfig()（同步）。</summary>
    FtpServerConfig DefaultConfig();

    /// <summary>TS: ensureFtpSectionsUnlocked()（appcmd 解锁 ftpServer/* 配置节）。</summary>
    Task EnsureFtpSectionsUnlockedAsync();

    Task<FtpServerConfig> GetConfigAsync();

    Task SetConfigAsync(FtpServerConfig config);

    Task<FtpServerConfig> RestoreDefaultAsync();
}
