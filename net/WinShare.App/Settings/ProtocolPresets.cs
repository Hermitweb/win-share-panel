namespace WinShare.App.Settings;

/// <summary>
/// 配置预设（一键应用服务器级配置；只含各协议可写字段，只读字段刻意不出现。
/// 数据镜像 <c>src/utils/configPresets.ts</c> [Source: src/utils/configPresets.ts]）。
/// 与「权限模板」(IPresetService) 是两回事：预设作用于服务器级配置。
/// </summary>
public sealed record ConfigPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>降级安全性时必填的风险提示，应用前二次确认展示（对应 TS <c>risk?</c>）。</summary>
    public string? Risk { get; init; }
    /// <summary>setConfig 白名单可写字段 → 值（键名与契约配置类型属性名一致）。</summary>
    public required Dictionary<string, object?> Values { get; init; }
}

public static class ProtocolPresets
{
    public static IReadOnlyList<ConfigPreset> Smb { get; } =
    [
        new ConfigPreset
        {
            Id = "smb-sec-hardened",
            Name = "安全加固（内网服务器）",
            Description = "强制签名、严格名称检查、SMB1 审计、禁访客、不广播存在",
            Values = new()
            {
                ["enableSMB1Protocol"] = false,
                ["enableSMB2Protocol"] = true,
                ["enableSMB3Protocol"] = true,
                ["enableGuestUserAccess"] = false,
                ["enableInsecureGuestLogons"] = false,
                ["auditSmb1Access"] = true,
                ["requireSecuritySignature"] = true,
                ["enableStrictNameChecking"] = true,
                ["announceServer"] = false,
                ["unauthenticatedUsersTimeLimit"] = 30,
                ["enableLeasing"] = true,
                ["enableOplocks"] = true,
            },
        },
        new ConfigPreset
        {
            Id = "smb-perf",
            Name = "性能优先（大文件传输）",
            Description = "多通道/租约/机会锁/目录缓存全开，压缩取速度优化，超时放宽",
            Values = new()
            {
                ["enableMultiChannel"] = true,
                ["enableLeasing"] = true,
                ["enableOplocks"] = true,
                ["enableOplockDirectoryCache"] = true,
                ["enableSMBDirectoryCache"] = true,
                ["enableChannelChange"] = true,
                ["requestCompression"] = "OptimizeForSpeed",
                ["maxWorkItems"] = 2048,
                ["sessionTimeoutSeconds"] = 300,
                ["maxSessionPerConnection"] = 1024,
            },
        },
        new ConfigPreset
        {
            Id = "smb-compat",
            Name = "兼容优先（老旧客户端/NAS）",
            Description = "放开签名与严格名称检查、允许访客共享，兼容老设备接入",
            Risk = "关闭强制签名并允许访客会降低安全性，仅建议在可信内网/排障时段使用",
            Values = new()
            {
                ["requireSecuritySignature"] = false,
                ["enableStrictNameChecking"] = false,
                ["enableGuestUserAccess"] = true,
                ["enableInsecureGuestLogons"] = true,
                ["requestCompression"] = "Off",
            },
        },
        new ConfigPreset
        {
            Id = "smb-home",
            Name = "家庭局域网便捷",
            Description = "访客可发现、免签名，SMB2/3 保持开启，适合家用 NAS 场景",
            Risk = "访客访问对内开放，请确认设备处于可信局域网",
            Values = new()
            {
                ["enableSMB1Protocol"] = false,
                ["enableSMB2Protocol"] = true,
                ["enableSMB3Protocol"] = true,
                ["enableGuestUserAccess"] = true,
                ["requireSecuritySignature"] = false,
                ["announceServer"] = true,
                ["enableLeasing"] = true,
            },
        },
    ];

    public static IReadOnlyList<ConfigPreset> Nfs { get; } =
    [
        new ConfigPreset
        {
            Id = "nfs-sec",
            Name = "安全优先（Kerberos 环境）",
            Description = "关闭未映射访问、开启协商与活动日志、优雅卸载",
            Values = new()
            {
                ["enableUnmappedAccess"] = false,
                ["enableAuthenticationRenegotiation"] = true,
                ["logActivity"] = true,
                ["gracefulUnmount"] = true,
                ["restartConnectionTimeout"] = 60,
            },
        },
        new ConfigPreset
        {
            Id = "nfs-lan",
            Name = "内网便捷（AUTH_SYS）",
            Description = "允许未映射访问、降低日志开销、加大目录缓存",
            Risk = "未映射访问以 UID/GID 信任客户端，仅限可信内网",
            Values = new()
            {
                ["enableUnmappedAccess"] = true,
                ["logActivity"] = false,
                ["gracefulUnmount"] = true,
                ["directoryCacheExpiry"] = 120,
            },
        },
    ];

    public static IReadOnlyList<ConfigPreset> Ftp { get; } =
    [
        new ConfigPreset
        {
            Id = "ftp-secure",
            Name = "对外加密分发",
            Description = "FTPS 强制、禁匿名、用户目录隔离、被动端口段（需防火墙放行）",
            Risk = "被动模式端口段 50000-51000 需在防火墙/路由器放行",
            Values = new()
            {
                ["sslControlChannelPolicy"] = "SslRequire",
                ["sslDataChannelPolicy"] = "SslRequire",
                ["anonymousEnabled"] = false,
                ["basicEnabled"] = true,
                ["userIsolationMode"] = "StartInUsersDirectory",
                ["unauthenticatedTimeout"] = 15,
                ["firewallLowDataChannelPort"] = 50000,
                ["firewallHighDataChannelPort"] = 51000,
                ["logFilePeriod"] = "Daily",
            },
        },
        new ConfigPreset
        {
            Id = "ftp-lan",
            Name = "内网便捷传输",
            Description = "TLS 可选、基本认证、无隔离、保留断点续传碎片",
            Risk = "TLS 可选意味着允许明文连接，仅限可信内网",
            Values = new()
            {
                ["sslControlChannelPolicy"] = "SslAllow",
                ["sslDataChannelPolicy"] = "SslAllow",
                ["anonymousEnabled"] = false,
                ["basicEnabled"] = true,
                ["userIsolationMode"] = "None",
                ["keepPartialUploads"] = true,
                ["allowReplaceOnRename"] = true,
            },
        },
        new ConfigPreset
        {
            Id = "ftp-anon-ro",
            Name = "匿名下载站",
            Description = "匿名登录 + 基本认证关闭；写入需另行在授权规则限制",
            Risk = "任何人可匿名登录，请确保站点目录仅放置可公开内容",
            Values = new()
            {
                ["sslControlChannelPolicy"] = "SslAllow",
                ["sslDataChannelPolicy"] = "SslAllow",
                ["anonymousEnabled"] = true,
                ["anonymousUserName"] = "IUSR",
                ["basicEnabled"] = false,
                ["userIsolationMode"] = "None",
                ["keepPartialUploads"] = false,
            },
        },
    ];

    public static IReadOnlyList<ConfigPreset> Webdav { get; } =
    [
        new ConfigPreset
        {
            Id = "wd-read-only",
            Name = "安全只读发布",
            Description = "关闭 authoring，仅 Windows 认证，集成校验开启",
            Values = new()
            {
                ["authoringEnabled"] = false,
                ["anonymousEnabled"] = false,
                ["basicEnabled"] = false,
                ["windowsEnabled"] = true,
                ["verifyIntegration"] = true,
                ["allowDoubleEscaping"] = false,
            },
        },
        new ConfigPreset
        {
            Id = "wd-collab",
            Name = "内网协作写入",
            Description = "开启 authoring 与大请求体（约 286MB），Windows 认证",
            Values = new()
            {
                ["authoringEnabled"] = true,
                ["anonymousEnabled"] = false,
                ["windowsEnabled"] = true,
                ["basicEnabled"] = false,
                ["maxAllowedContentLength"] = 300000000L,
                ["authoringMaxRequestBodySize"] = 300000000L,
                ["verifyIntegration"] = true,
            },
        },
        new ConfigPreset
        {
            Id = "wd-drop",
            Name = "匿名投递区（高危）",
            Description = "任何人可上传：匿名 + authoring 开启",
            Risk = "匿名上传可被滥用（恶意文件/占满磁盘），务必配合独立目录与配额，建议仅短时开启",
            Values = new()
            {
                ["authoringEnabled"] = true,
                ["anonymousEnabled"] = true,
                ["windowsEnabled"] = false,
                ["basicEnabled"] = false,
                ["allowDoubleEscaping"] = false,
            },
        },
    ];

}


/// <summary>Protocol 扩展面（WinShare.Core.Contracts.Protocol）。</summary>
public static class ProtocolExtensions
{
    /// <summary>展示名（SMB/NFS/FTP/WEBDAV；对齐 TS <c>proto.toUpperCase()</c> 用法）。</summary>
    public static string Display(this WinShare.Core.Contracts.Protocol p) => p.ToString().ToUpperInvariant();
}
