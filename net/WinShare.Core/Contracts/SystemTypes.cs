namespace WinShare.Core.Contracts;

/// <summary>服务状态（TS: types.ts ServiceStatus.status）。</summary>
public enum ServiceState { Running, Stopped, Unknown }

/// <summary>服务状态（TS: types.ts ServiceStatus）。</summary>
public record ServiceStatus
{
    public string name { get; init; }
    public ServiceState status { get; init; }
    public string startType { get; init; }
}

/// <summary>当前用户信息（TS: types.ts UserInfo；computerName 用于拼 UNC 路径）。</summary>
public record UserInfo
{
    public string username { get; init; }
    public bool isAdmin { get; init; }
    public string computerName { get; init; }
}

/// <summary>OS 功能探测位（TS: types.ts OsFeatures）。</summary>
public record OsFeatures
{
    public bool smbShareModule { get; init; }
    public bool nfsServerCmdlets { get; init; }
    public bool iisAvailable { get; init; }
    public bool smbQuicConfig { get; init; }
}

/// <summary>OS 信息（TS: types.ts OsInfo）。</summary>
public record OsInfo
{
    public string caption { get; init; }
    public int buildNumber { get; init; }
    public int skuId { get; init; }
    public string skuName { get; init; }
    public bool isServer { get; init; }
    public bool isHomeEdition { get; init; }
    public string hostname { get; init; }
    public OsFeatures features { get; init; }
}

/// <summary>getOsInfo 入参（TS: system.ts getOsInfo(opts: { refresh?: boolean })）。</summary>
public record OsInfoOptions
{
    public bool? refresh { get; init; }
}

/// <summary>仪表板统计（TS: types.ts DashboardStats）。</summary>
public record DashboardStats
{
    public int shareCount { get; init; }
    public int activeSessions { get; init; }
    public int openFiles { get; init; }
    public ServiceState serviceStatus { get; init; }
    public IReadOnlyList<TopShare> topShares { get; init; }
    /// <summary>TS: byProtocol: Record&lt;Protocol, { shares, sessions }&gt;。</summary>
    public Dictionary<Protocol, ProtocolStats> byProtocol { get; init; }
}

/// <summary>仪表板 Top 共享项（TS: DashboardStats.topShares 元素）。</summary>
public record TopShare
{
    public string name { get; init; }
    public int connections { get; init; }
    public Protocol protocol { get; init; }
}

/// <summary>按协议统计（TS: DashboardStats.byProtocol 值形状）。</summary>
public record ProtocolStats
{
    public int shares { get; init; }
    public int sessions { get; init; }
}

/// <summary>健康检查结果（TS: system.ts healthCheck 返回 { ok, detail }）。</summary>
public record HealthCheckResult
{
    public bool ok { get; init; }
    public string detail { get; init; }
}

/// <summary>应用与运行时版本（TS: types.ts AppInfo；「应用设置 · 关于」卡使用）。</summary>
public record AppInfo
{
    public string version { get; init; }
    public string electron { get; init; }
    public string chrome { get; init; }
    public string node { get; init; }
    public string platform { get; init; }
    public string arch { get; init; }
}
