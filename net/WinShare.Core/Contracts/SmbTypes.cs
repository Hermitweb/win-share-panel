namespace WinShare.Core.Contracts;

/// <summary>SMB 请求压缩（TS: SmbServerConfig.requestCompression）。</summary>
public enum RequestCompression { Off, Allow, Require }

/// <summary>SMB 服务器配置（TS: types.ts SmbServerConfig；setConfig 侧 TS 用 Partial，允许省略字段）。</summary>
public record SmbServerConfig
{
    public bool enableSMB1Protocol { get; init; }
    public bool enableSMB2Protocol { get; init; }
    public bool enableSMB3Protocol { get; init; }
    public bool enableGuestUserAccess { get; init; }
    public bool enableInsecureGuestLogons { get; init; }
    public bool auditSmb1Access { get; init; }
    public bool requireSecuritySignature { get; init; }
    public bool enableMultiChannel { get; init; }
    public bool announceServer { get; init; }
    public int unauthenticatedUsersTimeLimit { get; init; }
    // 扩展配置项
    public bool enableOplocks { get; init; }
    public bool enableOplockDirectoryCache { get; init; }
    public bool enableStrictNameChecking { get; init; }
    public bool enableLeasing { get; init; }
    public bool enableSMBQUIC { get; init; }
    public bool enableChannelChange { get; init; }
    public bool enableSMBDirectoryCache { get; init; }
    public int sessionTimeoutSeconds { get; init; }
    public int maxSessionPerConnection { get; init; }
    public int maxMpxCount { get; init; }
    public int maxWorkItems { get; init; }
    public int maxThreadsPerQueue { get; init; }
    public bool multipleSessionsPerConnection { get; init; }
    public RequestCompression requestCompression { get; init; }
    public bool silentAU { get; init; }
}

/// <summary>SMB 会话（TS: types.ts SmbSession）。</summary>
public record SmbSession
{
    public string clientId { get; init; }
    public string clientUserName { get; init; }
    public string clientComputerName { get; init; }
    public string sessionStartTime { get; init; }
    public int clientOpenFiles { get; init; }
    public int clientIdleTime { get; init; }
    public long bytesReceived { get; init; }
    public long bytesSent { get; init; }
}

/// <summary>SMB 打开文件（TS: types.ts SmbOpenFile）。</summary>
public record SmbOpenFile
{
    public string fileId { get; init; }
    public string path { get; init; }
    public string clientUserName { get; init; }
    public string clientComputerName { get; init; }
    public int lockCount { get; init; }
    public int relativeOpenTime { get; init; }
}

/// <summary>SMB 配置快照（TS: types.ts SmbSnapshot）。</summary>
public record SmbSnapshot
{
    public string id { get; init; }
    public string ts { get; init; }
    public SmbServerConfig config { get; init; }
}

/// <summary>SMB 配置快照元数据（TS: types.ts SmbSnapshotMeta）。</summary>
public record SmbSnapshotMeta
{
    public string id { get; init; }
    public string ts { get; init; }
}

/// <summary>某共享上的打开文件（TS: share.ts getShareOpenFiles 内联返回形状）。</summary>
public record ShareOpenFile
{
    public long fileId { get; init; }
    public string path { get; init; }
    public string clientUserName { get; init; }
    public string clientComputerName { get; init; }
    public int lockCount { get; init; }
}

/// <summary>某共享上的单个客户端连接（TS: share.ts getShareConnections 内联形状）。</summary>
public record ClientConnection
{
    public string clientUserName { get; init; }
    public string clientComputerName { get; init; }
    public int openFiles { get; init; }
}

/// <summary>共享连接统计（TS: share.ts getShareConnections 返回形状）。</summary>
public record ShareConnections
{
    public int concurrentUsers { get; init; }
    public IReadOnlyList<ClientConnection> clientConnections { get; init; }
}

/// <summary>关闭共享打开文件结果（TS: share.ts closeShareOpenFiles 返回形状）。</summary>
public record CloseOpenFilesResult
{
    public int closed { get; init; }
    public int failed { get; init; }
}
