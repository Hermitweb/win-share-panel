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
        // 身份映射（WMI 直写）
        anonymousUid = -2,
        anonymousGid = -2,
        // 协议版本开关（探测失败回落值；真实值以 Get-NfsServerConfiguration 为准）
        enableNfsV2 = false,
        enableNfsV3 = true,
        enableNfsV4 = false,
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
                // 身份映射（WMI 直写）
                anonymousUid = ConfigJson.Int(r, "AnonymousUid", def.anonymousUid),
                anonymousGid = ConfigJson.Int(r, "AnonymousGid", def.anonymousGid),
                // 协议版本开关（best-effort：缺失字段回落默认）
                enableNfsV2 = ConfigJson.Bool(r, "EnableNFSV2", def.enableNfsV2),
                enableNfsV3 = ConfigJson.Bool(r, "EnableNFSV3", def.enableNfsV3),
                enableNfsV4 = ConfigJson.Bool(r, "EnableNFSV4", def.enableNfsV4),
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
    /// TS: setConfig(config: Partial&lt;NfsServerConfig&gt;)。
    /// 协议版本开关走 Set-NfsServerConfiguration（-EnableNFSV2/-V3/-V4）；
    /// 身份映射三字段（anonymousUid/Gid/gatewayCharacterSet）Set-NfsServerConfiguration 不接受，
    /// 改经 MSFT_NfsServerConfig WMI 直写：与现值相同则跳过，提供程序拒绝时抛明确错误（不静默）。
    /// </summary>
    public async Task SetConfigAsync(NfsConfigPatch config)
    {
        List<string> parts = new() { "Set-NfsServerConfiguration" };
        // 布尔字段：运行时类型校验（非法值 psBool 返回 null → 跳过，杜绝注入）
        AddBool(parts, "-GracefulUnmount", config.gracefulUnmount);
        AddBool(parts, "-LogActivity", config.logActivity);
        AddBool(parts, "-EnableUnmappedAccess", config.enableUnmappedAccess);
        AddBool(parts, "-EnableAuthenticationRenegotiation", config.enableAuthenticationRenegotiation);
        AddBool(parts, "-EnableNFSV2", config.enableNfsV2);
        AddBool(parts, "-EnableNFSV3", config.enableNfsV3);
        AddBool(parts, "-EnableNFSV4", config.enableNfsV4);
        // 数值字段：同上门禁（TS 侧 '0' 为 truthy，故 0 也会写入）
        AddNumber(parts, "-TcpConnectionTimeout", config.tcpConnectionTimeout);
        AddNumber(parts, "-UdpConnectionTimeout", config.udpConnectionTimeout);
        AddNumber(parts, "-RestartConnectionTimeout", config.restartConnectionTimeout);
        AddNumber(parts, "-MaxConcurrentConnectionsPerUser", config.maxConcurrentConnectionsPerUser);
        AddNumber(parts, "-DirectoryCacheExpiry", config.directoryCacheExpiry);
        parts.Add("-Confirm:$false");
        bool hasIdentity = config.anonymousUid is not null || config.anonymousGid is not null
            || !string.IsNullOrEmpty(config.gatewayCharacterSet);
        if (parts.Count <= 2 && !hasIdentity) throw Errors.InvalidParam("未提供任何配置项");
        if (parts.Count > 2)
        {
            await _ps.RunVoidAsync(string.Join(' ', parts), 15000, PsRunner.DefaultRetries);
        }
        // 身份映射/网关：与现值 diff 后 WMI 直写（全量保存未改动时为 no-op，不因提供程序差异阻断保存）
        if (hasIdentity)
        {
            await WriteIdentityAsync(config);
        }
    }

    /// <summary>TS: restoreDefault()。</summary>
    public async Task<NfsServerConfig> RestoreDefaultAsync()
    {
        NfsServerConfig def = DefaultConfig();
        await SetConfigAsync(def);
        return def;
    }

    // === 身份映射 WMI 直写（匿名 UID/GID、网关字符集）===

    /// <summary>
    /// 与服务器现值逐字段 diff，仅对发生变化的字段经 Set-CimInstance 写入并回读校验。
    /// 未提供（null）/ 未变化 → 不触发 WMI；写入失败或回读不一致 → 明确报错（不静默吞掉）。
    /// </summary>
    private async Task WriteIdentityAsync(NfsConfigPatch config)
    {
        bool hasUid = config.anonymousUid is not null;
        bool hasGid = config.anonymousGid is not null;
        bool hasCharset = !string.IsNullOrEmpty(config.gatewayCharacterSet);
        if (!hasUid && !hasGid && !hasCharset) return;

        NfsServerConfig current = await GetConfigAsync();
        List<string> props = new();
        if (hasUid && current.anonymousUid != config.anonymousUid) props.Add($"AnonymousUid = {UidPsValue(config.anonymousUid!.Value)}");
        if (hasGid && current.anonymousGid != config.anonymousGid) props.Add($"AnonymousGid = {UidPsValue(config.anonymousGid!.Value)}");
        string? charset = hasCharset ? config.gatewayCharacterSet!.Trim() : null;
        if (charset is not null && !string.Equals(current.gatewayCharacterSet, charset, StringComparison.OrdinalIgnoreCase))
        {
            props.Add($"GatewayCharacterSet = {PsGate.PsQuote(charset)}");
        }
        if (props.Count == 0) return; // 全部与现值一致：无需写入

        string cmd = "$p = @{" + string.Join("; ", props) + "}; " +
            "Set-CimInstance -Namespace root/Microsoft/Windows/NFS " +
            "-Query 'SELECT * FROM MSFT_NfsServerConfig' -Property $p -ErrorAction Stop";
        try
        {
            await _ps.RunVoidAsync(cmd, 15000, 0);
        }
        catch (Exception e)
        {
            throw Errors.CommandFailed(
                $"写入 NFS 身份映射失败（Windows NFS 提供程序可能不支持该字段）：{Slice(e.Message, 200)}");
        }
        await VerifyIdentityAsync(config);
    }

    /// <summary>回读校验：Get-NfsServerConfiguration 重新取值，与请求值比对（UID 兼容带符号/无符号两种表示）。</summary>
    private async Task VerifyIdentityAsync(NfsConfigPatch config)
    {
        NfsServerConfig fresh = await GetConfigAsync();
        bool UidOk(int? requested, int actual) =>
            requested is null || actual == requested || (uint)actual == (uint)requested.Value;
        bool ok = UidOk(config.anonymousUid, fresh.anonymousUid)
            && UidOk(config.anonymousGid, fresh.anonymousGid)
            && (string.IsNullOrEmpty(config.gatewayCharacterSet)
                || string.Equals(fresh.gatewayCharacterSet, config.gatewayCharacterSet!.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!ok)
        {
            throw Errors.CommandFailed("NFS 身份映射写入后回读不一致：Windows NFS 提供程序可能拒绝了该字段（写入未生效）");
        }
    }

    /// <summary>UID/GID 的 PS 字面量：负值按 UInt32 回绕表示（-2 → 4294967294），经 PsGate 数值门禁。</summary>
    private static string? UidPsValue(int v) =>
        PsGate.PsNumber(v < 0 ? v + 4294967296L : (long)v);

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

    /// <summary>修改服务启动类型（v2.1 面板解锁；NFS 服务名运行时解析，NfsService/NfsClnt 二选一）。</summary>
    public async Task SetServiceStartTypeAsync(string startType)
    {
        string? name = await GetNfsServiceNameAsync();
        await ServiceStartType.SetAsync(_ps, name, startType, "NFS");
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
/// 身份映射三字段（anonymousUid/Gid/gatewayCharacterSet）经 WMI 直写；protocolVersion 为服务器报告值不参与写入。
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
    public bool? enableNfsV2 { get; init; }
    public bool? enableNfsV3 { get; init; }
    public bool? enableNfsV4 { get; init; }
    public int? anonymousUid { get; init; }
    public int? anonymousGid { get; init; }
    public string? gatewayCharacterSet { get; init; }

    /// <summary>完整配置 → 全字段提供（protocolVersion 不参与写入）。</summary>
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
        enableNfsV2 = config.enableNfsV2,
        enableNfsV3 = config.enableNfsV3,
        enableNfsV4 = config.enableNfsV4,
        anonymousUid = config.anonymousUid,
        anonymousGid = config.anonymousGid,
        gatewayCharacterSet = config.gatewayCharacterSet,
    };
}
