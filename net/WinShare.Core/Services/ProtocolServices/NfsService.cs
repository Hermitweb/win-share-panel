// 移植自 electron/services/nfs.ts（NFS 服务器配置 + 服务控制）。
// 命令串逐字复制自 TS：Get-/Set-NfsServerConfiguration、Get-Service、Restart-/Start-/Stop-Service；
// 执行经 IPsInvoker（默认 PsRunner.Default）：查询 15000ms/2 次重试，
// 未装 NFS 时必然失败的探测用 retries:0（TS 注释：避免切 Tab 延迟）。
// 默认值语义见 Contracts/NfsTypes.cs 与 TS defaultConfig()。
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Shares; // PsGate：服务域 PS 参数闸门窄门面（与 TS 调用点同名）

namespace WinShare.Core.Services.ProtocolServices;

/// <summary>NFS 服务器配置与服务控制（TS: electron/services/nfs.ts）。</summary>
public sealed class NfsService : INfsService
{
    // TS: getNfsServiceName —— 一次查询两个候选名（Windows Server 为 NfsService，Win10/11 客户端为 NfsClnt）
    private const string NfsServiceNameCommand =
        "@('NfsService','NfsClnt') | ForEach-Object { Get-Service -Name $_ -ErrorAction SilentlyContinue } | " +
        "Where-Object { $_ } | Select-Object -First 1 -ExpandProperty Name";

    // TS: restartService —— /access is denied|拒绝访问|administrator|权限/i
    private static readonly Regex AccessDenied = new(
        @"access is denied|拒绝访问|administrator|权限", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IPsInvoker _ps;

    public NfsService(IPsInvoker? ps = null) => _ps = ps ?? PsRunner.Default;

    /// <summary>TS: defaultConfig()——协议未装或查询失败时返回，让 Settings 表单可渲染。</summary>
    public NfsServerConfig DefaultConfig() => new()
    {
        gracefulUnmount = false,
        logActivity = false,
        enableUnmappedAccess = false,
        enableAuthenticationRenegotiation = false,
        gatewayCharacterSet = "ANSI",
        protocolVersion = "4.1",
        // 连接与超时
        tcpConnectionTimeout = 240,
        udpConnectionTimeout = 240,
        restartConnectionTimeout = 60,
        maxConcurrentConnectionsPerUser = 0,
        directoryCacheExpiry = 60,
        // 身份映射（只读）
        anonymousUid = -2,
        anonymousGid = -2,
    };

    /// <summary>TS: getConfig()——未装 NFS 时返回默认配置，避免 Settings Tab 弹错。</summary>
    public async Task<NfsServerConfig> GetConfigAsync()
    {
        try
        {
            JsonElement raw = await _ps.RunJsonAsync("Get-NfsServerConfiguration", 15000, 0);
            JsonElement r = ConfigJson.Obj(raw);
            NfsServerConfig def = DefaultConfig();
            return new NfsServerConfig
            {
                gracefulUnmount = ConfigJson.Truthy(r, "GracefulUnmount"),
                logActivity = ConfigJson.Truthy(r, "LogActivity"),
                enableUnmappedAccess = ConfigJson.Truthy(r, "EnableUnmappedAccess"),
                enableAuthenticationRenegotiation = ConfigJson.Truthy(r, "EnableAuthenticationRenegotiation"),
                // TS: raw.GatewayCharacterSet || 'ANSI'
                gatewayCharacterSet = NonEmpty(ConfigJson.Str(r, "GatewayCharacterSet")) ?? def.gatewayCharacterSet,
                protocolVersion = NonEmpty(ConfigJson.Str(r, "ProtocolVersion")) ?? def.protocolVersion,
                // 连接与超时（best-effort：旧版 Windows 可能无此字段，降级为默认值）
                tcpConnectionTimeout = ConfigJson.Int(r, "TcpConnectionTimeout", def.tcpConnectionTimeout),
                udpConnectionTimeout = ConfigJson.Int(r, "UdpConnectionTimeout", def.udpConnectionTimeout),
                restartConnectionTimeout = ConfigJson.Int(r, "RestartConnectionTimeout", def.restartConnectionTimeout),
                maxConcurrentConnectionsPerUser = ConfigJson.Int(r, "MaxConcurrentConnectionsPerUser", def.maxConcurrentConnectionsPerUser),
                directoryCacheExpiry = ConfigJson.Int(r, "DirectoryCacheExpiry", def.directoryCacheExpiry),
                // 身份映射（只读）
                anonymousUid = ConfigJson.Int(r, "AnonymousUid", def.anonymousUid),
                anonymousGid = ConfigJson.Int(r, "AnonymousGid", def.anonymousGid),
            };
        }
        catch
        {
            // NFS 未装：返回默认配置，避免 Settings Tab 弹错
            return DefaultConfig();
        }
    }

    /// <summary>TS: setConfig(config)——契约面收完整配置（= 全字段提供）。</summary>
    public Task SetConfigAsync(NfsServerConfig config) => SetConfigAsync(NfsConfigPatch.FromFull(config));

    /// <summary>
    /// TS: setConfig(config: Partial&lt;NfsServerConfig&gt;)——刻意不写回 gatewayCharacterSet /
    /// protocolVersion / anonymousUid / anonymousGid：这些是服务器只读状态（Set-NfsServerConfiguration
    /// 亦不接受这些参数）。静默跳过是正确语义而非遗漏（TS 审计 M-1 复核结论）。
    /// </summary>
    public async Task SetConfigAsync(NfsConfigPatch config)
    {
        List<string> parts = new() { "Set-NfsServerConfiguration" };
        // 布尔字段：运行时类型校验（非法值 psBool 返回 null → 跳过，杜绝注入）
        AddBool(parts, "-GracefulUnmount", config.gracefulUnmount);
        AddBool(parts, "-LogActivity", config.logActivity);
        AddBool(parts, "-EnableUnmappedAccess", config.enableUnmappedAccess);
        AddBool(parts, "-EnableAuthenticationRenegotiation", config.enableAuthenticationRenegotiation);
        // 数值字段：同上门禁（TS 侧 '0' 为 truthy，故 0 也会写入）
        AddNumber(parts, "-TcpConnectionTimeout", config.tcpConnectionTimeout);
        AddNumber(parts, "-UdpConnectionTimeout", config.udpConnectionTimeout);
        AddNumber(parts, "-RestartConnectionTimeout", config.restartConnectionTimeout);
        AddNumber(parts, "-MaxConcurrentConnectionsPerUser", config.maxConcurrentConnectionsPerUser);
        AddNumber(parts, "-DirectoryCacheExpiry", config.directoryCacheExpiry);
        parts.Add("-Confirm:$false");
        if (parts.Count <= 2) throw Errors.InvalidParam("未提供任何配置项");
        await _ps.RunVoidAsync(string.Join(' ', parts), 15000, PsRunner.DefaultRetries);
    }

    /// <summary>TS: restoreDefault()。</summary>
    public async Task<NfsServerConfig> RestoreDefaultAsync()
    {
        NfsServerConfig def = DefaultConfig();
        await SetConfigAsync(def);
        return def;
    }

    /// <summary>TS: getNfsServiceName()——一次查询两个候选名，返回存在的那个（优先 NfsService）。</summary>
    private async Task<string?> GetNfsServiceNameAsync()
    {
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(NfsServiceNameCommand, 15000, 0);
            // TS: raw ? String(raw).trim() : null —— 数组经 String() 拼逗号，空数组归一为无服务
            string text = raw.ValueKind switch
            {
                JsonValueKind.Array => string.Join(",",
                    raw.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : "")),
                JsonValueKind.String => raw.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => raw.GetRawText(),
            };
            text = text.Trim();
            return text.Length > 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>TS: getServiceStatus()。</summary>
    public async Task<ServiceStatus> GetServiceStatusAsync()
    {
        string? name = await GetNfsServiceNameAsync();
        if (name is null)
        {
            return new ServiceStatus { name = "NFS", status = ServiceState.Unknown, startType = "Unknown" };
        }
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(
                $"Get-Service {name} -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType",
                15000, PsRunner.DefaultRetries);
            return ServiceStatusReader.Read(raw, name);
        }
        catch
        {
            return new ServiceStatus { name = name, status = ServiceState.Unknown, startType = "Unknown" };
        }
    }

    /// <summary>TS: restartService()。</summary>
    public async Task RestartServiceAsync()
    {
        string? name = await GetNfsServiceNameAsync();
        if (name is null)
        {
            throw Errors.CommandFailed("NFS 服务未安装。请在「共享管理」页 NFS Tab 按引导安装 NFS 角色后重试");
        }
        try
        {
            await _ps.RunVoidAsync($"Restart-Service -Name {name} -Force -ErrorAction Stop", 15000, 0);
        }
        catch (Exception e)
        {
            string msg = e.Message ?? "";
            if (AccessDenied.IsMatch(msg))
            {
                throw Errors.CommandFailed("重启 NFS 服务需要管理员权限，请以管理员身份运行 WinShare Panel");
            }
            throw Errors.CommandFailed($"重启 NFS 服务失败：{Slice(msg, 200)}");
        }
    }

    /// <summary>TS: startService()。</summary>
    public async Task StartServiceAsync()
    {
        string? name = await GetNfsServiceNameAsync();
        if (name is null)
        {
            throw Errors.CommandFailed("NFS 服务未安装，请先安装 NFS 角色");
        }
        try
        {
            await _ps.RunVoidAsync($"Start-Service -Name {name} -ErrorAction Stop", 15000, 0);
        }
        catch (Exception e)
        {
            throw Errors.CommandFailed($"启动 NFS 服务失败：{Slice(e.Message, 200)}");
        }
    }

    /// <summary>TS: stopService()。</summary>
    public async Task StopServiceAsync()
    {
        string? name = await GetNfsServiceNameAsync();
        if (name is null)
        {
            throw Errors.CommandFailed("NFS 服务未安装，请先安装 NFS 角色");
        }
        try
        {
            await _ps.RunVoidAsync($"Stop-Service -Name {name} -Force -ErrorAction Stop", 15000, 0);
        }
        catch (Exception e)
        {
            throw Errors.CommandFailed($"停止 NFS 服务失败：{Slice(e.Message, 200)}");
        }
    }

    // === 命令拼接助手（TS: parts.push(`-${name} ${pretty}`) 的闸门版）===

    private static void AddBool(List<string> parts, string flag, bool? value)
    {
        if (PsGate.PsBool(value) is string ps) parts.Add($"{flag} {ps}");
    }

    private static void AddNumber(List<string> parts, string flag, int? value)
    {
        if (value is null) return;
        if (PsGate.PsNumber(value) is string ps) parts.Add($"{flag} {ps}");
    }

    // TS: raw.X || 默认值 —— 仅空串 / null 回落（'ANSI' 等非空值原样保留）
    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // TS: msg.slice(0, 200)
    private static string Slice(string? s, int max) => s is null ? "" : (s.Length > max ? s[..max] : s);
}

/// <summary>
/// TS: setConfig(config: Partial&lt;NfsServerConfig&gt;) 的部分配置入参（未提供 = null = 不写入）。
/// 契约面 <see cref="INfsService.SetConfigAsync(NfsServerConfig)"/> 收完整配置，内部经 <see cref="FromFull"/> 转换。
/// </summary>
public sealed record NfsConfigPatch
{
    public bool? gracefulUnmount { get; init; }
    public bool? logActivity { get; init; }
    public bool? enableUnmappedAccess { get; init; }
    public bool? enableAuthenticationRenegotiation { get; init; }
    public int? tcpConnectionTimeout { get; init; }
    public int? udpConnectionTimeout { get; init; }
    public int? restartConnectionTimeout { get; init; }
    public int? maxConcurrentConnectionsPerUser { get; init; }
    public int? directoryCacheExpiry { get; init; }

    /// <summary>完整配置 → 全字段提供（只读字段天然不在补丁里）。</summary>
    public static NfsConfigPatch FromFull(NfsServerConfig config) => new()
    {
        gracefulUnmount = config.gracefulUnmount,
        logActivity = config.logActivity,
        enableUnmappedAccess = config.enableUnmappedAccess,
        enableAuthenticationRenegotiation = config.enableAuthenticationRenegotiation,
        tcpConnectionTimeout = config.tcpConnectionTimeout,
        udpConnectionTimeout = config.udpConnectionTimeout,
        restartConnectionTimeout = config.restartConnectionTimeout,
        maxConcurrentConnectionsPerUser = config.maxConcurrentConnectionsPerUser,
        directoryCacheExpiry = config.directoryCacheExpiry,
    };
}
