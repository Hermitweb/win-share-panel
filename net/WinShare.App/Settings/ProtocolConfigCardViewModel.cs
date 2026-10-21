using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 协议配置卡 VM 通用基座（纯逻辑 xUnit 可测；对齐
/// src/hooks/useProtocolSettings.ts [Source: src/hooks/useProtocolSettings.ts]）：
/// 能力门控 → 配置读取 → 部分补丁保存 → 服务启停/重启 → 恢复默认 → 预设应用。
/// TConfig = 契约配置类型（SmbServerConfig 等，init-only record）。
/// 保存语义与 TS setConfig(Partial) 对齐：VM 只组装「当前模式下可写字段」的补丁，
/// 与服务器现值合并后整量写回（setConfig 白名单外/只读字段不被触碰）。
/// 契约服务缺席时整卡只读降级（degraded note），不崩不臆造。
/// </summary>
public abstract class ProtocolConfigCardViewModel<TConfig> : SettingsCardBase
    where TConfig : class, new()
{
    readonly IProtocolService? _svc;

    public Protocol Protocol { get; }
    public bool AdvancedMode { get; }

    /// <summary>true=已装 / false=未装（面板走安装引导降级）/ null=未知（探测中或服务缺席）。</summary>
    public bool? Installed { get; private set; }
    public ServiceStatus? Service { get; private set; }
    public TConfig? Config { get; protected set; }
    public bool Loading { get; private set; }

    /// <summary>服务缺席降级说明（视图占位文案）。</summary>
    public string? DegradedNote { get; private set; }

    public ProtocolConfigCardViewModel(Protocol protocol, IProtocolService? service, bool advancedMode)
    {
        Protocol = protocol;
        _svc = service;
        AdvancedMode = advancedMode;
        if (service is null)
        {
            DegradedNote = "协议服务未注册（等待 Core 服务层实现）：配置读取与保存不可用，当前为只读降级。";
        }
    }

    IProtocolAdapter? Adapter => _svc?.GetAdapter(Protocol);

    /// <summary>能力位（capabilities 数据渲染，G5 数据源之一；服务缺席为 null）。</summary>
    public ProtocolCapabilities? Capabilities => Adapter?.Capabilities;

    /// <summary>外部探测结果注入（协议横幅 VM 探测后同步卡片门控；对齐 TS protocolCaps store）。</summary>
    public void SetInstalled(bool? installed)
    {
        Installed = installed;
        Raise(nameof(Installed));
    }

    /// <summary>配置读取 + 服务状态（对齐 TS 并发拉取 <c>Promise.all([getConfig, serviceStatus])</c>）。</summary>
    public async Task LoadAsync()
    {
        var adapter = Adapter;
        if (adapter is null)
        {
            Error = DegradedNote ?? "协议服务未注册";
            return;
        }
        Loading = true;
        try
        {
            var cfgTask = adapter.GetConfigAsync();
            var svcTask = adapter.GetServiceStatusAsync();
            await Task.WhenAll(cfgTask, svcTask);
            Config = Cast(cfgTask.Result);
            Service = svcTask.Result;
            Error = null;
            Raise(nameof(Config)); // 视图侧据此重拉表单绑定（服务器值回填）
            OnConfigReloaded?.Invoke();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>默认配置预填（adapter.DefaultConfig，恢复默认弹窗前视图展示用；缺席返回 null）。</summary>
    public TConfig? DefaultConfig()
    {
        var adapter = Adapter;
        if (adapter is null) return null;
        try { return Cast(adapter.DefaultConfig()); }
        catch { return null; }
    }

    /// <summary>恢复默认（对齐 useProtocolSettings.restoreDefault：写回默认并回填当前配置）。</summary>
    public async Task RestoreDefaultAsync()
    {
        var adapter = Adapter;
        if (adapter is null) { Error = DegradedNote ?? "协议服务未注册"; return; }
        if (Busy) return;
        Busy = true;
        try
        {
            var def = Cast(adapter.DefaultConfig());
            await adapter.SetConfigAsync(def);
            Config = def;
            Success = "已恢复默认配置";
            await LoadAsync();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 保存（对齐 TS save：表单校验在视图层，VM 组装补丁 → 与现值合并 → 写回 → 成功文案）。
    /// 当前模式下可写字段集合由 <see cref="BuildPatch"/> 决定（新手模式不含高级项）。
    /// </summary>
    public async Task SaveAsync()
    {
        var adapter = Adapter;
        if (adapter is null) { Error = DegradedNote ?? "协议服务未注册"; return; }
        if (Busy) return;
        Busy = true;
        try
        {
            var patch = BuildPatch();
            var merged = BuildMerged(adapter, patch);
            await adapter.SetConfigAsync(merged);
            Config = Cast(merged);
            Success = "已保存";
            await LoadAsync();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>预设一键应用（对齐 ConfigPresetBar onApply：setConfig(p.values) 后刷新）。</summary>
    public async Task ApplyPresetAsync(ConfigPreset preset)
    {
        var adapter = Adapter;
        if (adapter is null) { Error = DegradedNote ?? "协议服务未注册"; return; }
        if (Busy) return;
        Busy = true;
        try
        {
            var merged = BuildMerged(adapter, preset.Values);
            await adapter.SetConfigAsync(merged);
            Success = $"已应用预设「{preset.Name}」";
            await LoadAsync();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task RestartAsync() => await ServiceActionAsync("重启", a => a.RestartServiceAsync(), s => $"{s} 服务已重启");

    /// <summary>
    /// 启动（契约缺口处理：IProtocolAdapter 面没有独立 start/stop，stopped 态下以
    /// RestartServiceAsync 达成启动语义；stop 无契约方法 → 视图禁用按钮并注明原因）。
    /// </summary>
    public async Task StartAsync() => await ServiceActionAsync("启动", a => a.RestartServiceAsync(), s => $"{s} 服务已启动");

    async Task ServiceActionAsync(string verb, Func<IProtocolAdapter, Task> action, Func<string, string> text)
    {
        var adapter = Adapter;
        if (adapter is null) { Error = DegradedNote ?? "协议服务未注册"; return; }
        if (Busy) return;
        Busy = true;
        try
        {
            await action(adapter);
            Success = text(Protocol.Display());
            await LoadAsync();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>服务启停三按钮的可用性（对齐 ProtocolSettingsShell：Stopped→启动可点，否则→停止可点）。</summary>
    public bool IsServiceStopped => Service?.status == ServiceState.Stopped;

    /// <summary>表单区宿主（视图层设置；LoadAsync 后重建绑定用）。</summary>
    public Action? OnConfigReloaded { get; set; }

    /// <summary>当前模式下可写字段 → 取值（属性名与 TConfig 属性名一致；枚举用 C# 枚举名字面量）。</summary>
    protected abstract IReadOnlyDictionary<string, object?> BuildPatch();

    /// <summary>测试可观测面（xUnit 直读补丁内容；与 UI 保存路径同源）。</summary>
    public IReadOnlyDictionary<string, object?> BuildPatchForTesting() => BuildPatch();

    // ===== 内部：与服务器现值合并补丁（init-only record 经 backing field 克隆，逐字段对齐 TS Partial 合并） =====

    TConfig? Cast(object? raw) => raw as TConfig;

    protected TConfig? BuildMerged(IProtocolAdapter adapter, IReadOnlyDictionary<string, object?> patch)
    {
        var current = Config;
        return current is null ? null : CloneWithPatch(current, patch);
    }

    /// <summary>现值 + 补丁 → 新实例（未列出的字段原值保留；补丁值按目标属性类型做数值/枚举收敛）。</summary>
    protected TConfig CloneWithPatch(TConfig current, IReadOnlyDictionary<string, object?> patch)
    {
        var clone = new TConfig();
        var props = typeof(TConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead).ToList();
        foreach (var prop in props)
        {
            object? value = null;
            try { value = prop.GetValue(current); } catch { value = null; }
            if (patch.TryGetValue(prop.Name, out var raw) && raw is not null)
            {
                value = Coerce(raw, prop.PropertyType) ?? value;
            }
            SetBacking(clone, prop, value);
        }
        return clone;
    }

    /// <summary>补丁值类型收敛：数值（int→long 等）与枚举（字符串字面量 → 枚举）；不匹配时原值抛入合并异常。</summary>
    protected static object? Coerce(object? raw, Type target)
    {
        if (raw is null) return null;
        if (target.IsEnum && raw is string s)
        {
            // 预设字面量可能超出 C# 契约枚举域（如 TS 'OptimizeForSpeed' 不在 RequestCompression 三值内）：
            // 解析失败 → 返回 null = 该字段保留服务器现值（不写脏值；缺口见任务回报）
            try { return Enum.Parse(target, s, ignoreCase: true); }
            catch { return null; }
        }
        if (raw is object r && target.IsAssignableFrom(r.GetType())) return r;
        if (target == typeof(long) && raw is int i) return i;
        try
        {
            return Convert.ChangeType(raw, target);
        }
        catch
        {
            throw new InvalidOperationException($"预设/补丁值 {raw} 无法写入 {target.Name} 类型字段");
        }
    }

    static void SetBacking(object instance, PropertyInfo prop, object? value)
    {
        // init-only 属性无公有 setter；经 backing field（_Name）写入
        var field = typeof(TConfig)
            .GetField($"_{prop.Name}", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"配置类型缺少字段 {prop.Name}");
        RuntimeHelpers.SetFieldValue(instance, field, value);
    }
}

/// <summary>
/// 各协议卡 VM 的「视图可绑定」属性镜像面：把契约 record 的字段暴露为 INPC 属性，
/// WPF 表单双向绑定；保存时由 <see cref="ProtocolConfigCardViewModel{TConfig}.BuildPatch"/> 收敛。
/// 字段清单逐字段对齐各 TS 面板 [Source: NfsSettingsPanel/FtpSettingsPanel/WebdavSettingsPanel/Settings.tsx]。
/// </summary>

/// <summary>SMB 配置卡（Settings.tsx 服务器配置面板；新手模式高级参数收起，保存不含高级项）。</summary>
public sealed class SmbConfigCardViewModel : ProtocolConfigCardViewModel<SmbServerConfig>
{
    // 基础协议
    public bool EnableSmb1 { get => C().enableSMB1Protocol; set { Config = C() with { enableSMB1Protocol = value }; Raise(); } }
    public bool EnableSmb2 { get => C().enableSMB2Protocol; set { Config = C() with { enableSMB2Protocol = value }; Raise(); } }
    public bool EnableSmb3 { get => C().enableSMB3Protocol; set { Config = C() with { enableSMB3Protocol = value }; Raise(); } }
    public bool AuditSmb1 { get => C().auditSmb1Access; set { Config = C() with { auditSmb1Access = value }; Raise(); } }
    // 安全
    public bool GuestAccess { get => C().enableGuestUserAccess; set { Config = C() with { enableGuestUserAccess = value }; Raise(); } }
    public bool InsecureGuestLogons { get => C().enableInsecureGuestLogons; set { Config = C() with { enableInsecureGuestLogons = value }; Raise(); } }
    public bool RequireSignature { get => C().requireSecuritySignature; set { Config = C() with { requireSecuritySignature = value }; Raise(); } }
    public bool StrictNameChecking { get => C().enableStrictNameChecking; set { Config = C() with { enableStrictNameChecking = value }; Raise(); } }
    public bool SilentAu { get => C().silentAU; set { Config = C() with { silentAU = value }; Raise(); } }
    // 性能与功能
    public bool MultiChannel { get => C().enableMultiChannel; set { Config = C() with { enableMultiChannel = value }; Raise(); } }
    public bool Leasing { get => C().enableLeasing; set { Config = C() with { enableLeasing = value }; Raise(); } }
    public bool Oplocks { get => C().enableOplocks; set { Config = C() with { enableOplocks = value }; Raise(); } }
    public bool OplockDirectoryCache { get => C().enableOplockDirectoryCache; set { Config = C() with { enableOplockDirectoryCache = value }; Raise(); } }
    public bool DirectoryCache { get => C().enableSMBDirectoryCache; set { Config = C() with { enableSMBDirectoryCache = value }; Raise(); } }
    public bool ChannelChange { get => C().enableChannelChange; set { Config = C() with { enableChannelChange = value }; Raise(); } }
    public bool SmbQuic { get => C().enableSMBQUIC; set { Config = C() with { enableSMBQUIC = value }; Raise(); } }
    public bool AnnounceServer { get => C().announceServer; set { Config = C() with { announceServer = value }; Raise(); } }
    public bool MultipleSessions { get => C().multipleSessionsPerConnection; set { Config = C() with { multipleSessionsPerConnection = value }; Raise(); } }
    // 高级（专家模式）
    public int UnauthenticatedTimeLimit { get => C().unauthenticatedUsersTimeLimit; set { Config = C() with { unauthenticatedUsersTimeLimit = value }; Raise(); } }
    public int SessionTimeout { get => C().sessionTimeoutSeconds; set { Config = C() with { sessionTimeoutSeconds = value }; Raise(); } }
    public int MaxSessionPerConn { get => C().maxSessionPerConnection; set { Config = C() with { maxSessionPerConnection = value }; Raise(); } }
    public int MaxMpxCount { get => C().maxMpxCount; set { Config = C() with { maxMpxCount = value }; Raise(); } }
    public int MaxWorkItems { get => C().maxWorkItems; set { Config = C() with { maxWorkItems = value }; Raise(); } }
    public int MaxThreadsPerQueue { get => C().maxThreadsPerQueue; set { Config = C() with { maxThreadsPerQueue = value }; Raise(); } }
    public string RequestCompression { get => RequestCompressionName(C().requestCompression); set { Config = C() with { requestCompression = ParseCompression(value) }; Raise(); } }

    public SmbConfigCardViewModel(IProtocolService? svc, bool advancedMode)
        : base(Protocol.Smb, svc, advancedMode)
    {
        Config = new SmbServerConfig();
    }

    SmbServerConfig C() => Config ??= new SmbServerConfig();

    public static string RequestCompressionName(RequestCompression c) => c switch
    {
        RequestCompression.Off => "关闭",
        RequestCompression.Allow => "允许",
        RequestCompression.Require => "要求",
        _ => c.ToString(),
    };

    static RequestCompression ParseCompression(string? s) => s switch
    {
        "关闭" or "Off" => RequestCompression.Off,
        "允许" or "Allow" => RequestCompression.Allow,
        "要求" or "Require" => RequestCompression.Require,
        _ => RequestCompression.Off,
    };

    protected override IReadOnlyDictionary<string, object?> BuildPatch()
    {
        var c = C();
        var patch = new Dictionary<string, object?>
        {
            ["enableSMB1Protocol"] = c.enableSMB1Protocol,
            ["enableSMB2Protocol"] = c.enableSMB2Protocol,
            ["enableSMB3Protocol"] = c.enableSMB3Protocol,
            ["auditSmb1Access"] = c.auditSmb1Access,
            ["enableGuestUserAccess"] = c.enableGuestUserAccess,
            ["enableInsecureGuestLogons"] = c.enableInsecureGuestLogons,
            ["requireSecuritySignature"] = c.requireSecuritySignature,
            ["enableStrictNameChecking"] = c.enableStrictNameChecking,
            ["silentAU"] = c.silentAU,
            ["enableMultiChannel"] = c.enableMultiChannel,
            ["enableLeasing"] = c.enableLeasing,
            ["enableOplocks"] = c.enableOplocks,
            ["enableOplockDirectoryCache"] = c.enableOplockDirectoryCache,
            ["enableSMBDirectoryCache"] = c.enableSMBDirectoryCache,
            ["enableChannelChange"] = c.enableChannelChange,
            ["enableSMBQUIC"] = c.enableSMBQUIC,
            ["announceServer"] = c.announceServer,
            ["multipleSessionsPerConnection"] = c.multipleSessionsPerConnection,
        };
        if (AdvancedMode)
        {
            patch["unauthenticatedUsersTimeLimit"] = c.unauthenticatedUsersTimeLimit;
            patch["sessionTimeoutSeconds"] = c.sessionTimeoutSeconds;
            patch["maxSessionPerConnection"] = c.maxSessionPerConnection;
            patch["maxMpxCount"] = c.maxMpxCount;
            patch["maxWorkItems"] = c.maxWorkItems;
            patch["maxThreadsPerQueue"] = c.maxThreadsPerQueue;
            patch["requestCompression"] = c.requestCompression.ToString();
        }
        return patch;
    }
}

/// <summary>NFS 配置卡（基础 + 连接与超时；匿名 UID/GID、网关字符集、协议版本只读）。</summary>
public sealed class NfsConfigCardViewModel : ProtocolConfigCardViewModel<NfsServerConfig>
{
    public bool GracefulUnmount { get => C().gracefulUnmount; set { Config = C() with { gracefulUnmount = value }; Raise(); } }
    public bool LogActivity { get => C().logActivity; set { Config = C() with { logActivity = value }; Raise(); } }
    public bool UnmappedAccess { get => C().enableUnmappedAccess; set { Config = C() with { enableUnmappedAccess = value }; Raise(); } }
    public bool AuthRenegotiation { get => C().enableAuthenticationRenegotiation; set { Config = C() with { enableAuthenticationRenegotiation = value }; Raise(); } }
    public int TcpTimeout { get => C().tcpConnectionTimeout; set { Config = C() with { tcpConnectionTimeout = value }; Raise(); } }
    public int UdpTimeout { get => C().udpConnectionTimeout; set { Config = C() with { udpConnectionTimeout = value }; Raise(); } }
    public int RestartTimeout { get => C().restartConnectionTimeout; set { Config = C() with { restartConnectionTimeout = value }; Raise(); } }
    public int MaxConcurrentPerUser { get => C().maxConcurrentConnectionsPerUser; set { Config = C() with { maxConcurrentConnectionsPerUser = value }; Raise(); } }
    public int DirectoryCacheExpiry { get => C().directoryCacheExpiry; set { Config = C() with { directoryCacheExpiry = value }; Raise(); } }
    // 只读展示（不参与补丁）
    public int AnonymousUid => C().anonymousUid;
    public int AnonymousGid => C().anonymousGid;
    public string GatewayCharacterSet => C().gatewayCharacterSet;
    public string ProtocolVersion => C().protocolVersion;

    public NfsConfigCardViewModel(IProtocolService? svc, bool advancedMode)
        : base(Protocol.Nfs, svc, advancedMode)
    {
        Config = new NfsServerConfig();
    }

    NfsServerConfig C() => Config ??= new NfsServerConfig();

    protected override IReadOnlyDictionary<string, object?> BuildPatch()
    {
        var c = C();
        return new Dictionary<string, object?>
        {
            ["gracefulUnmount"] = c.gracefulUnmount,
            ["logActivity"] = c.logActivity,
            ["enableUnmappedAccess"] = c.enableUnmappedAccess,
            ["enableAuthenticationRenegotiation"] = c.enableAuthenticationRenegotiation,
            ["tcpConnectionTimeout"] = c.tcpConnectionTimeout,
            ["udpConnectionTimeout"] = c.udpConnectionTimeout,
            ["restartConnectionTimeout"] = c.restartConnectionTimeout,
            ["maxConcurrentConnectionsPerUser"] = c.maxConcurrentConnectionsPerUser,
            ["directoryCacheExpiry"] = c.directoryCacheExpiry,
        };
    }
}

/// <summary>FTP 配置卡（IIS ftpServer 服务器级；站点级配置在共享管理页）。</summary>
public sealed class FtpConfigCardViewModel : ProtocolConfigCardViewModel<FtpServerConfig>
{
    public string SslControl { get => SslName(C().sslControlChannelPolicy); set { Config = C() with { sslControlChannelPolicy = ParseSsl(value) }; Raise(); } }
    public string SslData { get => SslName(C().sslDataChannelPolicy); set { Config = C() with { sslDataChannelPolicy = ParseSsl(value) }; Raise(); } }
    public string SslCertHash { get => C().sslServerCertHash; set { Config = C() with { sslServerCertHash = value }; Raise(); } }
    public bool SslClientCertRequired { get => C().sslClientCertRequired; set { Config = C() with { sslClientCertRequired = value }; Raise(); } }
    public bool Ssl128 { get => C().ssl128; set { Config = C() with { ssl128 = value }; Raise(); } }
    public bool AnonymousEnabled { get => C().anonymousEnabled; set { Config = C() with { anonymousEnabled = value }; Raise(); } }
    public string AnonymousUser { get => C().anonymousUserName; set { Config = C() with { anonymousUserName = value }; Raise(); } }
    public bool BasicEnabled { get => C().basicEnabled; set { Config = C() with { basicEnabled = value }; Raise(); } }
    public int FwLowPort { get => C().firewallLowDataChannelPort; set { Config = C() with { firewallLowDataChannelPort = value }; Raise(); } }
    public int FwHighPort { get => C().firewallHighDataChannelPort; set { Config = C() with { firewallHighDataChannelPort = value }; Raise(); } }
    public string GreetingMessage { get => C().greetingMessage; set { Config = C() with { greetingMessage = value }; Raise(); } }
    public string BannerMessage { get => C().bannerMessage; set { Config = C() with { bannerMessage = value }; Raise(); } }
    public string ExitMessage { get => C().exitMessage; set { Config = C() with { exitMessage = value }; Raise(); } }
    public string MaxClientsMessage { get => C().maxClientsMessage; set { Config = C() with { maxClientsMessage = value }; Raise(); } }
    public bool SuppressDefaultMessages { get => C().suppressDefaultMessages; set { Config = C() with { suppressDefaultMessages = value }; Raise(); } }
    public bool ShowVirtualDirs { get => C().showVirtualDirs; set { Config = C() with { showVirtualDirs = value }; Raise(); } }
    public string IsolationMode { get => IsoName(C().userIsolationMode); set { Config = C() with { userIsolationMode = ParseIso(value) }; Raise(); } }
    public int UnauthTimeout { get => C().unauthenticatedTimeout; set { Config = C() with { unauthenticatedTimeout = value }; Raise(); } }
    public int ControlTimeout { get => C().controlConnectionTimeout; set { Config = C() with { controlConnectionTimeout = value }; Raise(); } }
    public int DataChannelTimeout { get => C().dataChannelConnectionTimeout; set { Config = C() with { dataChannelConnectionTimeout = value }; Raise(); } }
    public bool KeepPartialUploads { get => C().keepPartialUploads; set { Config = C() with { keepPartialUploads = value }; Raise(); } }
    public bool ReplaceOnRename { get => C().allowReplaceOnRename; set { Config = C() with { allowReplaceOnRename = value }; Raise(); } }
    public string LogDir { get => C().logFileDirectory; set { Config = C() with { logFileDirectory = value }; Raise(); } }
    public string LogPeriod { get => PeriodName(C().logFilePeriod); set { Config = C() with { logFilePeriod = ParsePeriod(value) }; Raise(); } }

    public FtpConfigCardViewModel(IProtocolService? svc, bool advancedMode)
        : base(Protocol.Ftp, svc, advancedMode)
    {
        Config = new FtpServerConfig();
    }

    FtpServerConfig C() => Config ??= new FtpServerConfig();

    // 枚举展示名与 TS FtpSettingsPanel 下拉选项逐字对齐
    public static string SslName(SslPolicy p) => p switch
    {
        SslPolicy.SslAllow => "允许（不强制）",
        SslPolicy.SslRequire => "要求",
        SslPolicy.SslRequireCredentials => "要求证书",
        _ => p.ToString(),
    };
    public static string IsoName(FtpUserIsolationMode m) => m switch
    {
        FtpUserIsolationMode.None => "无隔离",
        FtpUserIsolationMode.StartInUsersDirectory => "起始用户目录",
        FtpUserIsolationMode.IsolateUsers => "隔离用户",
        FtpUserIsolationMode.IsolateUsersWithoutAD => "隔离（无 AD）",
        FtpUserIsolationMode.ActiveDirectory => "Active Directory",
        _ => m.ToString(),
    };
    public static string PeriodName(FtpLogPeriod p) => p switch
    {
        FtpLogPeriod.Hourly => "每小时",
        FtpLogPeriod.Daily => "每天",
        FtpLogPeriod.Weekly => "每周",
        FtpLogPeriod.Monthly => "每月",
        FtpLogPeriod.MaxSize => "按大小",
        FtpLogPeriod.Never => "从不",
        _ => p.ToString(),
    };
    static SslPolicy ParseSsl(string? s) => s switch
    {
        "要求" or "SslRequire" => SslPolicy.SslRequire,
        "要求证书" or "SslRequireCredentials" => SslPolicy.SslRequireCredentials,
        _ => SslPolicy.SslAllow,
    };
    static FtpUserIsolationMode ParseIso(string? s) => s switch
    {
        "起始用户目录" or "StartInUsersDirectory" => FtpUserIsolationMode.StartInUsersDirectory,
        "隔离用户" or "IsolateUsers" => FtpUserIsolationMode.IsolateUsers,
        "隔离（无 AD）" or "IsolateUsersWithoutAD" => FtpUserIsolationMode.IsolateUsersWithoutAD,
        "Active Directory" or "ActiveDirectory" => FtpUserIsolationMode.ActiveDirectory,
        _ => FtpUserIsolationMode.None,
    };
    static FtpLogPeriod ParsePeriod(string? s) => s switch
    {
        "每小时" or "Hourly" => FtpLogPeriod.Hourly,
        "每周" or "Weekly" => FtpLogPeriod.Weekly,
        "每月" or "Monthly" => FtpLogPeriod.Monthly,
        "按大小" or "MaxSize" => FtpLogPeriod.MaxSize,
        "从不" or "Never" => FtpLogPeriod.Never,
        _ => FtpLogPeriod.Daily,
    };

    protected override IReadOnlyDictionary<string, object?> BuildPatch()
    {
        var c = C();
        return new Dictionary<string, object?>
        {
            ["sslControlChannelPolicy"] = c.sslControlChannelPolicy.ToString(),
            ["sslDataChannelPolicy"] = c.sslDataChannelPolicy.ToString(),
            ["sslServerCertHash"] = c.sslServerCertHash,
            ["sslClientCertRequired"] = c.sslClientCertRequired,
            ["ssl128"] = c.ssl128,
            ["anonymousEnabled"] = c.anonymousEnabled,
            ["anonymousUserName"] = c.anonymousUserName,
            ["basicEnabled"] = c.basicEnabled,
            ["firewallLowDataChannelPort"] = c.firewallLowDataChannelPort,
            ["firewallHighDataChannelPort"] = c.firewallHighDataChannelPort,
            ["greetingMessage"] = c.greetingMessage,
            ["bannerMessage"] = c.bannerMessage,
            ["exitMessage"] = c.exitMessage,
            ["maxClientsMessage"] = c.maxClientsMessage,
            ["suppressDefaultMessages"] = c.suppressDefaultMessages,
            ["showVirtualDirs"] = c.showVirtualDirs,
            ["userIsolationMode"] = c.userIsolationMode.ToString(),
            ["unauthenticatedTimeout"] = c.unauthenticatedTimeout,
            ["controlConnectionTimeout"] = c.controlConnectionTimeout,
            ["dataChannelConnectionTimeout"] = c.dataChannelConnectionTimeout,
            ["keepPartialUploads"] = c.keepPartialUploads,
            ["allowReplaceOnRename"] = c.allowReplaceOnRename,
            ["logFileDirectory"] = c.logFileDirectory,
            ["logFilePeriod"] = c.logFilePeriod.ToString(),
        };
    }
}

/// <summary>WebDAV 配置卡（IIS system.webServer；服务器级只读状态单列展示）。</summary>
public sealed class WebdavConfigCardViewModel : ProtocolConfigCardViewModel<WebdavServerConfig>
{
    public bool AuthoringEnabled { get => C().authoringEnabled; set { Config = C() with { authoringEnabled = value }; Raise(); } }
    public long AuthoringMaxBody { get => C().authoringMaxRequestBodySize; set { Config = C() with { authoringMaxRequestBodySize = value }; Raise(); } }
    public long MaxContentLength { get => C().maxAllowedContentLength; set { Config = C() with { maxAllowedContentLength = value }; Raise(); } }
    public bool DoubleEscaping { get => C().allowDoubleEscaping; set { Config = C() with { allowDoubleEscaping = value }; Raise(); } }
    public bool VerifyIntegration { get => C().verifyIntegration; set { Config = C() with { verifyIntegration = value }; Raise(); } }
    public bool AnonymousEnabled { get => C().anonymousEnabled; set { Config = C() with { anonymousEnabled = value }; Raise(); } }
    public bool BasicEnabled { get => C().basicEnabled; set { Config = C() with { basicEnabled = value }; Raise(); } }
    public bool WindowsAuth { get => C().windowsEnabled; set { Config = C() with { windowsEnabled = value }; Raise(); } }
    public int MaxUrlLength { get => C().maxUrlLength; set { Config = C() with { maxUrlLength = value }; Raise(); } }
    public int MaxQueryStringLength { get => C().maxQueryStringLength; set { Config = C() with { maxQueryStringLength = value }; Raise(); } }
    // 只读展示（服务器级状态）
    public int GlobalRulesCount => C().globalAuthoringRulesCount;
    public bool StaticCompression => C().enableStaticCompression;
    public bool DynamicCompression => C().enableDynamicCompression;
    public bool RequireSsl => C().requireSSL;

    public WebdavConfigCardViewModel(IProtocolService? svc, bool advancedMode)
        : base(Protocol.Webdav, svc, advancedMode)
    {
        Config = new WebdavServerConfig();
    }

    WebdavServerConfig C() => Config ??= new WebdavServerConfig();

    protected override IReadOnlyDictionary<string, object?> BuildPatch()
    {
        var c = C();
        return new Dictionary<string, object?>
        {
            ["authoringEnabled"] = c.authoringEnabled,
            ["authoringMaxRequestBodySize"] = c.authoringMaxRequestBodySize,
            ["maxAllowedContentLength"] = c.maxAllowedContentLength,
            ["allowDoubleEscaping"] = c.allowDoubleEscaping,
            ["verifyIntegration"] = c.verifyIntegration,
            ["anonymousEnabled"] = c.anonymousEnabled,
            ["basicEnabled"] = c.basicEnabled,
            ["windowsEnabled"] = c.windowsEnabled,
            ["maxUrlLength"] = c.maxUrlLength,
            ["maxQueryStringLength"] = c.maxQueryStringLength,
        };
    }
}
