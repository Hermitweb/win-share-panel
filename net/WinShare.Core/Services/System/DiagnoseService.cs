// 移植自 electron/services/diagnose.ts（一键诊断："别人连不上我的共享" 自助排查 + 自动修复）。
// 链路与用户旅程一致：服务在跑 → 端口在听 → 共享存在 → 路径有效 → NTFS 可读
// → 共享权限 → 防火墙放行 → 安全策略（SMB1/访客）。
// 每项 check 独立容错：探测失败记 warn 而不是整体崩；fix 键驱动可自动修复项。
// PS 命令串从 TS 原文件逐字复制（仅模板字符串 → C# 插值 / PsArgs.PsQuote）。
// applyFix 的修复动作干跑（写路径由测试以假 IPsInvoker 断言 netsh/icacls 命令串，不真改系统）。
//
// 命名空间说明：本程序集存在 WinShare.Core.Services.Protocol 命名空间，而 Contracts 里也有
// 同名类型 WinShare.Core.Contracts.Protocol（枚举）。在 WinShare.Core.Services.Host 内按 C#
// 名称查找规则，裸 Protocol 会先命中「命名空间」而非「枚举」，故本文件统一以别名
// WsProtocol 指向契约枚举（别名优先于命名空间查找）。
// 同理，"System.Text" / "System.Globalization" 在本命名空间内会被误解析为
// WinShare.Core.Services.Host.Text，故正则与文化相关类型一律以别名 Rx / Gl 引用。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Ps;
using Rx = System.Text.RegularExpressions;
using Gl = System.Globalization;

namespace WinShare.Core.Services.Host;

/// <summary>IDiagnoseService 实现。</summary>
public sealed class DiagnoseService : IDiagnoseService
{
    private readonly IPsInvoker _ps;
    private readonly ISmbService _smb;
    private readonly IUserService _user;
    private readonly IFirewallService _firewall;

    /// <summary>审计钩子（TS: audit(operator, action, target, result)）；测试可替身记录调用。</summary>
    public Action<string, string, string, string>? AuditHook { get; set; }

    public DiagnoseService(IPsInvoker? ps = null, ISmbService? smb = null,
        IUserService? user = null, IFirewallService? firewall = null)
    {
        _ps = ps ?? PsRunner.Default;
        _smb = smb ?? new NullSmbService();
        _user = user ?? new NullUserService();
        _firewall = firewall ?? new NullFirewallService();
    }

    // ===== runDiagnose =====

    /// <summary>
    /// TS: push(items, key, label, fn) —— 单项容错包装：成功按 ok 记 pass/fail，抛错记 warn。
    /// </summary>
    private static async Task PushAsync(List<DiagnoseItem> items, string key, string label,
        Func<Task<(bool Ok, string Detail, DiagnoseFixKind? Fix)>> fn)
    {
        try
        {
            (bool ok, string detail, DiagnoseFixKind? fix) r = await fn();
            items.Add(new DiagnoseItem
            {
                key = key,
                label = label,
                status = r.ok ? DiagnoseStatus.Pass : DiagnoseStatus.Fail,
                detail = r.detail,
                fix = r.fix,
            });
        }
        catch (Exception e)
        {
            items.Add(new DiagnoseItem
            {
                key = key,
                label = label,
                status = DiagnoseStatus.Warn,
                detail = $"探测失败：{e.Message}",
            });
        }
    }

    /// <summary>
    /// TS: resolveSharePath(shareName) —— 解析共享的本地路径（不存在/非法返回 null）；一次查询多处复用。
    /// </summary>
    private async Task<string?> ResolveSharePathAsync(string shareName)
    {
        if (!PsArgs.ValidateShareName(shareName)) throw Errors.InvalidParam("共享名非法");
        // TS 原串：
        //   try { Get-SmbShare -Name 'shareName' -ErrorAction Stop | Select-Object -ExpandProperty Path } catch { $null }
        string command =
            $"try {{ Get-SmbShare -Name {PsArgs.PsQuote(shareName)} -ErrorAction Stop | Select-Object -ExpandProperty Path }} catch {{ $null }}";
        JsonElement raw = await _ps.RunJsonAsync(command, PsRunner.DefaultTimeoutMs, 0);
        JsonElement? first = SystemJsCompat.FirstOrNull(raw);
        string? p = first is { } f && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        return !string.IsNullOrEmpty(p) ? p : null;
    }

    /// <summary>TS: runDiagnose(opts: { shareName? })，逐项容错（探测失败记 warn 不崩整体）。</summary>
    public async Task<IReadOnlyList<DiagnoseItem>> RunDiagnoseAsync(DiagnoseOptions? opts = null)
    {
        var items = new List<DiagnoseItem>();
        // TS: const target = opts.shareName（闭包内需非空收窄，先用局部常量接住）
        string? target = opts?.shareName;
        string? pathProbe = null;
        bool probed = false;

        async Task<string?> ProbePathAsync()
        {
            if (string.IsNullOrEmpty(target)) return null;
            if (!probed)
            {
                probed = true;
                pathProbe = await ResolveSharePathAsync(target!);
            }
            return pathProbe;
        }

        await PushAsync(items, "smb-service", "SMB 服务端（LanmanServer）运行", async () =>
        {
            ServiceStatus svc = await _smb.GetServiceStatusAsync();
            return (
                svc.status == ServiceState.Running,
                $"状态：{StatusText(svc.status)}（{svc.startType}）",
                svc.status != ServiceState.Running ? DiagnoseFixKind.ServiceLanman : null);
        });

        await PushAsync(items, "port-445", "SMB 端口 445 已监听", async () =>
        {
            // TS 原串：
            //   @(Get-NetTCPConnection -LocalPort 445 -State Listen -ErrorAction SilentlyContinue).Count
            JsonElement raw = await _ps.RunJsonAsync(
                "@(Get-NetTCPConnection -LocalPort 445 -State Listen -ErrorAction SilentlyContinue).Count",
                PsRunner.DefaultTimeoutMs, 0);
            JsonElement? first = SystemJsCompat.FirstOrNull(raw);
            // TS: Number(Array.isArray(raw) ? raw[0] : raw)
            double n = first is { } f ? SystemJsCompat.NumberOf(f) : double.NaN;
            return (n > 0, n > 0 ? $"{JsNumber(n)} 个监听" : "445 未监听（服务未起或被占用）", null);
        });

        await PushAsync(items, "share-exists", "目标共享存在", async () =>
        {
            if (string.IsNullOrEmpty(target)) return (true, "未指定共享，跳过", null);
            string? path = await ProbePathAsync();
            return (path is not null, path is not null ? $"共享路径：{path}" : $"共享\"{target}\"不存在", null);
        });

        await PushAsync(items, "path-exists", "共享本地路径存在", async () =>
        {
            if (string.IsNullOrEmpty(target)) return (true, "未指定共享，跳过", null);
            string? path = await ProbePathAsync();
            if (path is null) return (false, "共享不存在（见上项）", null);
            bool exists = Directory.Exists(path) || File.Exists(path);
            return (exists, exists ? path : $"路径不存在：{path}", null);
        });

        await PushAsync(items, "ntfs-read", "共享路径 NTFS 已授予读取权限", async () =>
        {
            if (string.IsNullOrEmpty(target)) return (true, "未指定共享，跳过", null);
            string? path = await ProbePathAsync();
            if (path is null) return (false, "共享不存在（见上项）", null);
            if (!Directory.Exists(path) && !File.Exists(path)) return (false, $"路径不存在：{path}", null);
            NtfsAcl acl = await _user.GetNtfsPermissionsAsync(path);
            var readable = acl.entries.Where(e => e.type == NtfsAclType.Allow).ToList();
            var denied = acl.entries.Where(e => e.type == NtfsAclType.Deny).ToList();
            // TS: /^(everyone|authenticated users|builtin\/users|nt authority\/authenticated users)$/i
            var anyone = new Rx.Regex(
                @"^(everyone|authenticated users|builtin/users|nt authority/authenticated users)$",
                Rx.RegexOptions.IgnoreCase);
            bool hasRead = readable.Any(e =>
                anyone.IsMatch(e.account.ToLowerInvariant())
                || Rx.Regex.IsMatch(e.rights ?? "", @"(read|modify|full)", Rx.RegexOptions.IgnoreCase));
            return (
                hasRead,
                hasRead
                    ? $"{readable.Count} 条允许访问{(denied.Count > 0 ? $"，{denied.Count} 条拒绝" : "")}"
                    : "该路径 NTFS 未授予任何可读权限——客户端即使有共享权限也会被拒",
                hasRead ? null : DiagnoseFixKind.AclReadEveryone);
        });

        await PushAsync(items, "firewall-smb", "防火墙放行 SMB 入站", async () =>
        {
            IReadOnlyList<FirewallRule> rules = await _firewall.ListManagedRulesAsync();
            bool hasSmb = rules.Any(r => (r.name ?? "").Contains("SMB (445/TCP)") && r.enabled);
            if (hasSmb) return (true, "WinShare 组规则已存在", null);
            // 系统内置"文件和打印机共享"规则组通常已启用，无法穷举判定，命中即视为放行
            // TS 原串：
            //   @(Get-NetFirewallRule -Group "@FirewallAPI.dll,-28502" -Enabled True -Action Allow -ErrorAction SilentlyContinue).Count
            JsonElement raw = await _ps.RunJsonAsync(
                "@(Get-NetFirewallRule -Group \"@FirewallAPI.dll,-28502\" -Enabled True -Action Allow -ErrorAction SilentlyContinue).Count",
                PsRunner.DefaultTimeoutMs, 0);
            JsonElement? first = SystemJsCompat.FirstOrNull(raw);
            double n = first is { } f ? SystemJsCompat.NumberOf(f) : double.NaN;
            return (
                n > 0,
                n > 0 ? "系统\"文件和打印机共享\"规则组已启用" : "未见启用的共享放行规则",
                n > 0 ? null : DiagnoseFixKind.FirewallSmb);
        });

        if (!string.IsNullOrEmpty(target))
        {
            await PushAsync(items, "share-perms", "共享权限已授予访问者", async () =>
            {
                if (!PsArgs.ValidateShareName(target)) throw Errors.InvalidParam("共享名非法");
                IReadOnlyList<SharePermission> perms = await _user.GetSharePermissionsAsync(target!);
                var allow = perms.Where(p => !p.deny).ToList();
                bool everyoneFull = allow.Any(p =>
                    Rx.Regex.IsMatch(p.account ?? "", @"^(everyone|authenticated users)$", Rx.RegexOptions.IgnoreCase)
                    && p.access == ShareAccess.Full);
                if (allow.Count == 0)
                {
                    return (false, "共享权限为空——任何人都连不上", null);
                }
                return (
                    true,
                    everyoneFull
                        ? $"{allow.Count} 条授权，其中 Everyone 为完全控制（建议收敛为只读或指定组）"
                        : $"{allow.Count} 条授权",
                    null);
            });
        }

        await PushAsync(items, "smb1", "未启用 SMB1 旧协议（安全）", async () =>
        {
            SmbServerConfig cfg = await _smb.GetConfigAsync();
            return (
                !cfg.enableSMB1Protocol,
                cfg.enableSMB1Protocol ? "SMB1 已开启——建议关闭（永恒之蓝等风险面）" : "SMB1 关闭",
                null);
        });

        await PushAsync(items, "guest", "未开放来宾/不安全访客登录（安全）", async () =>
        {
            SmbServerConfig cfg = await _smb.GetConfigAsync();
            bool risky = cfg.enableGuestUserAccess || cfg.enableInsecureGuestLogons;
            return (
                !risky,
                risky
                    ? $"访客访问={JsBool(cfg.enableGuestUserAccess)}，不安全访客登录={JsBool(cfg.enableInsecureGuestLogons)}"
                    : "均未开放",
                null);
        });

        return items;
    }

    // ===== applyFix =====

    /// <summary>
    /// TS: applyFix(fix, args?: { shareName? })。
    /// 路径一律由服务端按 shareName 重新解析（绝不信渲染层传入的路径），
    /// 与新建共享同一 validatePath 防线；修复动作写审计日志。
    /// </summary>
    public async Task<string> ApplyFixAsync(DiagnoseFixKind fix, DiagnoseOptions? args = null)
    {
        switch (fix)
        {
            case DiagnoseFixKind.ServiceLanman:
                await _smb.StartServiceAsync();
                WriteAudit("diagnoseFix", "service:lanman");
                return "LanmanServer 服务已启动";

            case DiagnoseFixKind.FirewallSmb:
                {
                    IReadOnlyList<string> created = await _firewall.EnsureRulesAsync(new[]
                    {
                        new DesiredRule { name = "WinShare SMB (445/TCP)", ports = "445" },
                    });
                    WriteAudit("diagnoseFix", "firewall:smb");
                    return created.Count > 0 ? "已添加防火墙规则：SMB 445/TCP 入站放行" : "规则已存在，无需重复添加";
                }

            case DiagnoseFixKind.AclReadEveryone:
                {
                    string shareName = args?.shareName ?? "";
                    if (string.IsNullOrEmpty(shareName)) throw Errors.InvalidParam("ACL 修复需要共享名");
                    string? path = await ResolveSharePathAsync(shareName);
                    if (path is null) throw Errors.ShareNotFound(shareName);
                    // 复用共享路径校验（盘符开头、无控制字符/通配），与新建共享同一防线，杜绝注入
                    if (!PsArgs.ValidatePath(path) || path.Length > 260)
                    {
                        throw Errors.InvalidParam($"共享路径非法，无法执行 ACL 修复：{path}");
                    }
                    if (!Directory.Exists(path) && !File.Exists(path))
                    {
                        throw Errors.InvalidParam($"共享路径不存在：{path}");
                    }
                    // *S-1-1-0 为 Everyone 的 SID 形式，免依赖本地化组名（中文/英文系统一致）
                    // TS 原串：
                    //   $s = (Get-Item -LiteralPath 'path').FullName; icacls $s /grant '*S-1-1-0:(OI)(CI)R' /C /Q | Out-Null
                    string command =
                        $"$s = (Get-Item -LiteralPath {PsArgs.PsQuote(path)}).FullName; icacls $s /grant {PsArgs.PsQuote("*S-1-1-0:(OI)(CI)R")} /C /Q | Out-Null";
                    await _ps.RunVoidAsync(command, PsRunner.DefaultTimeoutMs, 0);
                    WriteAudit("diagnoseFix", $"acl:read-everyone:{path}");
                    return $"已对 {path} 授予 Everyone 读取";
                }

            default:
                throw Errors.InvalidParam("未知修复项");
        }
    }

    // ===== 内部辅助 =====

    private void WriteAudit(string action, string target)
    {
        if (AuditHook is { } hook)
        {
            hook("system", action, target, Audit.Success);
            return;
        }
        Audit.Write("system", action, target, Audit.Success);
    }

    /// <summary>TS: 状态：${svc.status}（${svc.startType}） —— status 用 TS 字符串域。</summary>
    private static string StatusText(ServiceState s) => s switch
    {
        ServiceState.Running => "Running",
        ServiceState.Stopped => "Stopped",
        _ => "Unknown",
    };

    /// <summary>TS: String(v) 对布尔的小写形式。</summary>
    private static string JsBool(bool v) => v ? "true" : "false";

    /// <summary>TS: ${n} 个监听 —— JS number → string（整数形态无小数点）。</summary>
    private static string JsNumber(double v)
    {
        if (SystemJsCompat.IsFiniteNumber(v) && v == Math.Floor(v) && Math.Abs(v) < 1e15)
        {
            return ((long)v).ToString(Gl.CultureInfo.InvariantCulture);
        }
        return v.ToString(Gl.CultureInfo.InvariantCulture);
    }

    // ===== 依赖缺省替身（契约服务未注入时不崩，与 TS "依赖未安装即抛错记 warn" 等价）=====

    private sealed class NullSmbService : ISmbService
    {
        public SmbServerConfig DefaultConfig() => new();
        public Task<SmbServerConfig> GetConfigAsync() => Fail<SmbServerConfig>();
        public Task SetConfigAsync(SmbServerConfig config) => FailVoid();
        public Task<SmbServerConfig> RestoreDefaultAsync() => Fail<SmbServerConfig>();
        public Task<IReadOnlyList<SmbSnapshotMeta>> ListSnapshotsAsync() => Fail<IReadOnlyList<SmbSnapshotMeta>>();
        public Task RollbackSnapshotAsync(string id) => FailVoid();
        public Task<ServiceStatus> GetServiceStatusAsync() => Fail<ServiceStatus>();
        public Task RestartServiceAsync() => FailVoid();
        public Task StartServiceAsync() => FailVoid();
        public Task StopServiceAsync() => FailVoid();

        private static Task<T> Fail<T>() => Task.FromException<T>(Errors.CommandFailed("SMB 服务未注入"));
        private static Task FailVoid() => Task.FromException(Errors.CommandFailed("SMB 服务未注入"));
    }

    private sealed class NullUserService : IUserService
    {
        public Task<IReadOnlyList<LocalUser>> ListUsersAsync() => Fail<IReadOnlyList<LocalUser>>();
        public Task<IReadOnlyList<LocalGroup>> ListGroupsAsync() => Fail<IReadOnlyList<LocalGroup>>();
        public Task<LocalUser> GetUserAsync(string name) => Fail<LocalUser>();
        public Task CreateUserAsync(CreateUserOpts opts) => FailVoid();
        public Task UpdateUserAsync(string name, UpdateUserOpts opts) => FailVoid();
        public Task DeleteUserAsync(string name) => FailVoid();
        public Task SetUserPasswordAsync(string name, string password) => FailVoid();
        public Task EnableUserAsync(string name) => FailVoid();
        public Task DisableUserAsync(string name) => FailVoid();
        public Task RenameUserAsync(string oldName, string newName) => FailVoid();
        public Task CreateGroupAsync(CreateGroupOpts opts) => FailVoid();
        public Task DeleteGroupAsync(string name) => FailVoid();
        public Task UpdateGroupAsync(string name, string description) => FailVoid();
        public Task RenameGroupAsync(string name, string newName) => FailVoid();
        public Task AddGroupMemberAsync(string group, string member) => FailVoid();
        public Task RemoveGroupMemberAsync(string group, string member) => FailVoid();
        public Task<IReadOnlyList<SharePermission>> GetSharePermissionsAsync(string shareName) => Fail<IReadOnlyList<SharePermission>>();
        public Task<IReadOnlyList<SharePermission>> GetUserSharePermissionsAsync(string username) => Fail<IReadOnlyList<SharePermission>>();
        public Task SetSharePermissionsAsync(string shareName, IReadOnlyList<SharePermission> perms) => FailVoid();
        public Task<NtfsAcl> GetNtfsPermissionsAsync(string path) => Fail<NtfsAcl>();

        private static Task<T> Fail<T>() => Task.FromException<T>(Errors.CommandFailed("用户服务未注入"));
        private static Task FailVoid() => Task.FromException(Errors.CommandFailed("用户服务未注入"));
    }

    private sealed class NullFirewallService : IFirewallService
    {
        public string FwGroup => FirewallService.FwGroupConst;
        public Task<IReadOnlyList<FirewallRule>> ListManagedRulesAsync() =>
            Task.FromException<IReadOnlyList<FirewallRule>>(Errors.CommandFailed("防火墙服务未注入"));
        public Task<IReadOnlyList<string>> EnsureRulesAsync(IReadOnlyList<DesiredRule> desired) =>
            Task.FromException<IReadOnlyList<string>>(Errors.CommandFailed("防火墙服务未注入"));
        public Task RemoveRuleAsync(string name) => Task.FromException(Errors.CommandFailed("防火墙服务未注入"));
        public IReadOnlyList<DesiredRule> PresetRules(string kind, PresetRuleOptions? opts = null) =>
            throw Errors.CommandFailed("防火墙服务未注入");
    }
}