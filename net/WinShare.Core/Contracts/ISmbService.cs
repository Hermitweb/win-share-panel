namespace WinShare.Core.Contracts;

/// <summary>
/// SMB 服务器配置与服务控制（对应 electron/services/smb.ts 导出：
/// defaultConfig / getConfig / setConfig / restoreDefault / listSnapshots /
/// rollbackSnapshot / getServiceStatus / restartService / startService / stopService）。
/// TS setConfig 入参 Partial&lt;SmbServerConfig&gt;（可省略字段），C# 侧传完整 SmbServerConfig 或按需实现 patch 语义。
/// </summary>
public interface ISmbService
{
    /// <summary>TS: defaultConfig()（同步）。</summary>
    SmbServerConfig DefaultConfig();

    Task<SmbServerConfig> GetConfigAsync();

    Task SetConfigAsync(SmbServerConfig config);

    /// <summary>
    /// v2.3：本机 Set-SmbServerConfiguration 实际支持的参数名集（null=探测失败，按全部支持处理）。
    /// UI 据此禁用不支持的字段（Win11 26200 已无 EnableSMB3Protocol 写参数）。
    /// </summary>
    Task<IReadOnlySet<string>?> GetSettableFieldsAsync();

    Task<SmbServerConfig> RestoreDefaultAsync();

    Task<IReadOnlyList<SmbSnapshotMeta>> ListSnapshotsAsync();

    Task RollbackSnapshotAsync(string id);

    /// <summary>TS: getServiceStatus()，服务名 LanmanServer/SMB 服务端。</summary>
    Task<ServiceStatus> GetServiceStatusAsync();

    Task RestartServiceAsync();
    Task StartServiceAsync();
    Task StopServiceAsync();

    /// <summary>修改服务启动类型（Set-Service -StartupType，白名单 Automatic/Manual/Disabled；写后回读校验）。</summary>
    Task SetServiceStartTypeAsync(string startType);
}
