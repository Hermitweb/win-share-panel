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

    /// <summary>v2.3：本机 Set-NfsServerConfiguration 支持的参数名集（null=探测失败，按全部支持处理）。</summary>
    Task<IReadOnlySet<string>?> GetSettableFieldsAsync();

    Task<NfsServerConfig> RestoreDefaultAsync();

    Task<ServiceStatus> GetServiceStatusAsync();

    Task RestartServiceAsync();
    Task StartServiceAsync();
    Task StopServiceAsync();

    /// <summary>修改服务启动类型（Set-Service -StartupType，白名单 Automatic/Manual/Disabled；写后回读校验）。</summary>
    Task SetServiceStartTypeAsync(string startType);
}
