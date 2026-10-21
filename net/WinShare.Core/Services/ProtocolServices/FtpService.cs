// 移植自 electron/services/ftp.ts（FTP/IIS 服务器配置 + 服务控制）。
// 命令串逐字复制自 TS：ftpsvc 服务控制、appcmd 解锁 ftpServer/* 配置节、
// Get-/Set-WebConfigurationProperty 批读写（MACHINE/WEBROOT/APPHOST 级）。
// 站点级配置（端口/SSL/授权）由 ftpAdapter 经 IIS WebAdministration 处理，不在本服务内。
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Shares; // PsGate：服务域 PS 参数闸门窄门面（与 TS 调用点同名）

namespace WinShare.Core.Services.ProtocolServices;

/// <summary>FTP（IIS）服务器配置与服务控制（TS: electron/services/ftp.ts）。</summary>
public sealed class FtpService : IFtpService
{
    /// <summary>TS: ensureFtpSectionsUnlocked / setConfig / getConfig 的超时（毫秒）。</summary>
    private const int IisTimeoutMs = 30000;

    private const string ServiceName = "ftpsvc";

    // TS: restartService —— 依赖服务无法停止时的兜底（Cannot stop → 尝试 Start-Service）
    private const string RestartCommand =
        "try { Restart-Service -Name ftpsvc -Force -ErrorAction Stop } catch { if ($_.Exception.Message -match " +
        "'Cannot stop|无法停止|dependent|依赖') { Start-Service -Name ftpsvc -ErrorAction SilentlyContinue; " +
        "if ((Get-Service ftpsvc).Status -ne 'Running') { throw } } else { throw } }";

    // TS: appcmd 全路径（$env:windir 在 PS 内展开）
    private const string AppCmd = @"$env:windir\system32\inetsrv\appcmd.exe";

    // TS: sections —— 注意 TS 表里已带外层单引号，逐字保留（/section:'ftpServer/...'）
    private static readonly string[] UnlockSections =
    {
        "'ftpServer/security/authentication/anonymousAuthentication'",
        "'ftpServer/security/authentication/basicAuthentication'",
        "'ftpServer/security/authentication/customAuthentication'",
        "'ftpServer/security/authorization'",
        "'ftpServer/security/ssl'",
        "'ftpServer/userIsolation'",
        "'ftpServer/messages'",
        "'ftpServer/directoryBrowse'",
        "'ftpServer/connections'",
        "'ftpServer/fileHandling'",
        "'ftpServer/firewallSupport'",
        "'ftpServer/logFile'",
    };

    private static readonly Regex AccessDenied = new(
        @"access is denied|拒绝访问|administrator|权限", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CannotStop = new(
        @"Cannot stop|无法停止|dependent|依赖", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NewLine = new(@"\r?\n", RegexOptions.Compiled);

    /// <summary>
    /// TS: FTP_FIELDS —— 驱动 getConfig / setConfig 命令构建的字段表（顺序即命令段落顺序）。
    /// 类型：Text 覆盖 TS 的 'string' 与 'enum'（两者在本文件里行为完全一致：
    /// 读侧走 String(v)，写侧走 psQuote）。
    /// </summary>
    private static readonly Field[] FtpFields =
    {
        new("sslControlChannelPolicy", "ftpServer/security/ssl", "controlChannelPolicy", FieldType.Text),
        new("sslDataChannelPolicy", "ftpServer/security/ssl", "dataChannelPolicy", FieldType.Text),
        new("sslServerCertHash", "ftpServer/security/ssl", "serverCertHash", FieldType.Text),
        new("sslClientCertRequired", "ftpServer/security/ssl", "clientCertRequired", FieldType.Boolean),
        new("ssl128", "ftpServer/security/ssl", "ssl128", FieldType.Boolean),
        new("anonymousEnabled", "ftpServer/security/authentication/anonymousAuthentication", "enabled", FieldType.Boolean),
        new("anonymousUserName", "ftpServer/security/authentication/anonymousAuthentication", "userName", FieldType.Text),
        new("basicEnabled", "ftpServer/security/authentication/basicAuthentication", "enabled", FieldType.Boolean),
        new("firewallLowDataChannelPort", "ftpServer/firewallSupport", "lowDataChannelPort", FieldType.Number),
        new("firewallHighDataChannelPort", "ftpServer/firewallSupport", "highDataChannelPort", FieldType.Number),
        new("greetingMessage", "ftpServer/messages", "greetingMessage", FieldType.Text),
        new("bannerMessage", "ftpServer/messages", "bannerMessage", FieldType.Text),
        new("exitMessage", "ftpServer/messages", "exitMessage", FieldType.Text),
        new("maxClientsMessage", "ftpServer/messages", "maxClientsMessage", FieldType.Text),
        new("suppressDefaultMessages", "ftpServer/messages", "suppressDefault", FieldType.Boolean),
        new("showVirtualDirs", "ftpServer/directoryBrowse", "showVirtualDirs", FieldType.Boolean),
        new("userIsolationMode", "ftpServer/userIsolation", "mode", FieldType.Text),
        new("unauthenticatedTimeout", "ftpServer/connections", "unauthenticatedTimeout", FieldType.Number),
        new("controlConnectionTimeout", "ftpServer/connections", "controlConnectionTimeout", FieldType.Number),
        new("dataChannelConnectionTimeout", "ftpServer/connections", "dataChannelConnectionTimeout", FieldType.Number),
        new("keepPartialUploads", "ftpServer/fileHandling", "keepPartialUploads", FieldType.Boolean),
        new("allowReplaceOnRename", "ftpServer/fileHandling", "allowReplaceOnRename", FieldType.Boolean),
        new("logFileDirectory", "ftpServer/logFile", "directory", FieldType.Text),
        new("logFilePeriod", "ftpServer/logFile", "period", FieldType.Text),
    };

    private readonly IPsInvoker _ps;

    public FtpService(IPsInvoker? ps = null) => _ps = ps ?? PsRunner.Default;

    /// <summary>TS: defaultConfig()——IIS FTP 7.5+ 默认配置。</summary>
    public FtpServerConfig DefaultConfig() => new()
    {
        sslControlChannelPolicy = SslPolicy.SslAllow,
        sslDataChannelPolicy = SslPolicy.SslAllow,
        sslServerCertHash = "",
        sslClientCertRequired = false,
        ssl128 = false,
        anonymousEnabled = false,
        anonymousUserName = "IUSR",
        basicEnabled = false,
        firewallLowDataChannelPort = 0,
        firewallHighDataChannelPort = 0,
        greetingMessage = "",
        bannerMessage = "",
        exitMessage = "",
        maxClientsMessage = "",
        suppressDefaultMessages = false,
        showVirtualDirs = false,
        userIsolationMode = FtpUserIsolationMode.None,
        unauthenticatedTimeout = 30,
        controlConnectionTimeout = 300,
        dataChannelConnectionTimeout = 30,
        keepPartialUploads = false,
        allowReplaceOnRename = false,
        logFileDirectory = @"%SystemDrive%\inetpub\logs\LogFiles",
        logFilePeriod = FtpLogPeriod.Daily,
    };

    /// <summary>TS: getServiceStatus()。</summary>
    public async Task<ServiceStatus> GetServiceStatusAsync()
    {
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(
                $"Get-Service {ServiceName} -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType",
                15000, PsRunner.DefaultRetries);
            return ServiceStatusReader.Read(raw, ServiceName);
        }
        catch
        {
            return new ServiceStatus { name = ServiceName, status = ServiceState.Unknown, startType = "Unknown" };
        }
    }

    /// <summary>TS: serviceExists(name)——避免对不存在的服务执行 Restart-Service 抛错。</summary>
    private async Task<bool> ServiceExistsAsync()
    {
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(
                $"Get-Service {ServiceName} -ErrorAction SilentlyContinue | Select-Object Name", 15000, 0);
            JsonElement r = ConfigJson.Obj(raw);
            return !string.IsNullOrEmpty(ConfigJson.Str(r, "Name"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>TS: restartService()。</summary>
    public async Task RestartServiceAsync()
    {
        if (!await ServiceExistsAsync())
        {
            throw Errors.CommandFailed(
                "ftpsvc 服务未安装。FTP 依赖 IIS 角色，请在「共享管理」页 FTP Tab 按引导安装 FTP 角色服务后重试");
        }
        try
        {
            await _ps.RunVoidAsync(RestartCommand, 15000, 0);
        }
        catch (Exception e)
        {
            string msg = e.Message ?? "";
            if (AccessDenied.IsMatch(msg))
            {
                throw Errors.CommandFailed("重启 ftpsvc 服务需要管理员权限，请以管理员身份运行 WinShare Panel");
            }
            if (CannotStop.IsMatch(msg))
            {
                throw Errors.CommandFailed(
                    "ftpsvc 服务存在依赖服务无法重启，请手动停止依赖服务后重试，或重启计算机");
            }
            throw Errors.CommandFailed($"重启 ftpsvc 服务失败：{Slice(msg, 200)}");
        }
    }

    /// <summary>TS: startService()。</summary>
    public async Task StartServiceAsync()
    {
        if (!await ServiceExistsAsync())
        {
            throw Errors.CommandFailed("ftpsvc 服务未安装，请先安装 FTP 角色服务");
        }
        try
        {
            await _ps.RunVoidAsync($"Start-Service -Name {ServiceName} -ErrorAction Stop", 15000, 0);
        }
        catch (Exception e)
        {
            throw Errors.CommandFailed($"启动 ftpsvc 服务失败：{Slice(e.Message, 200)}");
        }
    }

    /// <summary>TS: stopService()。</summary>
    public async Task StopServiceAsync()
    {
        if (!await ServiceExistsAsync())
        {
            throw Errors.CommandFailed("ftpsvc 服务未安装，请先安装 FTP 角色服务");
        }
        try
        {
            await _ps.RunVoidAsync($"Stop-Service -Name {ServiceName} -Force -ErrorAction Stop", 15000, 0);
        }
        catch (Exception e)
        {
            throw Errors.CommandFailed($"停止 ftpsvc 服务失败：{Slice(e.Message, 200)}");
        }
    }

    /// <summary>
    /// TS: ensureFtpSectionsUnlocked()——overrideModeDefault=Deny 的节需 appcmd unlock 后才能在服务器级 Set；
    /// 同时解锁 authorization 供 ftpAdapter 站点级 setPermissions 复用。
    /// best-effort：失败不阻断后续写入（部分节可能已解锁）。
    /// </summary>
    public async Task EnsureFtpSectionsUnlockedAsync()
    {
        string cmd = string.Join("; ", UnlockSections.Select(s =>
            $"try {{ & {AppCmd} unlock config /section:{s} 2>&1 | Out-Null }} catch {{}}"));
        try
        {
            await _ps.RunVoidAsync(cmd, IisTimeoutMs, 0);
        }
        catch
        {
            // best-effort：解锁失败不阻断
        }
    }

    /// <summary>TS: getConfig()——单次 PowerShell 批量读取所有节；整体失败返回默认配置。</summary>
    public async Task<FtpServerConfig> GetConfigAsync()
    {
        try
        {
            string readParts = string.Join("; ", FtpFields.Select(f =>
                $"try {{ $r | Add-Member -MemberType NoteProperty -Name {f.tsField} -Value " +
                $"(Get-WebConfigurationProperty -Filter '{f.filter}' -PSPath 'MACHINE/WEBROOT/APPHOST' " +
                $"-Name {f.psName} -ErrorAction Stop) }} catch {{}}"));
            string cmd = $"Import-Module WebAdministration; $r = [PSCustomObject]@{{}}; {readParts}; $r";
            JsonElement raw = await _ps.RunJsonAsync(cmd, 15000, 0);
            JsonElement obj = ConfigJson.Obj(raw);
            FtpServerConfig def = DefaultConfig();

            bool Bool(string n, bool fallback) =>
                ConfigJson.IsAbsentOrNull(obj, n) ? fallback : ConfigJson.Truthy(obj, n);
            int Num(string n, int fallback) =>
                ConfigJson.IsAbsentOrNull(obj, n) ? fallback : ConfigJson.IntOr(obj, n, fallback);
            string Text(string n, string fallback) =>
                ConfigJson.IsAbsentOrNull(obj, n) ? fallback : ConfigJson.JsString(obj, n) ?? fallback;

            return new FtpServerConfig
            {
                sslControlChannelPolicy = ConfigEnum.Parse(Text("sslControlChannelPolicy", ""), def.sslControlChannelPolicy),
                sslDataChannelPolicy = ConfigEnum.Parse(Text("sslDataChannelPolicy", ""), def.sslDataChannelPolicy),
                sslServerCertHash = Text("sslServerCertHash", def.sslServerCertHash),
                sslClientCertRequired = Bool("sslClientCertRequired", def.sslClientCertRequired),
                ssl128 = Bool("ssl128", def.ssl128),
                anonymousEnabled = Bool("anonymousEnabled", def.anonymousEnabled),
                anonymousUserName = Text("anonymousUserName", def.anonymousUserName),
                basicEnabled = Bool("basicEnabled", def.basicEnabled),
                firewallLowDataChannelPort = Num("firewallLowDataChannelPort", def.firewallLowDataChannelPort),
                firewallHighDataChannelPort = Num("firewallHighDataChannelPort", def.firewallHighDataChannelPort),
                greetingMessage = Text("greetingMessage", def.greetingMessage),
                bannerMessage = Text("bannerMessage", def.bannerMessage),
                exitMessage = Text("exitMessage", def.exitMessage),
                maxClientsMessage = Text("maxClientsMessage", def.maxClientsMessage),
                suppressDefaultMessages = Bool("suppressDefaultMessages", def.suppressDefaultMessages),
                showVirtualDirs = Bool("showVirtualDirs", def.showVirtualDirs),
                userIsolationMode = ConfigEnum.Parse(Text("userIsolationMode", ""), def.userIsolationMode),
                unauthenticatedTimeout = Num("unauthenticatedTimeout", def.unauthenticatedTimeout),
                controlConnectionTimeout = Num("controlConnectionTimeout", def.controlConnectionTimeout),
                dataChannelConnectionTimeout = Num("dataChannelConnectionTimeout", def.dataChannelConnectionTimeout),
                keepPartialUploads = Bool("keepPartialUploads", def.keepPartialUploads),
                allowReplaceOnRename = Bool("allowReplaceOnRename", def.allowReplaceOnRename),
                logFileDirectory = Text("logFileDirectory", def.logFileDirectory),
                logFilePeriod = ConfigEnum.Parse(Text("logFilePeriod", ""), def.logFilePeriod),
            };
        }
        catch
        {
            return DefaultConfig();
        }
    }

    /// <summary>TS: setConfig(config)——per-field try/catch，部分节锁定不阻断其他字段写入。</summary>
    public Task SetConfigAsync(FtpServerConfig config) => SetConfigAsync(FtpConfigPatch.FromFull(config));

    /// <summary>TS: setConfig(config: Partial&lt;FtpServerConfig&gt;)。</summary>
    public async Task SetConfigAsync(FtpConfigPatch config)
    {
        // 预解锁配置节
        await EnsureFtpSectionsUnlockedAsync();
        List<string> writeParts = new();
        foreach (Field f in FtpFields)
        {
            object? val = config[f.tsField];
            if (val is null) continue; // TS: val === undefined → 不写入
            string? psVal = f.type switch
            {
                FieldType.Boolean => PsGate.PsBool((bool)val),
                FieldType.Number => PsGate.PsNumber(val),
                // 字符串 / 枚举：清理换行，单引号转义
                _ => PsGate.PsQuote(NewLine.Replace(Convert.ToString(val, CultureInfo.InvariantCulture) ?? "", " ")),
            };
            if (psVal is null) continue; // 非法类型跳过，杜绝注入
            writeParts.Add(
                $"try {{ Set-WebConfigurationProperty -Filter '{f.filter}' -PSPath 'MACHINE/WEBROOT/APPHOST' " +
                $"-Name {f.psName} -Value {psVal} -ErrorAction Stop }} catch {{}}");
        }
        if (writeParts.Count == 0) throw Errors.InvalidParam("未提供任何配置项");
        await _ps.RunVoidAsync(string.Join("; ", writeParts), IisTimeoutMs, 0);
    }

    /// <summary>TS: restoreDefault()。</summary>
    public async Task<FtpServerConfig> RestoreDefaultAsync()
    {
        FtpServerConfig def = DefaultConfig();
        await SetConfigAsync(def);
        return def;
    }

    // TS: msg.slice(0, 200)
    private static string Slice(string? s, int max) => s is null ? "" : (s.Length > max ? s[..max] : s);

    private enum FieldType { Boolean, Number, Text }

    /// <summary>TS: FtpConfigField。</summary>
    private readonly record struct Field(string tsField, string filter, string psName, FieldType type);
}

/// <summary>
/// TS: setConfig(config: Partial&lt;FtpServerConfig&gt;) 的部分配置入参（未提供 = null = 不写入）。
/// 索引器按字段表 tsField 取值，故 getConfig 的字段表可同时驱动 setConfig。
/// </summary>
public sealed record FtpConfigPatch
{
    public SslPolicy? sslControlChannelPolicy { get; init; }
    public SslPolicy? sslDataChannelPolicy { get; init; }
    public string? sslServerCertHash { get; init; }
    public bool? sslClientCertRequired { get; init; }
    public bool? ssl128 { get; init; }
    public bool? anonymousEnabled { get; init; }
    public string? anonymousUserName { get; init; }
    public bool? basicEnabled { get; init; }
    public int? firewallLowDataChannelPort { get; init; }
    public int? firewallHighDataChannelPort { get; init; }
    public string? greetingMessage { get; init; }
    public string? bannerMessage { get; init; }
    public string? exitMessage { get; init; }
    public string? maxClientsMessage { get; init; }
    public bool? suppressDefaultMessages { get; init; }
    public bool? showVirtualDirs { get; init; }
    public FtpUserIsolationMode? userIsolationMode { get; init; }
    public int? unauthenticatedTimeout { get; init; }
    public int? controlConnectionTimeout { get; init; }
    public int? dataChannelConnectionTimeout { get; init; }
    public bool? keepPartialUploads { get; init; }
    public bool? allowReplaceOnRename { get; init; }
    public string? logFileDirectory { get; init; }
    public FtpLogPeriod? logFilePeriod { get; init; }

    /// <summary>按 tsField 取值（null = 未提供）。</summary>
    public object? this[string tsField] => tsField switch
    {
        "sslControlChannelPolicy" => sslControlChannelPolicy,
        "sslDataChannelPolicy" => sslDataChannelPolicy,
        "sslServerCertHash" => sslServerCertHash,
        "sslClientCertRequired" => sslClientCertRequired,
        "ssl128" => ssl128,
        "anonymousEnabled" => anonymousEnabled,
        "anonymousUserName" => anonymousUserName,
        "basicEnabled" => basicEnabled,
        "firewallLowDataChannelPort" => firewallLowDataChannelPort,
        "firewallHighDataChannelPort" => firewallHighDataChannelPort,
        "greetingMessage" => greetingMessage,
        "bannerMessage" => bannerMessage,
        "exitMessage" => exitMessage,
        "maxClientsMessage" => maxClientsMessage,
        "suppressDefaultMessages" => suppressDefaultMessages,
        "showVirtualDirs" => showVirtualDirs,
        "userIsolationMode" => userIsolationMode,
        "unauthenticatedTimeout" => unauthenticatedTimeout,
        "controlConnectionTimeout" => controlConnectionTimeout,
        "dataChannelConnectionTimeout" => dataChannelConnectionTimeout,
        "keepPartialUploads" => keepPartialUploads,
        "allowReplaceOnRename" => allowReplaceOnRename,
        "logFileDirectory" => logFileDirectory,
        "logFilePeriod" => logFilePeriod,
        _ => null,
    };

    /// <summary>完整配置 → 全字段提供。</summary>
    public static FtpConfigPatch FromFull(FtpServerConfig c) => new()
    {
        sslControlChannelPolicy = c.sslControlChannelPolicy,
        sslDataChannelPolicy = c.sslDataChannelPolicy,
        sslServerCertHash = c.sslServerCertHash,
        sslClientCertRequired = c.sslClientCertRequired,
        ssl128 = c.ssl128,
        anonymousEnabled = c.anonymousEnabled,
        anonymousUserName = c.anonymousUserName,
        basicEnabled = c.basicEnabled,
        firewallLowDataChannelPort = c.firewallLowDataChannelPort,
        firewallHighDataChannelPort = c.firewallHighDataChannelPort,
        greetingMessage = c.greetingMessage,
        bannerMessage = c.bannerMessage,
        exitMessage = c.exitMessage,
        maxClientsMessage = c.maxClientsMessage,
        suppressDefaultMessages = c.suppressDefaultMessages,
        showVirtualDirs = c.showVirtualDirs,
        userIsolationMode = c.userIsolationMode,
        unauthenticatedTimeout = c.unauthenticatedTimeout,
        controlConnectionTimeout = c.controlConnectionTimeout,
        dataChannelConnectionTimeout = c.dataChannelConnectionTimeout,
        keepPartialUploads = c.keepPartialUploads,
        allowReplaceOnRename = c.allowReplaceOnRename,
        logFileDirectory = c.logFileDirectory,
        logFilePeriod = c.logFilePeriod,
    };
}
