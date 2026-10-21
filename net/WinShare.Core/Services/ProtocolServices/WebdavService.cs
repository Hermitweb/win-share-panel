// 移植自 electron/services/webdav.ts（WebDAV/IIS 服务器配置 + 服务控制）。
// 命令串逐字复制自 TS：W3SVC 服务控制、appcmd 解锁 system.webServer/* 配置节、
// Get-/Set-WebConfigurationProperty 批读写（MACHINE/WEBROOT/APPHOST 级）。
// 站点级配置（authoring/authoringRules）由 webdavAdapter 经 WebAdministration 处理，不在本服务内。
// 项目记忆强约束：配置节必须用完整路径（短路径解析存在缺陷），故 FILTER 常量逐字保留。
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Shares; // PsGate：服务域 PS 参数闸门窄门面（与 TS 调用点同名）

namespace WinShare.Core.Services.ProtocolServices;

/// <summary>WebDAV（IIS）服务器配置与服务控制（TS: electron/services/webdav.ts）。</summary>
public sealed class WebdavService : IWebdavService
{
    private const int IisTimeoutMs = 30000;
    private const string ServiceName = "W3SVC";

    // TS: restartService —— 依赖服务无法停止时的兜底
    private const string RestartCommand =
        "try { Restart-Service -Name W3SVC -Force -ErrorAction Stop } catch { if ($_.Exception.Message -match " +
        "'Cannot stop|无法停止|dependent|依赖') { Start-Service -Name W3SVC -ErrorAction SilentlyContinue; " +
        "if ((Get-Service W3SVC).Status -ne 'Running') { throw } } else { throw } }";

    private const string AppCmd = @"$env:windir\system32\inetsrv\appcmd.exe";

    // === WebDAV 配置节完整路径 ===
    private const string AuthoringFilter = "system.webServer/webdav/authoring";
    private const string AuthoringRulesFilter = "system.webServer/webdav/authoring/authoringRules";
    private const string AnonAuthFilter = "system.webServer/security/authentication/anonymousAuthentication";
    private const string BasicAuthFilter = "system.webServer/security/authentication/basicAuthentication";
    private const string WindowsAuthFilter = "system.webServer/security/authentication/windowsAuthentication";
    private const string RequestFilteringFilter = "system.webServer/security/requestFiltering";
    private const string UrlCompressionFilter = "system.webServer/urlCompression";
    private const string AccessFilter = "system.webServer/security/access";

    private static readonly string[] UnlockSections =
    {
        $"'{AuthoringFilter}'",
        $"'{AuthoringRulesFilter}'",
        $"'{AnonAuthFilter}'",
        $"'{BasicAuthFilter}'",
        $"'{WindowsAuthFilter}'",
        $"'{RequestFilteringFilter}'",
        $"'{AccessFilter}'",
        $"'{UrlCompressionFilter}'",
    };

    private static readonly Regex AccessDenied = new(
        @"access is denied|拒绝访问|administrator|权限", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CannotStop = new(
        @"Cannot stop|无法停止|dependent|依赖", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>TS: WEBDAV_FIELDS —— 可写字段表（WebDAV 配置无字符串/枚举字段）。</summary>
    private static readonly Field[] WebdavFields =
    {
        new("authoringEnabled", AuthoringFilter, "enabled", FieldType.Boolean),
        new("authoringMaxRequestBodySize", AuthoringFilter, "maxRequestBodySize", FieldType.Number),
        new("maxAllowedContentLength", RequestFilteringFilter, "maxAllowedContentLength", FieldType.Number),
        new("allowDoubleEscaping", RequestFilteringFilter, "allowDoubleEscaping", FieldType.Boolean),
        new("verifyIntegration", RequestFilteringFilter, "verifyIntegration", FieldType.Boolean),
        new("anonymousEnabled", AnonAuthFilter, "enabled", FieldType.Boolean),
        new("basicEnabled", BasicAuthFilter, "enabled", FieldType.Boolean),
        new("windowsEnabled", WindowsAuthFilter, "enabled", FieldType.Boolean),
        new("maxUrlLength", RequestFilteringFilter + "/requestLimits", "maxUrl", FieldType.Number),
        new("maxQueryStringLength", RequestFilteringFilter + "/requestLimits", "maxQueryString", FieldType.Number),
    };

    private readonly IPsInvoker _ps;

    public WebdavService(IPsInvoker? ps = null) => _ps = ps ?? PsRunner.Default;

    /// <summary>TS: defaultConfig()——IIS WebDAV 默认配置。</summary>
    public WebdavServerConfig DefaultConfig() => new()
    {
        authoringEnabled = false,
        authoringMaxRequestBodySize = 0,
        maxAllowedContentLength = 30000000,
        allowDoubleEscaping = false,
        verifyIntegration = true,
        anonymousEnabled = false,
        basicEnabled = false,
        windowsEnabled = true,
        maxUrlLength = 260,
        maxQueryStringLength = 2048,
        // 只读信息
        globalAuthoringRulesCount = 0,
        enableStaticCompression = true,
        enableDynamicCompression = false,
        requireSSL = false,
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

    /// <summary>TS: serviceExists(name)。</summary>
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
                "W3SVC 服务未安装。WebDAV 依赖 IIS 角色，请在「共享管理」页 WebDAV Tab 按引导安装 IIS 角色后重试");
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
                throw Errors.CommandFailed("重启 W3SVC 服务需要管理员权限，请以管理员身份运行 WinShare Panel");
            }
            if (CannotStop.IsMatch(msg))
            {
                throw Errors.CommandFailed(
                    "W3SVC 服务存在依赖服务无法重启，请手动停止依赖服务后重试，或重启计算机");
            }
            throw Errors.CommandFailed($"重启 W3SVC 服务失败：{Slice(msg, 200)}");
        }
    }

    /// <summary>TS: startService()。</summary>
    public async Task StartServiceAsync()
    {
        if (!await ServiceExistsAsync())
        {
            throw Errors.CommandFailed("W3SVC 服务未安装，请先安装 IIS 角色");
        }
        try
        {
            await _ps.RunVoidAsync($"Start-Service -Name {ServiceName} -ErrorAction Stop", 15000, 0);
        }
        catch (Exception e)
        {
            throw Errors.CommandFailed($"启动 W3SVC 服务失败：{Slice(e.Message, 200)}");
        }
    }

    /// <summary>TS: stopService()。</summary>
    public async Task StopServiceAsync()
    {
        if (!await ServiceExistsAsync())
        {
            throw Errors.CommandFailed("W3SVC 服务未安装，请先安装 IIS 角色");
        }
        try
        {
            await _ps.RunVoidAsync($"Stop-Service -Name {ServiceName} -Force -ErrorAction Stop", 15000, 0);
        }
        catch (Exception e)
        {
            throw Errors.CommandFailed($"停止 W3SVC 服务失败：{Slice(e.Message, 200)}");
        }
    }

    /// <summary>
    /// TS: ensureWebdavSectionsUnlocked()——overrideModeDefault=Deny 的节需 appcmd unlock 后才能在服务器级 Set；
    /// 同时解锁 authoring/authoringRules 供 webdavAdapter 站点级写入复用。
    /// 幂等 + best-effort：失败不阻断后续写入（由 Set 的 try/catch 兜底）。
    /// </summary>
    public async Task EnsureWebdavSectionsUnlockedAsync()
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

    /// <summary>TS: getConfig()——单次批量读取所有节 + 只读字段；整体失败返回默认配置。</summary>
    public async Task<WebdavServerConfig> GetConfigAsync()
    {
        try
        {
            string readParts = string.Join("; ", WebdavFields.Select(f =>
                $"try {{ $r | Add-Member -MemberType NoteProperty -Name {f.tsField} -Value " +
                $"(Get-WebConfigurationProperty -Filter '{f.filter}' -PSPath 'MACHINE/WEBROOT/APPHOST' " +
                $"-Name {f.psName} -ErrorAction Stop) }} catch {{}}"));

            // 只读字段：globalAuthoringRulesCount（authoringRules 计数）、压缩、SSL
            string[] readOnlyParts =
            {
                $"try {{ $r | Add-Member -MemberType NoteProperty -Name globalAuthoringRulesCount -Value " +
                $"@(Get-WebConfiguration -Filter '{AuthoringRulesFilter}/*' -PSPath 'MACHINE/WEBROOT/APPHOST' " +
                "-ErrorAction Stop).Count } catch { $r | Add-Member -MemberType NoteProperty " +
                "-Name globalAuthoringRulesCount -Value 0 }",
                ReadOnlyPart("enableStaticCompression", "doStaticCompression"),
                ReadOnlyPart("enableDynamicCompression", "doDynamicCompression"),
                $"try {{ $f = (Get-WebConfigurationProperty -Filter '{AccessFilter}' " +
                "-PSPath 'MACHINE/WEBROOT/APPHOST' -Name sslFlags -ErrorAction Stop); " +
                "$r | Add-Member -MemberType NoteProperty -Name requireSSL -Value " +
                "($f -and $f -ne 'None' -and $f -ne '') } catch {}",
            };

            string cmd = $"Import-Module WebAdministration; $r = [PSCustomObject]@{{}}; {readParts}; " +
                $"{string.Join("; ", readOnlyParts)}; $r";
            JsonElement raw = await _ps.RunJsonAsync(cmd, 15000, 0);
            JsonElement obj = ConfigJson.Obj(raw);
            WebdavServerConfig def = DefaultConfig();

            bool Bool(string n, bool fallback) =>
                ConfigJson.IsAbsentOrNull(obj, n) ? fallback : ConfigJson.Truthy(obj, n);
            long Num(string n, long fallback) =>
                ConfigJson.IsAbsentOrNull(obj, n) ? fallback : ConfigJson.LongOr(obj, n, fallback);
            int IntNum(string n, int fallback) =>
                ConfigJson.IsAbsentOrNull(obj, n) ? fallback : ConfigJson.IntOr(obj, n, fallback);

            return new WebdavServerConfig
            {
                authoringEnabled = Bool("authoringEnabled", def.authoringEnabled),
                authoringMaxRequestBodySize = Num("authoringMaxRequestBodySize", def.authoringMaxRequestBodySize),
                maxAllowedContentLength = Num("maxAllowedContentLength", def.maxAllowedContentLength),
                allowDoubleEscaping = Bool("allowDoubleEscaping", def.allowDoubleEscaping),
                verifyIntegration = Bool("verifyIntegration", def.verifyIntegration),
                anonymousEnabled = Bool("anonymousEnabled", def.anonymousEnabled),
                basicEnabled = Bool("basicEnabled", def.basicEnabled),
                windowsEnabled = Bool("windowsEnabled", def.windowsEnabled),
                maxUrlLength = IntNum("maxUrlLength", def.maxUrlLength),
                maxQueryStringLength = IntNum("maxQueryStringLength", def.maxQueryStringLength),
                // 只读字段：TS globalAuthoringRulesCount !== undefined ? Number(v) || 0 : 0
                globalAuthoringRulesCount = ConfigJson.Prop(obj, "globalAuthoringRulesCount") is null
                    ? 0
                    : ConfigJson.IntOr(obj, "globalAuthoringRulesCount", 0),
                enableStaticCompression = ConfigJson.Bool(obj, "enableStaticCompression", def.enableStaticCompression),
                enableDynamicCompression = ConfigJson.Bool(obj, "enableDynamicCompression", def.enableDynamicCompression),
                requireSSL = ConfigJson.Bool(obj, "requireSSL", def.requireSSL),
            };
        }
        catch
        {
            return DefaultConfig();
        }
    }

    /// <summary>TS: setConfig(config)——per-field try/catch，部分节锁定不阻断其他字段写入；只读字段不可写。</summary>
    public Task SetConfigAsync(WebdavServerConfig config) => SetConfigAsync(WebdavConfigPatch.FromFull(config));

    /// <summary>TS: setConfig(config: Partial&lt;WebdavServerConfig&gt;)。</summary>
    public async Task SetConfigAsync(WebdavConfigPatch config)
    {
        // 预解锁配置节
        await EnsureWebdavSectionsUnlockedAsync();
        List<string> writeParts = new();
        foreach (Field f in WebdavFields)
        {
            object? val = config[f.tsField];
            if (val is null) continue; // TS: val === undefined → 不写入
            string? psVal = f.type == FieldType.Boolean ? PsGate.PsBool((bool)val) : PsGate.PsNumber(val);
            if (psVal is null) continue; // 非法类型跳过，杜绝注入
            writeParts.Add(
                $"try {{ Set-WebConfigurationProperty -Filter '{f.filter}' -PSPath 'MACHINE/WEBROOT/APPHOST' " +
                $"-Name {f.psName} -Value {psVal} -ErrorAction Stop }} catch {{}}");
        }
        if (writeParts.Count == 0) throw Errors.InvalidParam("未提供任何配置项");
        await _ps.RunVoidAsync(string.Join("; ", writeParts), IisTimeoutMs, 0);
    }

    /// <summary>TS: restoreDefault()。</summary>
    public async Task<WebdavServerConfig> RestoreDefaultAsync()
    {
        WebdavServerConfig def = DefaultConfig();
        await SetConfigAsync(def);
        return def;
    }

    // TS: 压缩只读字段读取段（两段同构，仅 note 名不同）
    private static string ReadOnlyPart(string noteName, string psName) =>
        $"try {{ $r | Add-Member -MemberType NoteProperty -Name {noteName} -Value " +
        $"(Get-WebConfigurationProperty -Filter '{UrlCompressionFilter}' " +
        $"-PSPath 'MACHINE/WEBROOT/APPHOST' -Name {psName} -ErrorAction Stop) }} catch {{}}";

    // TS: msg.slice(0, 200)
    private static string Slice(string? s, int max) => s is null ? "" : (s.Length > max ? s[..max] : s);

    private enum FieldType { Boolean, Number }

    /// <summary>TS: WebdavConfigField。</summary>
    private readonly record struct Field(string tsField, string filter, string psName, FieldType type);
}

/// <summary>
/// TS: setConfig(config: Partial&lt;WebdavServerConfig&gt;) 的部分配置入参（未提供 = null = 不写入）。
/// 只读字段（globalAuthoringRulesCount / 压缩 / requireSSL）不可写，故不在本类型里。
/// </summary>
public sealed record WebdavConfigPatch
{
    public bool? authoringEnabled { get; init; }
    public long? authoringMaxRequestBodySize { get; init; }
    public long? maxAllowedContentLength { get; init; }
    public bool? allowDoubleEscaping { get; init; }
    public bool? verifyIntegration { get; init; }
    public bool? anonymousEnabled { get; init; }
    public bool? basicEnabled { get; init; }
    public bool? windowsEnabled { get; init; }
    public int? maxUrlLength { get; init; }
    public int? maxQueryStringLength { get; init; }

    /// <summary>按 tsField 取值（null = 未提供）。</summary>
    public object? this[string tsField] => tsField switch
    {
        "authoringEnabled" => authoringEnabled,
        "authoringMaxRequestBodySize" => authoringMaxRequestBodySize,
        "maxAllowedContentLength" => maxAllowedContentLength,
        "allowDoubleEscaping" => allowDoubleEscaping,
        "verifyIntegration" => verifyIntegration,
        "anonymousEnabled" => anonymousEnabled,
        "basicEnabled" => basicEnabled,
        "windowsEnabled" => windowsEnabled,
        "maxUrlLength" => maxUrlLength,
        "maxQueryStringLength" => maxQueryStringLength,
        _ => null,
    };

    /// <summary>完整配置 → 可写字段全提供。</summary>
    public static WebdavConfigPatch FromFull(WebdavServerConfig c) => new()
    {
        authoringEnabled = c.authoringEnabled,
        authoringMaxRequestBodySize = c.authoringMaxRequestBodySize,
        maxAllowedContentLength = c.maxAllowedContentLength,
        allowDoubleEscaping = c.allowDoubleEscaping,
        verifyIntegration = c.verifyIntegration,
        anonymousEnabled = c.anonymousEnabled,
        basicEnabled = c.basicEnabled,
        windowsEnabled = c.windowsEnabled,
        maxUrlLength = c.maxUrlLength,
        maxQueryStringLength = c.maxQueryStringLength,
    };
}
