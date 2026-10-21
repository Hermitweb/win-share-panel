namespace WinShare.Core.Contracts;

/// <summary>SMB 共享类型（TS: types.ts Share.type）。</summary>
public enum ShareType { Disk, IPC, Printer, Special }

/// <summary>共享状态（TS: types.ts Share.status）。</summary>
public enum ShareStatus { Enabled, Disabled }

/// <summary>
/// 共享（TS: types.ts Share，全协议共用；SMB 专有字段必填语义保留，NFS/FTP/WebDAV 专有字段可选）。
/// 对应 electron/services/share.ts listShares/getShare 等返回形状。
/// </summary>
public record Share
{
    public string name { get; init; }
    public string path { get; init; }
    public string description { get; init; }
    public Protocol protocol { get; init; }
    // SMB 专有
    public ShareType type { get; init; }
    public bool hidden { get; init; }
    public bool encrypted { get; init; }
    // 通用状态
    public int concurrentUsers { get; init; }
    public ShareStatus status { get; init; }
    public bool cached { get; init; }
    // NFS 专有（可选）
    public string? networkName { get; init; }
    public IReadOnlyList<NfsAuthMethod>? authentication { get; init; }
    public NfsPermission? nfsPermission { get; init; }
    public int? anonymousUid { get; init; }
    public int? anonymousGid { get; init; }
    public bool? enableUnmappedAccess { get; init; }
    public bool? allowRootAccess { get; init; }
    // FTP 专有（可选）
    public int? port { get; init; }
    public string? siteName { get; init; }
    public SslPolicy? sslPolicy { get; init; }
    public FtpAuthMode? authMode { get; init; }
    // WebDAV 专有（可选）
    public bool? anonymousEnabled { get; init; }
    public bool? authoringEnabled { get; init; }
}

/// <summary>SMB 创建共享入参（TS: types.ts CreateShareOpts）。</summary>
public record CreateShareOpts
{
    public string name { get; init; }
    public string path { get; init; }
    public string? description { get; init; }
    public IReadOnlyList<string>? fullAccess { get; init; }
    public IReadOnlyList<string>? changeAccess { get; init; }
    public IReadOnlyList<string>? readAccess { get; init; }
    public bool? encrypted { get; init; }
    public IReadOnlyList<string>? noAccess { get; init; }
    public int? concurrentUserLimit { get; init; }
    public bool? cached { get; init; }
    public bool? encryptData { get; init; }
    public string? securityDescriptor { get; init; }
    public FolderEnumerationMode? folderEnumerationMode { get; init; }
    public CachingMode? cachingMode { get; init; }
    public bool? shareShadowCopy { get; init; }
}

/// <summary>SMB 更新共享入参（TS: types.ts UpdateShareOpts）。</summary>
public record UpdateShareOpts
{
    public string? description { get; init; }
    public int? concurrentUserLimit { get; init; }
    public bool? cached { get; init; }
    public FolderEnumerationMode? folderEnumerationMode { get; init; }
    public CachingMode? cachingMode { get; init; }
    public bool? encryptData { get; init; }
    public bool? hidden { get; init; }
}

/// <summary>统一创建共享入参（TS: types.ts CreateShareInput，按 protocol 分支使用各协议字段）。</summary>
public record CreateShareInput
{
    public Protocol protocol { get; init; }
    public string name { get; init; }
    public string path { get; init; }
    public string? description { get; init; }
    // SMB
    public bool? encrypted { get; init; }
    public IReadOnlyList<string>? fullAccess { get; init; }
    public IReadOnlyList<string>? changeAccess { get; init; }
    public IReadOnlyList<string>? readAccess { get; init; }
    public IReadOnlyList<string>? noAccess { get; init; }
    public bool? encryptData { get; init; }
    public int? concurrentUserLimit { get; init; }
    public CachingMode? cachingMode { get; init; }
    public FolderEnumerationMode? folderEnumerationMode { get; init; }
    public bool? shareShadowCopy { get; init; }
    // NFS
    public IReadOnlyList<NfsAuthMethod>? authentication { get; init; }
    public NfsPermission? nfsPermission { get; init; }
    public bool? allowRootAccess { get; init; }
    public bool? enableUnmappedAccess { get; init; }
    public int? anonymousUid { get; init; }
    public int? anonymousGid { get; init; }
    // FTP
    public int? port { get; init; }
    public SslPolicy? sslPolicy { get; init; }
    public FtpAuthMode? authMode { get; init; }
    // WebDAV
    public bool? anonymousEnabled { get; init; }
}

/// <summary>统一更新共享入参（TS: types.ts UpdateShareInput）。</summary>
public record UpdateShareInput
{
    public Protocol protocol { get; init; }
    public string? description { get; init; }
    // NFS 可改
    public NfsPermission? nfsPermission { get; init; }
    public bool? allowRootAccess { get; init; }
    public bool? enableUnmappedAccess { get; init; }
    // FTP 可改
    public SslPolicy? sslPolicy { get; init; }
    public FtpAuthMode? authMode { get; init; }
    public string? physicalPath { get; init; }
    // WebDAV 可改
    public bool? anonymousEnabled { get; init; }
    public bool? authoringEnabled { get; init; }
}

/// <summary>
/// 共享可恢复快照（TS: types.ts ShareSnapshot；与 electron/services/share.ts 本地
/// DisabledRecord 同形 —— disabled.json 记录与操作日志撤销共用同一形状）。
/// </summary>
public record ShareSnapshot
{
    public string name { get; init; }
    public string path { get; init; }
    public string description { get; init; }
    public IReadOnlyList<SharePermission> permissions { get; init; }
    public bool encrypted { get; init; }
    public int? concurrentUserLimit { get; init; }
    public CachingMode? cachingMode { get; init; }
    public FolderEnumerationMode? folderEnumerationMode { get; init; }
}
