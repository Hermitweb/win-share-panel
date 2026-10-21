// 移植自 electron/services/protocol/detect.ts（协议功能检测 + 安装引导）。
// 命令串逐字对齐 TS 原文（仅 TS 模板字符串 → C# 字符串拼接/插值）；执行统一经注入的
// IPsInvoker（生产实现 PsRunner → PoolSingleton 常驻池 + PsArgs 编码闸门）。本域命令串不含
// 用户输入，featureName 亦只取自下方 ProtocolInfos 常量，因此无需 PsArgs 转义。
// 注：命名空间用复数 Protocols（与 ProtocolRegistry 同域），单数会与 Contracts.Protocol 枚举同名冲突。
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;

namespace WinShare.Core.Services.Protocols;

/// <summary>
/// detect.ts 全部导出：DetectProtocolsAsync（detectProtocols）/ InstallProtocolAsync（installProtocol）
/// + 测试钩子 ResetInflightForTesting（__resetInflightForTesting）。
/// </summary>
public static class ProtocolDetector
{
    private const int DefaultTimeoutMs = PsRunner.DefaultTimeoutMs;
    private const int DefaultRetries = PsRunner.DefaultRetries;
    /// <summary>TS: { retries: 0 }。</summary>
    private const int NoRetries = 0;
    /// <summary>TS: installProtocol 的 runPowerShellVoid(cmd, { timeout: 300000, retries: 0 })。</summary>
    private const int InstallTimeoutMs = 300000;

    /// <summary>TS: PROTOCOL_INFO 的 value 形状。</summary>
    private sealed record ProtocolInfo(string ServerCmd, string ClientCmd, string Hint, string ServiceName);

    // TS: PROTOCOL_INFO —— 各协议安装命令与提示
    private static readonly Dictionary<Protocol, ProtocolInfo> ProtocolInfos = new()
    {
        [Protocol.Smb] = new ProtocolInfo(
            ServerCmd: "",
            ClientCmd: "",
            Hint: "SMB 内置于 Windows，LanmanServer 服务随系统启动",
            ServiceName: "LanmanServer"),
        [Protocol.Nfs] = new ProtocolInfo(
            ServerCmd: "Install-WindowsFeature FS-NFS-Service -IncludeManagementTools",
            ClientCmd: "Enable-WindowsOptionalFeature -Online -FeatureName ClientForNFS-Infrastructure -All -NoRestart",
            Hint: "Windows Server 安装 NFS 服务角色；Win10/11 客户端仅支持 NFS 客户端，无法创建共享",
            ServiceName: "NfsService"),
        [Protocol.Ftp] = new ProtocolInfo(
            ServerCmd: "Install-WindowsFeature Web-Ftp-Server -IncludeAllSubFeature -IncludeManagementTools",
            ClientCmd: "Enable-WindowsOptionalFeature -Online -FeatureName IIS-FTPServer -All -NoRestart",
            Hint: "需安装 IIS 角色与 FTP 角色服务，ftpsvc 服务依赖 IIS",
            ServiceName: "ftpsvc"),
        [Protocol.Webdav] = new ProtocolInfo(
            ServerCmd: "Install-WindowsFeature Web-WebDAV-Redirector -IncludeAllSubFeature -IncludeManagementTools",
            ClientCmd: "Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebDAV -All -NoRestart",
            Hint: "需安装 IIS 角色与 WebDAV 发布角色服务",
            ServiceName: "W3SVC"),
    };

    // TS: /-FeatureName\s+(\S+)/ —— 从安装命令中提取 FeatureName 用于预检
    private static readonly Regex FeatureNamePattern = new(@"-FeatureName\s+(\S+)", RegexOptions.Compiled);

    // TS: installProtocol 的三段错误友好化正则（逐字对齐 TS 字面量与 i 标志）
    private static readonly Regex ElevationPattern = new(
        @"elevation|administrator|权限不足|拒绝访问|Run as administrator|以管理员身份运行",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoMatchPattern = new(
        @"NoMatch|not found|找不到|无法识别|not a valid|无效",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RestartPattern = new(
        @"restart|reboot|重启|重启计算机",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // TS: let inflightDetect: Promise<ProtocolDetectionResult> | null —— 并发去重（HealthBar / Dashboard /
    // ProtocolCapabilityBanner 同时挂载时只起一组 PowerShell，避免 3 组进程造成 CPU 峰值 + 重复延迟）。
    private static readonly object InflightGate = new();
    private static Task<Dictionary<Protocol, ProtocolFeatureState>>? _inflightDetect;

    /// <summary>TS: detectFtpState() 的返回形状。</summary>
    private sealed record FtpState(bool Installed, bool IisInstalled, bool FtpRoleInstalled)
    {
        public static readonly FtpState None = new(false, false, false);
    }

    /// <summary>TS: detectModules() 的返回形状。</summary>
    private sealed record Modules(bool Nfs, bool Ftp, bool Webdav, FtpState FtpState);

    /// <summary>
    /// TS: detectProtocols() —— 并发调用共享同一 in-flight 检测，完成后清空供下次刷新使用。
    /// </summary>
    public static Task<Dictionary<Protocol, ProtocolFeatureState>> DetectProtocolsAsync(IPsInvoker ps)
    {
        lock (InflightGate)
        {
            if (_inflightDetect is not null)
            {
                Logger.Info("perf", "detectProtocols 复用 in-flight promise（跳过重复检测）");
                return _inflightDetect;
            }
            // M-8：按次时间戳计时，规避并发调用下同名标签冲突；清空动作在任务完成前执行（等价 TS .finally）
            Task<Dictionary<Protocol, ProtocolFeatureState>> task =
                DetectAndClearAsync(ps, Stopwatch.StartNew());
            _inflightDetect = task;
            return task;
        }
    }

    /// <summary>TS 测试钩子 __resetInflightForTesting：清空 in-flight，防止上个测试的未完成检测泄漏。</summary>
    public static void ResetInflightForTesting()
    {
        lock (InflightGate)
        {
            _inflightDetect = null;
        }
    }

    private static async Task<Dictionary<Protocol, ProtocolFeatureState>> DetectAndClearAsync(
        IPsInvoker ps, Stopwatch sw)
    {
        try
        {
            return await DoDetectProtocolsAsync(ps);
        }
        finally
        {
            lock (InflightGate)
            {
                _inflightDetect = null;
            }
            Logger.Info("perf", $"detectProtocols 耗时 {sw.ElapsedMilliseconds}ms");
        }
    }

    // TS: doDetectProtocols —— 三组查询并行（Promise.all）
    private static async Task<Dictionary<Protocol, ProtocolFeatureState>> DoDetectProtocolsAsync(IPsInvoker ps)
    {
        Task<bool> serverTask = IsWindowsServerAsync(ps);
        Task<Modules> modulesTask = DetectModulesAsync(ps);
        Task<Dictionary<string, JsonElement>> servicesTask = DetectServicesAsync(ps);
        await Task.WhenAll(serverTask, modulesTask, servicesTask);

        bool isServer = serverTask.Result;
        Modules modules = modulesTask.Result;
        Dictionary<string, JsonElement> services = servicesTask.Result;

        return new Dictionary<Protocol, ProtocolFeatureState>
        {
            [Protocol.Smb] = Build(Protocol.Smb, isServer, modules, services),
            [Protocol.Nfs] = Build(Protocol.Nfs, isServer, modules, services),
            [Protocol.Ftp] = Build(Protocol.Ftp, isServer, modules, services),
            [Protocol.Webdav] = Build(Protocol.Webdav, isServer, modules, services),
        };
    }

    // TS: build(proto) —— 单协议状态组装
    private static ProtocolFeatureState Build(
        Protocol proto, bool isServer, Modules modules, Dictionary<string, JsonElement> services)
    {
        ProtocolInfo info = ProtocolInfos[proto];
        string svcKey = info.ServiceName.ToLowerInvariant();
        services.TryGetValue(svcKey, out JsonElement svc);
        // NFS 客户端服务名为 NfsClnt，服务端为 NfsService；客户端系统无 NfsService 时回退到 NfsClnt
        if (proto == Protocol.Nfs && svc.ValueKind != JsonValueKind.Object)
        {
            services.TryGetValue("nfsclnt", out svc);
        }
        string svcName = Text(Prop(svc, "Name"));
        string serviceName = svcName.Length > 0 ? svcName : info.ServiceName;

        bool installed = proto switch
        {
            Protocol.Smb => true,
            Protocol.Nfs => modules.Nfs,
            Protocol.Ftp => modules.Ftp,
            _ => modules.Webdav,
        };

        ProtocolInstallType installType;
        if (proto == Protocol.Smb) installType = ProtocolInstallType.Builtin;
        else if (isServer) installType = ProtocolInstallType.ServerFeature;
        else installType = proto == Protocol.Nfs ? ProtocolInstallType.ClientOnly : ProtocolInstallType.IisRole;

        // FTP 提示细化：区分 IIS 未装 / FTP 角色未装 / 服务已停
        string installHint = info.Hint;
        if (proto == Protocol.Ftp && !installed)
        {
            FtpState ftp = modules.FtpState;
            if (!ftp.IisInstalled && !ftp.FtpRoleInstalled)
            {
                installHint = isServer
                    ? "FTP 依赖 IIS 角色。当前 IIS 与 FTP 角色服务均未安装，请点击「安装」自动安装 IIS + FTP 角色服务。"
                    : "FTP 依赖 IIS 可选功能。当前 IIS 与 FTP 可选功能均未安装，请点击「安装」自动安装。";
            }
            else if (ftp.IisInstalled && !ftp.FtpRoleInstalled)
            {
                installHint = isServer
                    ? "IIS 基础角色已安装，但 FTP 角色服务未安装。请点击「安装」补装 FTP 发布服务。"
                    : "IIS 基础功能已安装，但 FTP 可选功能未启用。请点击「安装」补装 FTP 功能。";
            }
            else
            {
                installHint = "FTP 角色服务已安装，但 ftpsvc 服务未运行。请点击「安装」或前往服务管理启动 ftpsvc。";
            }
        }

        return new ProtocolFeatureState
        {
            protocol = proto,
            installed = installed,
            installType = installType,
            serviceName = serviceName,
            serviceStatus = ParseServiceStatus(svc),
            installCommand = isServer ? info.ServerCmd : info.ClientCmd,
            installHint = installHint,
        };
    }

    // TS: isWindowsServer() —— Get-CimInstance 跨引擎兼容（5.1 / 7.x 都有）；失败降级 false
    private static async Task<bool> IsWindowsServerAsync(IPsInvoker ps)
    {
        try
        {
            JsonElement caption = await ps.RunJsonAsync(
                "(Get-CimInstance Win32_OperatingSystem).Caption", DefaultTimeoutMs, NoRetries);
            return Regex.IsMatch(Text(caption), "Server", RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // TS: parseServiceStatus —— 服务状态数值枚举（4=Running / 1=Stopped）
    private static ServiceState ParseServiceStatus(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return ServiceState.Unknown;
        JsonElement? name = Prop(raw, "Name");
        if (name is null || Text(name.Value).Length == 0) return ServiceState.Unknown;
        JsonElement status = Prop(raw, "Status") ?? default;
        string statusStr = status.ValueKind == JsonValueKind.String ? status.GetString() ?? "" : "";
        double statusNum = ToNumber(status);
        if (statusStr == "Running" || statusNum == 4) return ServiceState.Running;
        if (statusStr == "Stopped" || statusNum == 1) return ServiceState.Stopped;
        return ServiceState.Unknown;
    }

    // TS: Number(raw.Status) —— 非数值/非数字串收敛为 NaN（永远不等于 4/1）
    private static double ToNumber(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.String => double.TryParse(el.GetString(), out double d) ? d : double.NaN,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => double.NaN,
    };

    // TS: detectFtpState —— 区分 IIS 基础服务与 FTP 角色服务（多信号探测）
    private static async Task<FtpState> DetectFtpStateAsync(IPsInvoker ps)
    {
        try
        {
            JsonElement raw = await ps.RunJsonAsync(
                // 若 ftpsvc 不存在但 IIS 已安装，进一步用可选功能/服务器角色确认 FTP 发布功能状态
                "$ftp=$false; $iis=$false; $ftpRole=$false; " +
                "try { if (Get-Service W3SVC -ErrorAction SilentlyContinue) { $iis=$true } } catch {}; " +
                "try { if (Get-Service ftpsvc -ErrorAction SilentlyContinue) { $ftpRole=$true; $ftp=$true } } catch {}; " +
                "if (-not $ftpRole -and $iis) { " +
                "  try { " +
                "    if ((Get-WindowsOptionalFeature -Online -FeatureName IIS-FTPServer -ErrorAction SilentlyContinue).State -eq 'Enabled') { $ftpRole=$true } " +
                "  } catch {}; " +
                "  try { " +
                "    if ((Get-WindowsFeature -Name Web-Ftp-Server -ErrorAction SilentlyContinue).InstallState -eq 'Installed') { $ftpRole=$true } " +
                "  } catch {} " +
                "}; " +
                "[PSCustomObject]@{ Installed=$ftp; IisInstalled=$iis; FtpRoleInstalled=$ftpRole }",
                DefaultTimeoutMs, DefaultRetries);
            raw = Single(raw);
            return new FtpState(
                Truthy(Prop(raw, "Installed")),
                Truthy(Prop(raw, "IisInstalled")),
                Truthy(Prop(raw, "FtpRoleInstalled")));
        }
        catch
        {
            return FtpState.None;
        }
    }

    // TS: detectModules —— NFS / FTP / WebDAV 三模块可用性（FTP 状态并行细分）
    private static async Task<Modules> DetectModulesAsync(IPsInvoker ps)
    {
        try
        {
            Task<FtpState> ftpTask = DetectFtpStateAsync(ps);
            Task<JsonElement> rawTask = ps.RunJsonAsync(
                // WebDAV: 必须用完整路径 system.webServer/webdav/authoring，短路径 webdav/authoring
                // 在 WebAdministration 模块中解析有缺陷（读取恒返回空、写入静默失败）
                "$wd=$false; try { $null = Get-WebConfigurationProperty -Filter 'system.webServer/webdav/authoring' -PSPath 'MACHINE/WEBROOT/APPHOST' -Name enabled -ErrorAction Stop; $wd=$true } catch {}; " +
                // NFS: 多重检测——Server cmdlet / 客户端服务 / 服务端服务
                "$nfs=$false; try { if (Get-Command Get-NfsShare -ErrorAction SilentlyContinue) { $nfs=$true } elseif (Get-Service NfsClnt -ErrorAction SilentlyContinue) { $nfs=$true } elseif (Get-Service NfsService -ErrorAction SilentlyContinue) { $nfs=$true } } catch {}; " +
                "[PSCustomObject]@{ Nfs=$nfs; Webdav=$wd }",
                DefaultTimeoutMs, DefaultRetries);
            await Task.WhenAll(ftpTask, rawTask);
            FtpState ftp = ftpTask.Result;
            JsonElement raw = Single(rawTask.Result);
            return new Modules(
                Nfs: Truthy(Prop(raw, "Nfs")),
                Ftp: ftp.Installed,
                Webdav: Truthy(Prop(raw, "Webdav")),
                FtpState: ftp);
        }
        catch
        {
            return new Modules(false, false, false, FtpState.None);
        }
    }

    // TS: detectServices —— 逐个查询相关服务状态（键为小写服务名）
    private static async Task<Dictionary<string, JsonElement>> DetectServicesAsync(IPsInvoker ps)
    {
        try
        {
            JsonElement raw = await ps.RunJsonAsync(
                // 逐个查询服务：Get-Service Name1,Name2 在某些服务不存在时会导致进程退出码非 0，
                // execFile 据此抛错使 detectServices 误入 catch 返回空对象。
                // 逐个 Get-Service -ErrorAction SilentlyContinue 避免此问题。
                // NfsClnt = 客户端 NFS 服务（Win10/11），NfsService = 服务端 NFS 服务（Windows Server）
                "@('LanmanServer','NfsService','NfsClnt','ftpsvc','W3SVC') | ForEach-Object { Get-Service -Name $_ -ErrorAction SilentlyContinue } | Where-Object { $_ } | Select-Object Name, Status, StartType",
                DefaultTimeoutMs, DefaultRetries);
            var map = new Dictionary<string, JsonElement>();
            foreach (JsonElement s in ToArray(raw))
            {
                string name = Text(Prop(s, "Name"));
                if (name.Length > 0) map[name.ToLowerInvariant()] = s;
            }
            return map;
        }
        catch
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    // TS: isClientFeatureInstalled —— Windows 可选功能是否已安装（客户端场景）
    private static async Task<bool> IsClientFeatureInstalledAsync(IPsInvoker ps, string featureName)
    {
        try
        {
            JsonElement state = await ps.RunJsonAsync(
                $"(Get-WindowsOptionalFeature -Online -FeatureName {featureName} -ErrorAction SilentlyContinue).State",
                DefaultTimeoutMs, NoRetries);
            return string.Equals(Text(state).Trim(), "enabled", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // TS: isServerFeatureInstalled —— Windows Server 角色是否已安装（服务器场景）
    private static async Task<bool> IsServerFeatureInstalledAsync(IPsInvoker ps, string featureName)
    {
        try
        {
            JsonElement installed = await ps.RunJsonAsync(
                $"(Get-WindowsFeature -Name {featureName} -ErrorAction SilentlyContinue).InstallState",
                DefaultTimeoutMs, NoRetries);
            return string.Equals(Text(installed).Trim(), "installed", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>TS: installProtocol —— 安装 Windows 功能/角色（引导命令；已安装则直接返回）。</summary>
    public static async Task InstallProtocolAsync(IPsInvoker ps, Protocol protocol)
    {
        ProtocolInfo info = ProtocolInfos[protocol];
        string proto = ProtocolNameUpper(protocol);
        if (info.ServerCmd.Length == 0 && info.ClientCmd.Length == 0)
        {
            throw Errors.InvalidParam($"{proto} 协议无需安装（系统内置）");
        }
        bool isServer = await IsWindowsServerAsync(ps);
        string cmd = isServer ? info.ServerCmd : info.ClientCmd;
        if (cmd.Length == 0)
        {
            throw Errors.InvalidParam($"{proto} 协议在当前系统版本上不支持安装");
        }

        Match featureMatch = FeatureNamePattern.Match(cmd);
        string featureName = featureMatch.Success ? featureMatch.Groups[1].Value : "";

        // 预检：是否已安装（best-effort，失败则跳过，不阻塞安装流程）。
        // 注意：不预检"功能是否可用"——Get-WindowsOptionalFeature 在非管理员或某些环境下会静默
        // 失败返回空，导致把"检查失败"误判为"功能不可用"而中止安装；直接尝试安装命令本身是更
        // 可靠的事实来源。
        if (featureName.Length > 0)
        {
            try
            {
                bool alreadyInstalled = isServer
                    ? await IsServerFeatureInstalledAsync(ps, featureName)
                    : await IsClientFeatureInstalledAsync(ps, featureName);
                // 已安装：无需重复安装，直接返回成功
                if (alreadyInstalled) return;
            }
            catch
            {
                // 预检失败（可能缺少权限）：跳过，直接尝试安装
            }
        }

        // 安装 Windows 功能可能耗时较长，给 5 分钟超时
        try
        {
            await ps.RunVoidAsync(cmd, InstallTimeoutMs, NoRetries);
        }
        catch (Exception e)
        {
            string msg = e.Message ?? "";
            // 友好化常见错误
            if (ElevationPattern.IsMatch(msg))
            {
                throw Errors.CommandFailed(
                    $"{proto} 安装需要管理员权限，请以管理员身份运行 WinShare Panel 后重试");
            }
            if (NoMatchPattern.IsMatch(msg))
            {
                throw Errors.CommandFailed(
                    $"{proto} 功能名在当前系统版本上无效，请确认系统支持该协议（{info.Hint}）");
            }
            if (RestartPattern.IsMatch(msg))
            {
                throw Errors.CommandFailed(
                    $"{proto} 安装成功但需要重启计算机才能生效，请保存工作后重启系统");
            }
            // 透传原始错误（已截断）
            throw Errors.CommandFailed($"{proto} 安装失败：{MsgSlice(msg, 200)}");
        }
    }

    // TS: protocol.toUpperCase()
    private static string ProtocolNameUpper(Protocol p) => p switch
    {
        Protocol.Smb => "SMB",
        Protocol.Nfs => "NFS",
        Protocol.Ftp => "FTP",
        Protocol.Webdav => "WEBDAV",
        _ => p.ToString().ToUpperInvariant(),
    };

    // TS: msg.slice(0, 200)
    private static string MsgSlice(string msg, int max) => msg.Length > max ? msg[..max] : msg;

    /// <summary>TS: obj[name] —— 缺失属性等价 undefined。</summary>
    private static JsonElement? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement v) ? v : null;

    /// <summary>TS: Array.isArray(raw) ? raw : [raw] —— 统一成数组遍历（null/undefined 收敛为空）。</summary>
    private static List<JsonElement> ToArray(JsonElement raw) =>
        raw.ValueKind == JsonValueKind.Array
            ? raw.EnumerateArray().Select(e => e.Clone()).ToList()
            : raw.ValueKind == JsonValueKind.Undefined || raw.ValueKind == JsonValueKind.Null
                ? new List<JsonElement>()
                : new List<JsonElement> { raw.Clone() };

    /// <summary>TS: doDetectProtocols 内三处查询恒为单个对象结果；PS ConvertTo-Json 单对象不包数组，双保险解包。</summary>
    private static JsonElement Single(JsonElement raw) =>
        raw.ValueKind == JsonValueKind.Array
            ? (raw.GetArrayLength() > 0 ? raw[0].Clone() : default)
            : raw;

    /// <summary>TS: String(x) —— runPowerShell&lt;string&gt; 结果可能是 JSON 串或标量。</summary>
    private static string Text(JsonElement? el)
    {
        if (el is null) return "";
        JsonElement e = el.Value;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.Array => string.Join(" ", e.EnumerateArray().Select(x => Text(x))),
            _ => e.ToString(),
        };
    }

    private static string Text(JsonElement el) => Text((JsonElement?)el);

    /// <summary>TS: !!value —— JS 布尔真值语义（"false" 字符串仍为真，0 为假）。</summary>
    private static bool Truthy(JsonElement? el)
    {
        if (el is null) return false;
        JsonElement e = el.Value;
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.Number => e.GetDouble() != 0,
            JsonValueKind.String => (e.GetString() ?? "").Length > 0,
            _ => true,
        };
    }
}
