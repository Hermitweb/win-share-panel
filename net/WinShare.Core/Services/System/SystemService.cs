// 移植自 electron/services/system.ts（系统信息、提权、仪表板统计、OS 版本适配探测）。
// PS 命令串从 TS 原文件逐字复制（仅模板字符串 → C# 插值 / PsArgs 引用）。
// 依赖注入点对齐 TS 的模块级 import：
//   TS `import { getServiceStatus } from './smb'`            → <see cref="ISmbService"/>
//   TS `import { adapterList, adapterSessions } from './protocol/registry'` → <see cref="IProtocolService"/>
//   TS `import { readAuditLog } from '../lib/audit'`         → <see cref="Audit.ReadAuditLog"/>
//   TS `import * from '../lib/stateStore'`                   → <see cref="IStateStore"/>
// electron 专属语义（app.isPackaged / process.execPath / app.quit）以构造参数与
// <see cref="RelaunchAsAdminAsync"/> 的宿主回调承载（WPF 侧由组合根接线）。
//
// 命名空间说明：本程序集存在 WinShare.Core.Services.Protocol 命名空间，而 Contracts 里也有
// 同名类型 WinShare.Core.Contracts.Protocol（枚举）。在 WinShare.Core.Services.Host 内按 C#
// 名称查找规则，裸 Protocol 会先命中「命名空间」而非「枚举」，故本文件统一以别名
// WsProtocol 指向契约枚举（别名优先于命名空间查找）。
using System.Diagnostics;
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Ps;
using WsProtocol = WinShare.Core.Contracts.Protocol;
using Rx = System.Text.RegularExpressions;

namespace WinShare.Core.Services.Host;

/// <summary>ISystemService 实现。</summary>
public sealed class SystemService : ISystemService
{
    // ===== 版本适配（OS matrix）SKU 映射表（TS 原表逐字复制）=====

    private static readonly Dictionary<int, string> SkuNames = new()
    {
        [2] = "Home Basic",
        [3] = "Home Premium",
        [4] = "Enterprise",
        [6] = "Business (Server)",
        [7] = "Enterprise (Server)",
        [8] = "Datacenter (Server)",
        [9] = "Web Server (Server)",
        [12] = "Storage Server",
        [28] = "Standard (Server)",
        [39] = "Datacenter (Server)",
        [51] = "Essentials (Server)",
        [61] = "Business (Server)",
        [62] = "Enterprise (Server)",
        [63] = "Datacenter (Server)",
        [101] = "Home",
        [102] = "Home (N)",
        [103] = "Home 单语言版",
        [104] = "Home (中国)",
        [105] = "Home (N) 单语言版",
        [109] = "Home",
        [110] = "Home 单语言版",
        [111] = "Education",
        [121] = "Pro Education",
        [122] = "Pro for Workstations",
        [161] = "Pro Workstations (Server)?",
        [175] = "SE 单语言版",
    };

    // 家庭系 SKU：无 IIS（FTP/WebDAV 不可用），无客户端 NFS
    // TS: const HOME_SKUS = new Set([101, 102, 103, 104, 105, 109, 110])
    private static readonly HashSet<int> HomeSkus = new() { 101, 102, 103, 104, 105, 109, 110 };

    private readonly IPsInvoker _ps;
    private readonly ISmbService _smb;
    private readonly IProtocolService _protocol;
    private readonly IStateStore _store;

    /// <summary>TS: userInfo() 的宿主注入点；null 走 Environment.UserName。</summary>
    public Func<string>? UsernameProvider { get; set; }

    /// <summary>TS: hostname() 的宿主注入点；null 走 Environment.MachineName。</summary>
    public Func<string>? HostnameProvider { get; set; }

    /// <summary>
    /// TS: relaunchAsAdmin() 的宿主动作（Start-Process -Verb RunAs 提权重启自身）。
    /// 开发模式（<see cref="IsPackaged"/> = false）直接抛错，与 TS 语义一致。
    /// </summary>
    public Func<string, Task>? RelaunchAsAdminAction { get; set; }

    /// <summary>TS: app.isPackaged —— 开发模式提权无意义（electron-vite 子进程）。</summary>
    public bool IsPackaged { get; set; }

    private OsInfo? _osInfoCache;

    public SystemService(IPsInvoker? ps = null, ISmbService? smb = null,
        IProtocolService? protocol = null, IStateStore? store = null)
    {
        _ps = ps ?? PsRunner.Default;
        _smb = smb ?? new NullSmbService();
        _protocol = protocol ?? new NullProtocolService();
        _store = store ?? StateStore.Default;
    }

    // ===== 用户与提权 =====

    /// <summary>
    /// TS: getCurrentUser() —— userInfo() + isAdmin() + hostname()。
    /// A2：附计算机名供 UI 拼 UNC 路径（\\computer\share）。
    /// </summary>
    public async Task<UserInfo> GetCurrentUserAsync()
    {
        string username = UsernameProvider?.Invoke() ?? Environment.UserName;
        bool isAdmin = await IsAdminAsync();
        return new UserInfo { username = username, isAdmin = isAdmin, computerName = Hostname() };
    }

    /// <summary>TS: isAdmin() —— WindowsPrincipal.IsInRole(Administrator)；任何失败收敛 false。</summary>
    public async Task<bool> IsAdminAsync()
    {
        try
        {
            // TS 原串：
            //   '(New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)'
            JsonElement raw = await _ps.RunJsonAsync(
                "(New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)",
                PsRunner.DefaultTimeoutMs, 0);
            // TS: return !!r —— JSON false/空数组/null 一律 false
            JsonElement? first = SystemJsCompat.FirstOrNull(raw);
            return first is { } f && f.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// TS: relaunchAsAdmin() —— 以管理员权限重新启动当前可执行文件（UAC 提权），成功后退出当前非提权实例。
    /// 开发模式下提权无意义（electron-vite 子进程），直接抛错引导用户用管理员终端启动。
    /// </summary>
    public async Task RelaunchAsAdminAsync()
    {
        if (!IsPackaged)
        {
            throw Errors.CommandFailed("开发模式不支持自动提权，请以管理员身份运行开发环境");
        }
        // TS: const exePath = process.execPath
        string exePath = Environment.ProcessPath ?? "";
        if (RelaunchAsAdminAction is null)
        {
            throw Errors.CommandFailed("宿主未提供提权重启能力");
        }
        try
        {
            // TS: `Start-Process -FilePath ${psQuote(exePath)} -Verb RunAs`（触发 UAC 提示）
            await RelaunchAsAdminAction(exePath);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception e)
        {
            // TS: `提权失败：${(e as Error).message.slice(0, 200)}`
            string msg = e.Message.Length > 200 ? e.Message[..200] : e.Message;
            throw Errors.CommandFailed($"提权失败：{msg}");
        }
    }

    // ===== 仪表板统计 =====

    /// <summary>
    /// TS: getDashboardStats() —— 所有独立查询并行执行（Promise.allSettled 语义），
    /// 任何一路失败都只影响它自己那部分统计（其余照常返回）。
    /// </summary>
    public async Task<DashboardStats> GetDashboardStatsAsync()
    {
        // M-8：按次时间戳计时，规避并发调用（挂载+F5）下 console.time 标签冲突
        var sw = Stopwatch.StartNew();

        Task<IReadOnlyList<Share>> allSharesTask = SafeAdapterListAsync();
        Task<JsonElement> smbSessionsTask = SafeJsonAsync("Get-SmbSession");
        Task<JsonElement> smbFilesTask = SafeJsonAsync("Get-SmbOpenFile");
        Task<IReadOnlyList<ProtocolSession>> nfsSessionsTask = SafeAdapterSessionsAsync();
        Task<ServiceStatus?> svcTask = SafeServiceStatusAsync();

        // TS: await Promise.allSettled([...]) —— 五项并行，互不阻塞
        await Task.WhenAll(allSharesTask, smbSessionsTask, smbFilesTask, nfsSessionsTask, svcTask);

        // TS: const allShares = allSharesResult.status === 'fulfilled' ? allSharesResult.value : []
        IReadOnlyList<Share> allShares = allSharesTask.IsCompletedSuccessfully
            ? allSharesTask.Result
            : Array.Empty<Share>();

        var byProtocol = new Dictionary<WsProtocol, ProtocolStats>
        {
            [WsProtocol.Smb] = new ProtocolStats { shares = 0, sessions = 0 },
            [WsProtocol.Nfs] = new ProtocolStats { shares = 0, sessions = 0 },
            [WsProtocol.Ftp] = new ProtocolStats { shares = 0, sessions = 0 },
            [WsProtocol.Webdav] = new ProtocolStats { shares = 0, sessions = 0 },
        };
        foreach (Share s in allShares)
        {
            // TS: if (byProtocol[s.protocol]) byProtocol[s.protocol].shares++
            if (byProtocol.TryGetValue(s.protocol, out ProtocolStats? st))
            {
                byProtocol[s.protocol] = st with { shares = st.shares + 1 };
            }
        }

        int activeSessions = 0;
        int openFiles = 0;

        // SMB 会话与打开文件
        if (smbSessionsTask.IsCompletedSuccessfully)
        {
            // TS: Array.isArray(smbSessions.value) ? smbSessions.value.length : 0
            int n = smbSessionsTask.Result.ValueKind == JsonValueKind.Array
                ? smbSessionsTask.Result.GetArrayLength()
                : 0;
            activeSessions += n;
            byProtocol[WsProtocol.Smb] = byProtocol[WsProtocol.Smb] with { sessions = n };
        }
        if (smbFilesTask.IsCompletedSuccessfully)
        {
            openFiles = smbFilesTask.Result.ValueKind == JsonValueKind.Array
                ? smbFilesTask.Result.GetArrayLength()
                : 0;
        }

        // NFS 会话
        if (nfsSessionsTask.IsCompletedSuccessfully)
        {
            int n = nfsSessionsTask.Result.Count;
            byProtocol[WsProtocol.Nfs] = byProtocol[WsProtocol.Nfs] with { sessions = n };
            activeSessions += n;
        }

        // SMB 服务状态：失败回落 Unknown（TS: { status:'Unknown', name:'', startType:'' }）
        ServiceState serviceStatus = ServiceState.Unknown;
        if (svcTask.IsCompletedSuccessfully && svcTask.Result is { } svc)
        {
            serviceStatus = svc.status;
        }

        // TS: topShares —— 过滤 Special/IPC，按 connections 降序取前 8
        var topShares = allShares
            .Where(s => s.type != ShareType.Special && s.type != ShareType.IPC)
            .Select(s => new TopShare { name = s.name, connections = s.concurrentUsers, protocol = s.protocol })
            .OrderByDescending(t => t.connections)
            .Take(8)
            .ToList();

        sw.Stop();
        // TS: log.info('perf', `getDashboardStats 耗时 ${Date.now() - t0}ms`)
        Logger.Info("perf", $"getDashboardStats 耗时 {sw.ElapsedMilliseconds}ms");

        return new DashboardStats
        {
            shareCount = allShares.Count(s => s.type != ShareType.Special && s.type != ShareType.IPC),
            activeSessions = activeSessions,
            openFiles = openFiles,
            serviceStatus = serviceStatus,
            topShares = topShares,
            byProtocol = byProtocol,
        };
    }

    // ===== 审计 / 健康检查 =====

    /// <summary>TS: getAuditLog() = readAuditLog()。</summary>
    public Task<string> GetAuditLogAsync() => Task.FromResult(Audit.ReadAuditLog());

    /// <summary>TS: healthCheck() —— Get-Command Get-SmbShare 探测 PowerShell SMB 模块可用性。</summary>
    public async Task<HealthCheckResult> HealthCheckAsync()
    {
        try
        {
            // TS: runPowerShellVoid('Get-Command Get-SmbShare | Out-Null', { retries: 0 })
            await _ps.RunVoidAsync("Get-Command Get-SmbShare | Out-Null", PsRunner.DefaultTimeoutMs, 0);
            return new HealthCheckResult { ok = true, detail = "PowerShell SMB 模块可用" };
        }
        catch (Exception e)
        {
            return new HealthCheckResult { ok = false, detail = e.Message };
        }
    }

    // ===== OS 版本适配探测 =====

    /// <summary>
    /// TS: getOsInfo(opts: { refresh?: boolean }) —— 单次 PowerShell 往返探测 OS + 功能可用性，
    /// 进程内缓存；以"功能是否可用"驱动 UI/写入降级，不硬编码版本号判断。
    /// </summary>
    public async Task<OsInfo> GetOsInfoAsync(OsInfoOptions? opts = null)
    {
        // TS: if (osInfoCache && !opts.refresh) return osInfoCache
        if (_osInfoCache is not null && opts?.refresh != true) return _osInfoCache;

        // TS 原串（多段字符串拼接，逐字复制为单条命令）：
        //   '$os = Get-CimInstance Win32_OperatingSystem; '
        //   '$build = if ($os.BuildNumber) { [int]$os.BuildNumber } else { [Environment]::OSVersion.Version.Build }; '
        //   '$quic = $false; try { $quic = ((Get-SmbServerConfiguration).PSObject.Properties.Name -contains "EnableSMBQUIC") } catch {}; '
        //   '$iisFeat = $false; try { $null = Get-WindowsOptionalFeature -Online -FeatureName IIS-WebServer -ErrorAction Stop; $iisFeat = $true } catch {}; '
        //   '[PSCustomObject]@{ Caption = $os.Caption; Build = $build; SKU = [int]$os.OperatingSystemSKU; '
        //   'SmbShare = [bool](Get-Command Get-SmbShare -ErrorAction SilentlyContinue); '
        //   'NfsServer = [bool](Get-Command Get-NfsShare -ErrorAction SilentlyContinue); '
        //   'IisModule = [bool](Get-Module -ListAvailable -Name WebAdministration); '
        //   'IisFeature = $iisFeat; Quic = [bool]$quic }'
        const string command =
            "$os = Get-CimInstance Win32_OperatingSystem; " +
            "$build = if ($os.BuildNumber) { [int]$os.BuildNumber } else { [Environment]::OSVersion.Version.Build }; " +
            "$quic = $false; try { $quic = ((Get-SmbServerConfiguration).PSObject.Properties.Name -contains \"EnableSMBQUIC\") } catch {}; " +
            "$iisFeat = $false; try { $null = Get-WindowsOptionalFeature -Online -FeatureName IIS-WebServer -ErrorAction Stop; $iisFeat = $true } catch {}; " +
            "[PSCustomObject]@{ Caption = $os.Caption; Build = $build; SKU = [int]$os.OperatingSystemSKU; " +
            "SmbShare = [bool](Get-Command Get-SmbShare -ErrorAction SilentlyContinue); " +
            "NfsServer = [bool](Get-Command Get-NfsShare -ErrorAction SilentlyContinue); " +
            "IisModule = [bool](Get-Module -ListAvailable -Name WebAdministration); " +
            "IisFeature = $iisFeat; Quic = [bool]$quic }";
        JsonElement raw = await _ps.RunJsonAsync(command, PsRunner.DefaultTimeoutMs, 0);

        JsonElement obj = SystemJsCompat.FirstOrNull(raw) ?? default;
        string caption = obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty("Caption", out JsonElement c)
            && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";
        bool isServer = Rx.Regex.IsMatch(caption, "server", Rx.RegexOptions.IgnoreCase);
        double skuRaw = obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("SKU", out JsonElement sk)
            ? SystemJsCompat.NumberOf(sk)
            : double.NaN;
        int skuId = SystemJsCompat.IsFiniteNumber(skuRaw) ? (int)skuRaw : 0;
        double buildRaw = obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("Build", out JsonElement b)
            ? SystemJsCompat.NumberOf(b)
            : double.NaN;

        bool smbShare = SystemJsCompat.IsExplicitTrue(obj, "SmbShare");
        bool nfsServer = SystemJsCompat.IsExplicitTrue(obj, "NfsServer");
        bool iisModule = SystemJsCompat.IsExplicitTrue(obj, "IisModule");
        bool iisFeature = SystemJsCompat.IsExplicitTrue(obj, "IisFeature");
        bool quic = SystemJsCompat.IsExplicitTrue(obj, "Quic");

        var info = new OsInfo
        {
            // TS: caption.trim() || 'Windows（未知版本）'
            caption = caption.Trim().Length > 0 ? caption.Trim() : "Windows（未知版本）",
            buildNumber = SystemJsCompat.IsFiniteNumber(buildRaw) ? (int)buildRaw : 0,
            skuId = skuId,
            // TS: SKU_NAMES[skuId] ?? `SKU ${skuId}`
            skuName = SkuNames.TryGetValue(skuId, out string? skuName) ? skuName : $"SKU {skuId}",
            isServer = isServer,
            isHomeEdition = HomeSkus.Contains(skuId),
            hostname = Hostname(),
            features = new OsFeatures
            {
                smbShareModule = smbShare,
                nfsServerCmdlets = nfsServer,
                // TS: Server 恒可承载 IIS；客户端看 IIS 可选功能是否存在（Home 查询失败→false）
                iisAvailable = isServer || iisModule || iisFeature,
                smbQuicConfig = quic,
            },
        };
        _osInfoCache = info;
        return info;
    }

    // ===== 持久状态（stateStore 面）=====

    /// <summary>TS: stateStore.loadState()。</summary>
    public AppState LoadState() => _store.LoadState();

    /// <summary>TS: stateStore.saveState(patch)，返回合并后全量。</summary>
    public AppState SaveState(AppStatePatch patch) => _store.SaveState(patch);

    /// <summary>TS: stateStore.addJournal(entry)，返回补全后的条目。</summary>
    public JournalEntry AddJournal(JournalEntryDraft entry) => _store.AddJournal(entry);

    /// <summary>TS: stateStore.removeJournal(id)。</summary>
    public void RemoveJournal(string id) => _store.RemoveJournal(id);

    /// <summary>TS: stateStore.pushHistory(point)。</summary>
    public void PushHistory(HistoryPoint point) => _store.PushHistory(point);

    // ===== 内部：依赖容错（对齐 TS Promise.allSettled 的单路降级）=====

    private async Task<IReadOnlyList<Share>> SafeAdapterListAsync()
    {
        try
        {
            // TS: adapterList().catch(() => [] as Share[])
            return await _protocol.AdapterListAsync();
        }
        catch
        {
            return Array.Empty<Share>();
        }
    }

    private async Task<IReadOnlyList<ProtocolSession>> SafeAdapterSessionsAsync()
    {
        try
        {
            // TS: adapterSessions('nfs').catch(() => [])
            return await _protocol.AdapterSessionsAsync(WsProtocol.Nfs);
        }
        catch
        {
            return Array.Empty<ProtocolSession>();
        }
    }

    private async Task<ServiceStatus?> SafeServiceStatusAsync()
    {
        try
        {
            return await _smb.GetServiceStatusAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonElement> SafeJsonAsync(string command)
    {
        // TS: runPowerShell<any[]>('Get-SmbSession'/'Get-SmbOpenFile').catch(() => [])
        // 池 JSON 模式空 stdout 即收敛空数组，这里只需把异常转成空数组
        try
        {
            return await _ps.RunJsonAsync(command, PsRunner.DefaultTimeoutMs, 0);
        }
        catch
        {
            return PsRunner.ParseJson("");
        }
    }

    private string Hostname() => HostnameProvider?.Invoke() ?? Environment.MachineName;

    // ===== 依赖缺省替身（未注入时不崩，与 TS "模块未安装即查不到" 等价）=====

    private sealed class NullSmbService : ISmbService
    {
        public SmbServerConfig DefaultConfig() => new();
        public Task<SmbServerConfig> GetConfigAsync() => Task.FromException<SmbServerConfig>(Errors.CommandFailed("SMB 服务未注入"));
        public Task SetConfigAsync(SmbServerConfig config) => Task.FromException(Errors.CommandFailed("SMB 服务未注入"));
        public Task<SmbServerConfig> RestoreDefaultAsync() => Task.FromException<SmbServerConfig>(Errors.CommandFailed("SMB 服务未注入"));
        public Task<IReadOnlyList<SmbSnapshotMeta>> ListSnapshotsAsync() => Task.FromException<IReadOnlyList<SmbSnapshotMeta>>(Errors.CommandFailed("SMB 服务未注入"));
        public Task RollbackSnapshotAsync(string id) => Task.FromException(Errors.CommandFailed("SMB 服务未注入"));
        public Task<ServiceStatus> GetServiceStatusAsync() => Task.FromException<ServiceStatus>(Errors.CommandFailed("SMB 服务未注入"));
        public Task RestartServiceAsync() => Task.FromException(Errors.CommandFailed("SMB 服务未注入"));
        public Task StartServiceAsync() => Task.FromException(Errors.CommandFailed("SMB 服务未注入"));
        public Task StopServiceAsync() => Task.FromException(Errors.CommandFailed("SMB 服务未注入"));
    }

    private sealed class NullProtocolService : IProtocolService
    {
        public IProtocolAdapter GetAdapter(WsProtocol protocol) => throw Errors.InvalidParam("协议服务未注入");
        public IReadOnlyList<WsProtocol> GetRegisteredProtocols() => Array.Empty<WsProtocol>();
        public Dictionary<WsProtocol, ProtocolCapabilities?> GetCapabilitiesMap() => new();
        public Task<IReadOnlyList<Share>> AdapterListAsync(WsProtocol? protocol = null) => Task.FromResult<IReadOnlyList<Share>>(Array.Empty<Share>());
        public Task<Share> AdapterCreateAsync(CreateShareInput input) => Fail<Share>();
        public Task<Share> AdapterUpdateAsync(string name, UpdateShareInput input) => Fail<Share>();
        public Task AdapterDeleteAsync(WsProtocol protocol, string name) => Fail<object>();
        public Task AdapterToggleAsync(WsProtocol protocol, string name, bool enabled) => Fail<object>();
        public Task<IReadOnlyList<SharePermission>> AdapterGetPermissionsAsync(WsProtocol protocol, string name) => Fail<IReadOnlyList<SharePermission>>();
        public Task AdapterSetPermissionsAsync(WsProtocol protocol, string name, IReadOnlyList<SharePermission> perms) => Fail<object>();
        public Task<IReadOnlyList<ProtocolSession>> AdapterSessionsAsync(WsProtocol protocol) => Fail<IReadOnlyList<ProtocolSession>>();
        public Task AdapterCloseSessionAsync(WsProtocol protocol, string sessionId) => Fail<object>();
        public Task<Dictionary<WsProtocol, ProtocolFeatureState>> DetectProtocolsAsync() => Fail<Dictionary<WsProtocol, ProtocolFeatureState>>();
        public Task InstallProtocolAsync(WsProtocol protocol) => Fail<object>();

        private static Task<T> Fail<T>() => Task.FromException<T>(Errors.CommandFailed("协议服务未注入"));
    }
}