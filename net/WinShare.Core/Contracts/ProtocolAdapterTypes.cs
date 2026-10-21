namespace WinShare.Core.Contracts;

/// <summary>通用协议会话（TS: types.ts ProtocolSession；FTP/WebDAV 无原生会话，adapter 抛 unsupported）。</summary>
public record ProtocolSession
{
    public Protocol protocol { get; init; }
    public string sessionId { get; init; }
    public string clientUserName { get; init; }
    public string clientComputerName { get; init; }
    public string sessionStartTime { get; init; }
    public int clientOpenFiles { get; init; }
    public int clientIdleTime { get; init; }
    public long bytesReceived { get; init; }
    public long bytesSent { get; init; }
}

/// <summary>协议安装类型（TS: ProtocolFeatureState.installType）。</summary>
public enum ProtocolInstallType { ServerFeature, ClientOnly, IisRole, Builtin }

/// <summary>协议功能检测项（TS: types.ts ProtocolFeatureState）。</summary>
public record ProtocolFeatureState
{
    public Protocol protocol { get; init; }
    public bool installed { get; init; }
    public ProtocolInstallType installType { get; init; }
    public string serviceName { get; init; }
    public ServiceState serviceStatus { get; init; }
    public string installCommand { get; init; }
    public string installHint { get; init; }
}

/// <summary>权限模型（TS: ProtocolCapabilities.permissionModel）。</summary>
public enum PermissionModel { SmbAcl, NfsKrb, IisAuth, WebdavRules }

/// <summary>协议能力位（TS: types.ts ProtocolCapabilities，adapter 声明支持哪些操作，UI 据此降级）。</summary>
public record ProtocolCapabilities
{
    public bool supportsCreate { get; init; }
    public bool supportsUpdate { get; init; }
    public bool supportsDelete { get; init; }
    public bool supportsToggle { get; init; }
    public bool supportsPermissions { get; init; }
    public bool supportsSessions { get; init; }
    public bool supportsOpenFiles { get; init; }
    public bool supportsServerConfig { get; init; }
    public bool supportsRestart { get; init; }
    public PermissionModel permissionModel { get; init; }
}
