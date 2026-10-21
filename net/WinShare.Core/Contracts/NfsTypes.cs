namespace WinShare.Core.Contracts;

/// <summary>
/// NFS 服务器配置（TS: types.ts NfsServerConfig，Get-NfsServerConfiguration 返回字段子集；
/// 只读与默认值语义见 electron/services/nfs.ts defaultConfig）。
/// </summary>
public record NfsServerConfig
{
    public bool gracefulUnmount { get; init; }
    public bool logActivity { get; init; }
    public bool enableUnmappedAccess { get; init; }
    public bool enableAuthenticationRenegotiation { get; init; }
    /// <summary>只读。</summary>
    public string gatewayCharacterSet { get; init; }
    /// <summary>只读。</summary>
    public string protocolVersion { get; init; }
    /// <summary>秒，默认 240。</summary>
    public int tcpConnectionTimeout { get; init; }
    /// <summary>秒，默认 240。</summary>
    public int udpConnectionTimeout { get; init; }
    /// <summary>秒，默认 60。</summary>
    public int restartConnectionTimeout { get; init; }
    /// <summary>默认 0（无限制）。</summary>
    public int maxConcurrentConnectionsPerUser { get; init; }
    /// <summary>秒，默认 60（仅 Server 2019+）。</summary>
    public int directoryCacheExpiry { get; init; }
    /// <summary>默认 -2。</summary>
    public int anonymousUid { get; init; }
    /// <summary>默认 -2。</summary>
    public int anonymousGid { get; init; }
}
