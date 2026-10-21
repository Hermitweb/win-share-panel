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

    Task<SmbServerConfig> RestoreDefaultAsync();

    Task<IReadOnlyList<SmbSnapshotMeta>> ListSnapshotsAsync();

    Task RollbackSnapshotAsync(string id);

    /// <summary>TS: getServiceStatus()，服务名 LanmanServer/SMB 服务端。</summary>
    Task<ServiceStatus> GetServiceStatusAsync();

    Task RestartServiceAsync();
    Task StartServiceAsync();
    Task StopServiceAsync();
}
