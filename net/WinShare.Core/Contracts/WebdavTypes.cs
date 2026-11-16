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
    /// <summary>静态压缩（urlCompression/doStaticCompression，可写）。</summary>
    public bool enableStaticCompression { get; init; }
    /// <summary>动态压缩（urlCompression/doDynamicCompression，可写）。</summary>
    public bool enableDynamicCompression { get; init; }
    /// <summary>要求 SSL（security/access sslFlags，可写：Ssl/None）。</summary>
    public bool requireSSL { get; init; }
}

/// <summary>
/// 服务器级 WebDAV Authoring 规则（IIS authoringRules 集合项；面板可增删，规则数随实际条目回读）。
/// </summary>
public record WebdavAuthoringRule
{
    /// <summary>文件扩展名（"*" 或 ".txt" 等）。</summary>
    public string extension { get; init; }
    /// <summary>路径（"*" 或子路径）。</summary>
    public string path { get; init; }
    /// <summary>访问权限（"Read"/"Write"/"Read,Write"/"Read,Write,Source"）。</summary>
    public string access { get; init; }
    /// <summary>允许的用户（"*" 或账号列表）。</summary>
    public string users { get; init; }
    /// <summary>允许的组（空串或组列表）。</summary>
    public string roles { get; init; }
}

/// <summary>新增 Authoring 规则入参（access/users/roles 可空走默认）。</summary>
public record WebdavAuthoringRuleOpts
{
    public string extension { get; init; }
    public string path { get; init; }
    public string access { get; init; }
    public string users { get; init; }
    public string roles { get; init; }
}
