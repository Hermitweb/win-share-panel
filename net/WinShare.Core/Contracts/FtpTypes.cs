namespace WinShare.Core.Contracts;

/// <summary>FTP 用户隔离模式（TS: FtpServerConfig.userIsolationMode）。</summary>
public enum FtpUserIsolationMode { None, StartInUsersDirectory, IsolateUsers, IsolateUsersWithoutAD, ActiveDirectory }

/// <summary>FTP 日志周期（TS: FtpServerConfig.logFilePeriod）。</summary>
public enum FtpLogPeriod { Hourly, Daily, Weekly, Monthly, MaxSize, Never }

/// <summary>
/// FTP 服务器配置（TS: types.ts FtpServerConfig，IIS ftpServer/* 配置节，
/// 服务器级 MACHINE/WEBROOT/APPHOST；默认值语义见 electron/services/ftp.ts defaultConfig）。
/// </summary>
public record FtpServerConfig
{
    public SslPolicy sslControlChannelPolicy { get; init; }
    public SslPolicy sslDataChannelPolicy { get; init; }
    /// <summary>SHA-1 thumbprint，空字符串表示未配置。</summary>
    public string sslServerCertHash { get; init; }
    public bool sslClientCertRequired { get; init; }
    public bool ssl128 { get; init; }
    public bool anonymousEnabled { get; init; }
    /// <summary>默认 IUSR。</summary>
    public string anonymousUserName { get; init; }
    public bool basicEnabled { get; init; }
    /// <summary>0 表示未配置。</summary>
    public int firewallLowDataChannelPort { get; init; }
    /// <summary>0 表示未配置。</summary>
    public int firewallHighDataChannelPort { get; init; }
    public string greetingMessage { get; init; }
    public string bannerMessage { get; init; }
    public string exitMessage { get; init; }
    public string maxClientsMessage { get; init; }
    public bool suppressDefaultMessages { get; init; }
    public bool showVirtualDirs { get; init; }
    public FtpUserIsolationMode userIsolationMode { get; init; }
    public int unauthenticatedTimeout { get; init; }
    /// <summary>默认 300。</summary>
    public int controlConnectionTimeout { get; init; }
    /// <summary>默认 30。</summary>
    public int dataChannelConnectionTimeout { get; init; }
    public bool keepPartialUploads { get; init; }
    public bool allowReplaceOnRename { get; init; }
    public string logFileDirectory { get; init; }
    public FtpLogPeriod logFilePeriod { get; init; }
}
