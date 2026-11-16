namespace WinShare.Core.Contracts;

/// <summary>
/// NFS 服务器配置（TS: types.ts NfsServerConfig，Get-NfsServerConfiguration 返回字段子集；
/// 默认值语义见 electron/services/nfs.ts defaultConfig）。
/// 身份映射/网关三字段经 MSFT_NfsServerConfig WMI 直写（Set-NfsServerConfiguration 不接受这些参数），
/// 写入与现值相同则跳过，提供程序拒绝时给出明确错误。
/// </summary>
public record NfsServerConfig
{
    public bool gracefulUnmount { get; init; }
    public bool logActivity { get; init; }
    public bool enableUnmappedAccess { get; init; }
    public bool enableAuthenticationRenegotiation { get; init; }
    /// <summary>网关字符集（默认 ANSI；WMI 直写）。</summary>
    public string gatewayCharacterSet { get; init; }
    /// <summary>协议版本（服务器报告值，仅展示；版本启停用 enableNfsV2/3/4）。</summary>
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
    /// <summary>默认 -2（WMI 直写）。</summary>
    public int anonymousUid { get; init; }
    /// <summary>默认 -2（WMI 直写）。</summary>
    public int anonymousGid { get; init; }
    /// <summary>启用 NFSv2（Set-NfsServerConfiguration -EnableNFSV2；探测失败回落 false）。</summary>
    public bool enableNfsV2 { get; init; }
    /// <summary>启用 NFSv3（Set-NfsServerConfiguration -EnableNFSV3；探测失败回落 true）。</summary>
    public bool enableNfsV3 { get; init; }
    /// <summary>启用 NFSv4.1（Set-NfsServerConfiguration -EnableNFSV4；探测失败回落 false）。</summary>
    public bool enableNfsV4 { get; init; }
}
