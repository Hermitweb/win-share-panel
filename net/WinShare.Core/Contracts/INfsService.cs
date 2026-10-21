namespace WinShare.Core.Contracts;

/// <summary>
/// NFS 服务器配置与服务控制（对应 electron/services/nfs.ts 导出：
/// defaultConfig / getConfig / setConfig / restoreDefault /
/// getServiceStatus / restartService / startService / stopService）。
/// </summary>
public interface INfsService
{
    /// <summary>TS: defaultConfig()（同步）。</summary>
    NfsServerConfig DefaultConfig();

    Task<NfsServerConfig> GetConfigAsync();

    Task SetConfigAsync(NfsServerConfig config);

    Task<NfsServerConfig> RestoreDefaultAsync();

    Task<ServiceStatus> GetServiceStatusAsync();

    Task RestartServiceAsync();
    Task StartServiceAsync();
    Task StopServiceAsync();
}
