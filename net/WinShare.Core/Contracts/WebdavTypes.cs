namespace WinShare.Core.Contracts;

/// <summary>
/// WebDAV 服务器配置（TS: types.ts WebdavServerConfig，IIS system.webServer/* 配置节；
/// 默认值语义见 electron/services/webdav.ts defaultConfig）。
/// </summary>
public record WebdavServerConfig
{
    public bool authoringEnabled { get; init; }
    /// <summary>字节数，0 表示不限制。</summary>
    public long authoringMaxRequestBodySize { get; init; }
    /// <summary>字节数，默认 30000000。</summary>
    public long maxAllowedContentLength { get; init; }
    public bool allowDoubleEscaping { get; init; }
    public bool verifyIntegration { get; init; }
    public bool anonymousEnabled { get; init; }
    public bool basicEnabled { get; init; }
    public bool windowsEnabled { get; init; }
    /// <summary>默认 260。</summary>
    public int maxUrlLength { get; init; }
    /// <summary>默认 2048。</summary>
    public int maxQueryStringLength { get; init; }
    public int globalAuthoringRulesCount { get; init; }
    public bool enableStaticCompression { get; init; }
    public bool enableDynamicCompression { get; init; }
    public bool requireSSL { get; init; }
}
