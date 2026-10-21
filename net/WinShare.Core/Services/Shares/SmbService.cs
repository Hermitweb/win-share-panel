// 移植自 electron/services/smb.ts（SMB 服务器配置与服务控制：
// defaultConfig / getConfig / setConfig / restoreDefault / listSnapshots / rollbackSnapshot /
// getServiceStatus / restartService / startService / stopService）。
// 命令串从 TS 原文件 1:1 复制；FIELD_MAP 版本适配过滤、setConfig 串行化锁、写前快照、
// 快照 ID 路径穿越防护全部保留语义。
//
// TS setConfig(config: Partial<SmbServerConfig>) 的 patch 语义：字段"出现在入参里"才下发，
// 显式 false / 0 / 'Off' 与"未提供"必须可区分——C# 契约侧 SetConfigAsync 收完整 SmbServerConfig
// （视为全字段提供），同时公开 <see cref="SmbConfigPatch"/>（全可空字段）承载部分更新语义。
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Ps;

namespace WinShare.Core.Services.Shares;

/// <summary>
/// SMB 服务器配置补丁（TS: setConfig 入参 Partial&lt;SmbServerConfig&gt;）。
/// 每个字段非 null 才下发（对应 TS 的 `config[k] !== undefined`），显式 false/0/'Off' 照常下发。
/// </summary>
public sealed record SmbConfigPatch
{
    public bool? enableSMB1Protocol { get; init; }
    public bool? enableSMB2Protocol { get; init; }
    public bool? enableSMB3Protocol { get; init; }
    public bool? enableGuestUserAccess { get; init; }
    public bool? enableInsecureGuestLogons { get; init; }
    public bool? auditSmb1Access { get; init; }
    public bool? requireSecuritySignature { get; init; }
    public bool? enableMultiChannel { get; init; }
    public bool? announceServer { get; init; }
    public int? unauthenticatedUsersTimeLimit { get; init; }
    public bool? enableOplocks { get; init; }
    public bool? enableOplockDirectoryCache { get; init; }
    public bool? enableStrictNameChecking { get; init; }
    public bool? enableLeasing { get; init; }
    public bool? enableSMBQUIC { get; init; }
    public bool? enableChannelChange { get; init; }
    public bool? enableSMBDirectoryCache { get; init; }
    public int? sessionTimeoutSeconds { get; init; }
    public int? maxSessionPerConnection { get; init; }
    public int? maxMpxCount { get; init; }
    public int? maxWorkItems { get; init; }
    public int? maxThreadsPerQueue { get; init; }
    public bool? multipleSessionsPerConnection { get; init; }
    public RequestCompression? requestCompression { get; init; }
    public bool? silentAU { get; init; }

    /// <summary>完整配置 → 全字段补丁（TS: setConfig(defaultConfig())）。</summary>
    public static SmbConfigPatch FromFull(SmbServerConfig c) => new()
    {
        enableSMB1Protocol = c.enableSMB1Protocol,
        enableSMB2Protocol = c.enableSMB2Protocol,
        enableSMB3Protocol = c.enableSMB3Protocol,
        enableGuestUserAccess = c.enableGuestUserAccess,
        enableInsecureGuestLogons = c.enableInsecureGuestLogons,
        auditSmb1Access = c.auditSmb1Access,
        requireSecuritySignature = c.requireSecuritySignature,
        enableMultiChannel = c.enableMultiChannel,
        announceServer = c.announceServer,
        unauthenticatedUsersTimeLimit = c.unauthenticatedUsersTimeLimit,
        enableOplocks = c.enableOplocks,
        enableOplockDirectoryCache = c.enableOplockDirectoryCache,
        enableStrictNameChecking = c.enableStrictNameChecking,
        enableLeasing = c.enableLeasing,
        enableSMBQUIC = c.enableSMBQUIC,
        enableChannelChange = c.enableChannelChange,
        enableSMBDirectoryCache = c.enableSMBDirectoryCache,
        sessionTimeoutSeconds = c.sessionTimeoutSeconds,
        maxSessionPerConnection = c.maxSessionPerConnection,
        maxMpxCount = c.maxMpxCount,
        maxWorkItems = c.maxWorkItems,
        maxThreadsPerQueue = c.maxThreadsPerQueue,
        multipleSessionsPerConnection = c.multipleSessionsPerConnection,
        requestCompression = c.requestCompression,
        silentAU = c.silentAU,
    };
}

/// <summary><see cref="ISmbService"/> 实现（TS: electron/services/smb.ts）。</summary>
public sealed class SmbService : ISmbService
{
    /// <summary>TS: MAX_SNAPSHOTS = 20。</summary>
    private const int MaxSnapshots = 20;

    /// <summary>
    /// TS: FIELD_MAP —— TS 字段 → PowerShell 参数名（布尔字段映射，按 TS Object.keys 顺序遍历）。
    /// </summary>
    private static readonly (string Key, string PsName)[] FieldMap = new[]
    {
        ("enableSMB1Protocol", "EnableSMB1Protocol"),
        ("enableSMB2Protocol", "EnableSMB2Protocol"),
        ("enableSMB3Protocol", "EnableSMB3Protocol"),
        ("enableGuestUserAccess", "EnableGuestUserAccess"),
        ("enableInsecureGuestLogons", "EnableInsecureGuestLogons"),
        ("auditSmb1Access", "AuditSmb1Access"),
        ("requireSecuritySignature", "RequireSecuritySignature"),
        ("enableMultiChannel", "EnableMultiChannel"),
        ("announceServer", "AnnounceServer"),
        ("enableOplocks", "EnableOplocks"),
        ("enableOplockDirectoryCache", "EnableOplockDirectoryCache"),
        ("enableStrictNameChecking", "EnableStrictNameChecking"),
        ("enableLeasing", "EnableLeasing"),
        ("enableSMBQUIC", "EnableSMBQUIC"),
        ("enableChannelChange", "EnableChannelChange"),
        ("enableSMBDirectoryCache", "EnableSMBDirectoryCache"),
        ("multipleSessionsPerConnection", "MultipleSessionsPerConnection"),
        ("silentAU", "SilentAU"),
    };

    /// <summary>TS: COMPRESSION_OPTIONS —— RequestCompression 合法枚举值。</summary>
    private static readonly HashSet<string> CompressionOptions = new()
    {
        "Off", "OptimizeForSpeed", "OptimizeForSize",
    };

    // TS: rollbackSnapshot ID 校验 —— /^[A-Za-z0-9_-]+$/（防 ../ 路径穿越）
    private static readonly Regex SnapshotIdRegex = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    private readonly IPsInvoker _ps;

    // TS: skipNextSnapshot —— 防止 rollback→setConfig 递归快照
    private bool _skipNextSnapshot;

    // TS: setConfigChain —— setConfig 串行化锁（防止并发 setConfig 互相覆盖 skipNextSnapshot 标志）
    private Task _setConfigChain = Task.CompletedTask;

    public SmbService(IPsInvoker? ps = null) => _ps = ps ?? PsRunner.Default;

    /// <summary>TS: defaultConfig()——基于 Windows Server 2022 安全推荐默认值。</summary>
    public SmbServerConfig DefaultConfig() => new()
    {
        enableSMB1Protocol = false, // 出于安全考虑默认关闭 SMB1
        enableSMB2Protocol = true,
        enableSMB3Protocol = true,
        enableGuestUserAccess = false,
        enableInsecureGuestLogons = false,
        auditSmb1Access = true,
        requireSecuritySignature = true, // 默认要求签名
        enableMultiChannel = true,
        announceServer = false,
        unauthenticatedUsersTimeLimit = 0,
        // 扩展默认值
        enableOplocks = true,
        enableOplockDirectoryCache = true,
        enableStrictNameChecking = true,
        enableLeasing = true,
        enableSMBQUIC = false,
        enableChannelChange = true,
        enableSMBDirectoryCache = true,
        sessionTimeoutSeconds = 60,
        maxSessionPerConnection = 1024,
        maxMpxCount = 1000,
        maxWorkItems = 1000,
        maxThreadsPerQueue = 100,
        multipleSessionsPerConnection = false,
        requestCompression = RequestCompression.Off,
        silentAU = false,
    };

    /// <summary>TS: getConfig()。</summary>
    public async Task<SmbServerConfig> GetConfigAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync("Get-SmbServerConfiguration", 15000, 2);
        return new SmbServerConfig
        {
            enableSMB1Protocol = B(raw, "EnableSMB1Protocol"),
            enableSMB2Protocol = B(raw, "EnableSMB2Protocol"),
            enableSMB3Protocol = B(raw, "EnableSMB3Protocol"),
            enableGuestUserAccess = B(raw, "EnableGuestUserAccess"),
            enableInsecureGuestLogons = B(raw, "EnableInsecureGuestLogons"),
            auditSmb1Access = B(raw, "AuditSmb1Access"),
            requireSecuritySignature = B(raw, "RequireSecuritySignature"),
            enableMultiChannel = B(raw, "EnableMultiChannel"),
            announceServer = B(raw, "AnnounceServer"),
            unauthenticatedUsersTimeLimit = N(raw, "UnauthenticatedUsersTimeLimit", 0),
            // 扩展字段（某些旧版 Windows 可能无这些字段，降级为默认值）
            enableOplocks = B(raw, "EnableOplocks", true),
            enableOplockDirectoryCache = B(raw, "EnableOplockDirectoryCache", true),
            enableStrictNameChecking = B(raw, "EnableStrictNameChecking", true),
            enableLeasing = B(raw, "EnableLeasing", true),
            enableSMBQUIC = B(raw, "EnableSMBQUIC", false),
            enableChannelChange = B(raw, "EnableChannelChange", true),
            enableSMBDirectoryCache = B(raw, "EnableSMBDirectoryCache", true),
            sessionTimeoutSeconds = N(raw, "SessionTimeoutSeconds", 60),
            maxSessionPerConnection = N(raw, "MaxSessionPerConnection", 1024),
            maxMpxCount = N(raw, "MaxMpxCount", 1000),
            maxWorkItems = N(raw, "MaxWorkItems", 1000),
            maxThreadsPerQueue = N(raw, "MaxThreadsPerQueue", 100),
            multipleSessionsPerConnection = B(raw, "MultipleSessionsPerConnection"),
            requestCompression = Rc(raw, "RequestCompression"),
            silentAU = B(raw, "SilentAU"),
        };
    }

    /// <summary>TS: setConfig(config)——契约面收完整配置（= 全字段提供）。</summary>
    public Task SetConfigAsync(SmbServerConfig config) => SetConfigAsync(SmbConfigPatch.FromFull(config));

    /// <summary>
    /// TS: setConfig(config: Partial&lt;SmbServerConfig&gt;)——写前快照（rollback 触发时跳过）+
    /// OS 版本适配过滤 + 串行化锁。
    /// </summary>
    public async Task SetConfigAsync(SmbConfigPatch config)
    {
        // 串行化：将整次 setConfig（含快照+写入）排入链式队列，避免并发竞态
        Task next = _setConfigChain.ContinueWith(_ => RunAsync(config), TaskContinuationOptions.OnlyOnRanToCompletion)
            .Unwrap();
        // 保持链不中断：即使本次失败，下一次仍可执行
        _setConfigChain = next.ContinueWith(_ => Task.CompletedTask, TaskContinuationOptions.OnlyOnRanToCompletion);
        await next;
    }

    private async Task RunAsync(SmbConfigPatch config)
    {
        // 写前快照当前完整配置（rollback 触发的 setConfig 跳过；restoreDefault 同样经由本链，
        // 会正常产生"改前"快照——语义正确，M-2 留档）
        if (!_skipNextSnapshot)
        {
            try
            {
                SmbServerConfig current = await GetConfigAsync();
                string ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                string id = TsToFilename(ts);
                SmbSnapshot snap = new() { id = id, ts = ts, config = current };
                File.WriteAllText(
                    Path.Combine(ShareData.SnapshotDir(), $"{id}.json"),
                    JsonSerializer.Serialize(snap, JsonOpts.Indented));
                CleanupSnapshots();
            }
            catch
            {
                // 快照失败不阻塞写入
            }
        }
        _skipNextSnapshot = false;

        // 版本适配：读取本机 SMB 服务器配置的实际属性集（旧系统缺少 QUIC/SilentAU 等），
        // 下发前据此过滤；探测失败则 supported=null 回退全量下发（错误照实上抛）
        HashSet<string>? supported = null;
        try
        {
            JsonElement raw = await _ps.RunJsonAsync("Get-SmbServerConfiguration", 15000, 2);
            supported = new HashSet<string>(raw.ValueKind == JsonValueKind.Object
                ? raw.EnumerateObject().Select(p => p.Name)
                : Enumerable.Empty<string>());
        }
        catch
        {
            supported = null;
        }
        string cmd = BuildSetCmd(config, supported);
        if (cmd.Split(' ').Length <= 2)
        {
            throw Errors.InvalidParam("未提供任何配置项（本机可能不支持所改字段）");
        }
        await _ps.RunVoidAsync(cmd, 15000, 2);
    }

    /// <summary>
    /// TS: buildSetCmd(config, supported)——布尔字段映射 + 版本适配过滤 + 数值/枚举字段。
    /// </summary>
    internal static string BuildSetCmd(SmbConfigPatch config, HashSet<string>? supported)
    {
        List<string> skipped = new();
        bool Can(string psName)
        {
            if (supported is null || supported.Contains(psName)) return true;
            skipped.Add(psName);
            return false;
        }
        List<string> parts = new() { "Set-SmbServerConfiguration" };
        // 布尔/数值字段映射（运行时类型校验，防止渲染进程传入非法类型注入命令）
        foreach ((string key, string psName) in FieldMap)
        {
            if (PatchValue(config, key) is not bool bv0) continue; // TS: config[k] === undefined → continue
            if (!Can(psName)) continue;
            // TS: const bv = psBool(v); if (bv) { parts.push(`-${psName} ${bv}`); continue }
            parts.Add($"-{psName} {PsArgs.PsBool(bv0)}");
        }
        // 数值字段
        if (config.unauthenticatedUsersTimeLimit is int uv && Can("UnauthenticatedUsersTimeLimit"))
        {
            string? nv = PsGate.PsNumber(uv);
            if (nv is not null) parts.Add($"-UnauthenticatedUsersTimeLimit {nv}");
        }
        if (config.sessionTimeoutSeconds is int st && Can("SessionTimeoutSeconds"))
        {
            string? nv = PsGate.PsNumber(st);
            if (nv is not null) parts.Add($"-SessionTimeoutSeconds {nv}");
        }
        if (config.maxSessionPerConnection is int msp && Can("MaxSessionPerConnection"))
        {
            string? nv = PsGate.PsNumber(msp);
            if (nv is not null) parts.Add($"-MaxSessionPerConnection {nv}");
        }
        if (config.maxMpxCount is int mmc && Can("MaxMpxCount"))
        {
            string? nv = PsGate.PsNumber(mmc);
            if (nv is not null) parts.Add($"-MaxMpxCount {nv}");
        }
        if (config.maxWorkItems is int mwi && Can("MaxWorkItems"))
        {
            string? nv = PsGate.PsNumber(mwi);
            if (nv is not null) parts.Add($"-MaxWorkItems {nv}");
        }
        if (config.maxThreadsPerQueue is int mtp && Can("MaxThreadsPerQueue"))
        {
            string? nv = PsGate.PsNumber(mtp);
            if (nv is not null) parts.Add($"-MaxThreadsPerQueue {nv}");
        }
        // 枚举字段（白名单校验 + 引号包裹）
        if (config.requestCompression is RequestCompression rc && Can("RequestCompression"))
        {
            string? ev = PsGate.PsEnum(rc.ToString(), CompressionOptions);
            if (ev is not null) parts.Add($"-RequestCompression {PsGate.PsQuote(ev)}");
        }
        parts.Add("-Force");
        if (skipped.Count > 0)
        {
            Logger.Info("smb", $"版本适配：本机不支持以下配置项，已跳过：{string.Join(", ", skipped)}");
        }
        return string.Join(' ', parts);
    }

    /// <summary>TS: config[k]——按 TS 字段名取值（仅布尔字段走 FIELD_MAP 映射循环）。</summary>
    private static object? PatchValue(SmbConfigPatch c, string key) => key switch
    {
        "enableSMB1Protocol" => c.enableSMB1Protocol,
        "enableSMB2Protocol" => c.enableSMB2Protocol,
        "enableSMB3Protocol" => c.enableSMB3Protocol,
        "enableGuestUserAccess" => c.enableGuestUserAccess,
        "enableInsecureGuestLogons" => c.enableInsecureGuestLogons,
        "auditSmb1Access" => c.auditSmb1Access,
        "requireSecuritySignature" => c.requireSecuritySignature,
        "enableMultiChannel" => c.enableMultiChannel,
        "announceServer" => c.announceServer,
        "enableOplocks" => c.enableOplocks,
        "enableOplockDirectoryCache" => c.enableOplockDirectoryCache,
        "enableStrictNameChecking" => c.enableStrictNameChecking,
        "enableLeasing" => c.enableLeasing,
        "enableSMBQUIC" => c.enableSMBQUIC,
        "enableChannelChange" => c.enableChannelChange,
        "enableSMBDirectoryCache" => c.enableSMBDirectoryCache,
        "multipleSessionsPerConnection" => c.multipleSessionsPerConnection,
        "silentAU" => c.silentAU,
        _ => null,
    };

    /// <summary>TS: restoreDefault()。</summary>
    public async Task<SmbServerConfig> RestoreDefaultAsync()
    {
        SmbServerConfig def = DefaultConfig();
        await SetConfigAsync(def);
        return def;
    }

    /// <summary>TS: listSnapshots()。</summary>
    public async Task<IReadOnlyList<SmbSnapshotMeta>> ListSnapshotsAsync()
    {
        try
        {
            string[] files = Directory.GetFiles(ShareData.SnapshotDir())
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            List<SmbSnapshotMeta> list = files.Select(f =>
            {
                string id = Path.GetFileNameWithoutExtension(f);
                return new SmbSnapshotMeta { id = id, ts = id };
            }).ToList();
            // TS: .sort((a, b) => b.ts.localeCompare(a.ts))
            list.Sort((a, b) => string.CompareOrdinal(b.ts, a.ts));
            return list;
        }
        catch
        {
            return Array.Empty<SmbSnapshotMeta>();
        }
    }

    /// <summary>
    /// TS: rollbackSnapshot(id)——安全校验：id 必须为 ISO 时间戳转换的文件名格式，
    /// 防止 ../ 路径穿越读取上级目录任意 .json 并执行其中 config。
    /// </summary>
    public async Task RollbackSnapshotAsync(string id)
    {
        if (id is null || !SnapshotIdRegex.IsMatch(id)) throw Errors.InvalidParam("快照 ID 格式非法");
        string file = Path.Combine(ShareData.SnapshotDir(), $"{id}.json");
        if (!File.Exists(file)) throw Errors.InvalidParam("快照不存在");
        SmbSnapshot? snap;
        try
        {
            snap = JsonSerializer.Deserialize<SmbSnapshot>(File.ReadAllText(file));
        }
        catch (JsonException)
        {
            throw Errors.InvalidParam("快照文件损坏，无法解析");
        }
        // 二次校验：确保 snap 是合法快照对象
        if (snap is null || string.IsNullOrEmpty(snap.ts) || snap.config is null)
        {
            throw Errors.InvalidParam("快照内容不合法");
        }
        // 防止 setConfig 再次存快照；finally 重置以保证后续正常写入仍会快照
        _skipNextSnapshot = true;
        try
        {
            await SetConfigAsync(SmbConfigPatch.FromFull(snap.config));
        }
        finally
        {
            _skipNextSnapshot = false;
        }
    }

    /// <summary>TS: getServiceStatus()——Select-Object 精简输出，避免序列化整个依赖服务树。</summary>
    public async Task<ServiceStatus> GetServiceStatusAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync(
            "Get-Service LanmanServer | Select-Object Name, Status, StartType", 15000, 2);
        JsonElement r = PsGate.FirstOrNull(raw) ?? raw;
        // ConvertTo-Json 将 ServiceControllerStatus 枚举序列化为数值：
        //   1=Stopped, 2=StartPending, 3=StopPending, 4=Running, 5=ContinuePending, 6=PausePending, 7=Paused
        // ServiceStartMode 枚举：0=Boot, 1=System, 2=Automatic, 3=Manual, 4=Disabled
        string statusStr = Str(r, "Status") ?? "";
        int statusNum = Int(r, "Status");
        ServiceState status;
        if (statusStr == "Running" || statusNum == 4) status = ServiceState.Running;
        else if (statusStr == "Stopped" || statusNum == 1) status = ServiceState.Stopped;
        else if (statusStr == "Paused" || statusNum == 7) status = ServiceState.Stopped; // Paused 视为非运行
        else status = ServiceState.Unknown;

        int startNum = Int(r, "StartType");
        // TS: let startType = typeof raw.StartType === 'string' ? raw.StartType : ''
        string startType = Str(r, "StartType") ?? "";
        if (startType.Length == 0)
        {
            if (startNum == 2) startType = "Automatic";
            else if (startNum == 3) startType = "Manual";
            else if (startNum == 4) startType = "Disabled";
            else if (startNum == 0) startType = "Boot";
            else if (startNum == 1) startType = "System";
        }
        return new ServiceStatus { name = Str(r, "Name") ?? "LanmanServer", status = status, startType = startType };
    }

    /// <summary>TS: restartService()。</summary>
    public Task RestartServiceAsync() => _ps.RunVoidAsync("Restart-Service -Name LanmanServer -Force", 15000, 2);

    /// <summary>TS: startService()。</summary>
    public Task StartServiceAsync() => _ps.RunVoidAsync("Start-Service -Name LanmanServer", 15000, 2);

    /// <summary>TS: stopService()。</summary>
    public Task StopServiceAsync() => _ps.RunVoidAsync("Stop-Service -Name LanmanServer -Force", 15000, 2);

    // === TS: tsToFilename —— ISO 时间戳作文件名需替换 : 为 -（Windows 不允许 :） ===
    internal static string TsToFilename(string ts) => Regex.Replace(ts, "[:.]", "-");

    /// <summary>TS: cleanupSnapshots()——超量删除最旧快照。</summary>
    private static void CleanupSnapshots()
    {
        try
        {
            string[] files = Directory.GetFiles(ShareData.SnapshotDir())
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal) // ts desc
                .ToArray();
            foreach (string f in files.Skip(MaxSnapshots))
            {
                try { File.Delete(f); } catch { /* 忽略删除失败 */ }
            }
        }
        catch
        {
            // 忽略
        }
    }

    // === JSON 取值助手（TS 的 raw.X || 默认 语义） ===

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string prop)
    {
        if (e.TryGetProperty(prop, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int sn)) return sn;
        }
        return 0;
    }

    /// <summary>TS: raw.X !== undefined ? !!raw.X : 默认值。</summary>
    private static bool B(JsonElement e, string prop, bool fallback = false) =>
        e.TryGetProperty(prop, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    /// <summary>TS: raw.X || 默认 —— 0/缺失回落默认值。</summary>
    private static int N(JsonElement e, string prop, int fallback)
    {
        int n = Int(e, prop);
        return n != 0 ? n : fallback;
    }

    /// <summary>TS: raw.RequestCompression || 'Off'。</summary>
    private static RequestCompression Rc(JsonElement e, string prop) =>
        Str(e, prop) is string s && Enum.TryParse(s, ignoreCase: true, out RequestCompression rc)
            ? rc
            : RequestCompression.Off;
}
