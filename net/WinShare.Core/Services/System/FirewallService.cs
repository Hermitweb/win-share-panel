// 移植自 electron/services/firewall.ts（防火墙规则引擎：NetSecurity cmdlet，Win8+/Server2012+ 可用）。
// 设计同 TS：所有本应用创建的规则挂 DisplayGroup="WinShare Panel" 分组 + 固定 DisplayName，
// ensure 幂等（已存在不重建）；删除只允许组内规则，绝不误伤系统/第三方规则。
// PS 命令串从 TS 原文件逐字复制（仅把模板字符串替换为 C# 插值 / PsArgs.PsQuote 引用）。
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Ps;

namespace WinShare.Core.Services.Host;

/// <summary>IFirewallService 实现。</summary>
public sealed class FirewallService : IFirewallService
{
    /// <summary>TS: export const FW_GROUP = 'WinShare Panel'。</summary>
    public const string FwGroupConst = "WinShare Panel";

    /// <summary>TS: FW_GROUP（契约面）。</summary>
    public string FwGroup => FwGroupConst;

    // 规则名字符白名单。必须放行 "/"：presetRules 产出的预设名形如
    // "WinShare SMB (445/TCP)"，早先白名单没有 "/" 导致 ensureRules(presetRules(kind))
    // 恒抛 INVALID_PARAM——一键诊断的防火墙修复项与预设建/删规则在运行时全挂（qa-main t1 复现）。
    // 安全性不变：这些名字最终经 psQuote 进 PowerShell 单引号字符串上下文，
    // 单引号内无插值、"/" 与 "(" ")" 均为惰性字符，而真正的注入载体 "'" 仍被白名单挡在外面。
    // TS: const NAME_RE = /^[\w ().\-/]{1,100}$/
    private static readonly Regex NameRegex = new(@"^[A-Za-z0-9_ ().\-/]{1,100}$", RegexOptions.CultureInvariant);

    /// <summary>TS: 端口白名单 /^[\d,-]+$/（仅数字、逗号、连字符）。</summary>
    private static readonly Regex PortsRegex = new(@"^[\d,-]+$", RegexOptions.CultureInvariant);

    private readonly IPsInvoker _ps;

    public FirewallService(IPsInvoker? ps = null) => _ps = ps ?? PsRunner.Default;

    /// <summary>TS: validateDesired(r) —— 先全量校验再写入；校验入参而非归一化结果（契约收紧）。</summary>
    private static FwProtocol ValidateDesired(DesiredRule r)
    {
        if (!NameRegex.IsMatch(r.name ?? "")) throw Errors.InvalidParam($"规则名非法：{r.name}");
        if (!PortsRegex.IsMatch(r.ports ?? "")) throw Errors.InvalidParam($"端口格式非法：{r.ports}");
        // TS: if (r.protocol !== undefined && r.protocol !== 'TCP' && r.protocol !== 'UDP') throw
        // C# 侧契约为 FwProtocol?：null = 未提供；其余值必属 TCP/UDP 之一。
        if (r.protocol is not null and not FwProtocol.TCP and not FwProtocol.UDP)
        {
            throw Errors.InvalidParam("协议非法");
        }
        return r.protocol == FwProtocol.UDP ? FwProtocol.UDP : FwProtocol.TCP;
    }

    /// <summary>TS: listManagedRules()（仅 WinShare Panel 组内）。</summary>
    public async Task<IReadOnlyList<FirewallRule>> ListManagedRulesAsync()
    {
        // TS 原串（两条模板拼接，逐字复制）：
        //   `$rules = @(); try { $rules = @(Get-NetFirewallRule -DisplayGroup ${psQuote(FW_GROUP)} -ErrorAction Stop) } catch { return @() }`
        //   `; $rules | ForEach-Object { $p = $_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue; `
        //   `[PSCustomObject]@{ DisplayName=$_.DisplayName; Enabled=($_.Enabled -eq 'True'); Ports=(($p.LocalPort) -join ',') } }`
        string command =
            $"$rules = @(); try {{ $rules = @(Get-NetFirewallRule -DisplayGroup {PsArgs.PsQuote(FwGroupConst)} -ErrorAction Stop) }} catch {{ return @() }}" +
            $"; $rules | ForEach-Object {{ $p = $_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue; " +
            $"[PSCustomObject]@{{ DisplayName=$_.DisplayName; Enabled=($_.Enabled -eq 'True'); Ports=(($p.LocalPort) -join ',') }} }}";
        JsonElement raw = await _ps.RunJsonAsync(command, PsRunner.DefaultTimeoutMs, 0);

        // TS: Array.isArray(raw) ? raw : raw ? [raw] : []
        var arr = SystemJsCompat.ToArray(raw);
        var result = new List<FirewallRule>();
        foreach (JsonElement r in arr)
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            string? displayName = SystemJsCompat.NonEmptyStringProp(r, "DisplayName");
            if (displayName is null) continue;
            result.Add(new FirewallRule
            {
                name = displayName,
                enabled = SystemJsCompat.IsExplicitTrue(r, "Enabled"),
                // TS: String(r.Ports ?? '')
                ports = SystemJsCompat.StringOf(r.TryGetProperty("Ports", out JsonElement p) ? p : default),
            });
        }
        return result;
    }

    /// <summary>TS: ensureRules(desired)（幂等），返回本次新建的规则名列表。</summary>
    public async Task<IReadOnlyList<string>> EnsureRulesAsync(IReadOnlyList<DesiredRule> desired)
    {
        // TS: if (!Array.isArray(desired) || desired.length === 0) return []
        if (desired is null || desired.Count == 0) return Array.Empty<string>();
        // TS: if (desired.length > 20) throw Errors.invalidParam('单次规则请求过多')
        if (desired.Count > 20) throw Errors.InvalidParam("单次规则请求过多");

        // TS: const validated = desired.map((r) => ({ r, proto: validateDesired(r) }))
        // —— 全量校验先于任何命令：合法A+非法B 不再留下"部分提交"中间态。
        var validated = desired.Select(r => (Rule: r, Proto: ValidateDesired(r))).ToList();

        // TS: const existing = new Set((await listManagedRules()).map((rule) => rule.name))
        var existing = new HashSet<string>((await ListManagedRulesAsync()).Select(rule => rule.name));

        var created = new List<string>();
        foreach ((DesiredRule r, FwProtocol proto) in validated)
        {
            if (existing.Contains(r.name)) continue;
            // TS: r.ports.split(',').map(p => p.trim()).filter(Boolean).map(p => psQuote(p)).join(',')
            string ports = string.Join(",", (r.ports ?? "")
                .Split(',')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Select(PsArgs.PsQuote));
            // TS 原串（两条拼接，逐字复制）：
            //   `New-NetFirewallRule -DisplayName ${psQuote(r.name)} -Direction Inbound -Action Allow `
            //   `-Protocol ${psQuote(proto)} -LocalPort ${ports} -Profile Any -Group ${psQuote(FW_GROUP)}`
            string command =
                $"New-NetFirewallRule -DisplayName {PsArgs.PsQuote(r.name)} -Direction Inbound -Action Allow " +
                $"-Protocol {PsArgs.PsQuote(proto.ToString())} -LocalPort {ports} -Profile Any -Group {PsArgs.PsQuote(FwGroupConst)}";
            await _ps.RunVoidAsync(command, PsRunner.DefaultTimeoutMs, 0);
            created.Add(r.name);
        }
        return created;
    }

    /// <summary>TS: removeRule(name)（按 DisplayName 精确匹配，且必须在 WinShare Panel 组内）。</summary>
    public async Task RemoveRuleAsync(string name)
    {
        if (!NameRegex.IsMatch(name ?? "")) throw Errors.InvalidParam("规则名非法");
        // TS 原串（三条拼接，逐字复制）：
        //   `$ids = @(Get-NetFirewallRule -DisplayGroup ${psQuote(FW_GROUP)} -ErrorAction SilentlyContinue | `
        //   `Where-Object { $_.DisplayName -eq ${psQuote(name)} } | Select-Object -ExpandProperty Name); `
        //   `if ($ids.Count -gt 0) { $ids | Remove-NetFirewallRule }`
        string command =
            $"$ids = @(Get-NetFirewallRule -DisplayGroup {PsArgs.PsQuote(FwGroupConst)} -ErrorAction SilentlyContinue | " +
            $"Where-Object {{ $_.DisplayName -eq {PsArgs.PsQuote(name)} }} | Select-Object -ExpandProperty Name); " +
            $"if ($ids.Count -gt 0) {{ $ids | Remove-NetFirewallRule }}";
        await _ps.RunVoidAsync(command, PsRunner.DefaultTimeoutMs, 0);
    }

    /// <summary>
    /// TS: presetRules(kind, opts?) —— 常用预设：SMB(445) / FTP 控制(21)+被动段 / WebDAV(80) / QUIC；
    /// 参数化端口供 UI 传入。switch 分支与默认抛错逐字对齐。
    /// </summary>
    public IReadOnlyList<DesiredRule> PresetRules(string kind, PresetRuleOptions? opts = null)
    {
        switch (kind)
        {
            case "smb":
                return new[] { new DesiredRule { name = "WinShare SMB (445/TCP)", ports = "445" } };
            case "ftp":
                return new[]
                {
                    new DesiredRule { name = "WinShare FTP (21/TCP)", ports = "21" },
                    new DesiredRule { name = "WinShare FTP Passive (50000-51000/TCP)", ports = "50000-51000" },
                };
            case "ftpPassive":
                {
                    // TS: Number.isInteger(opts?.passiveFrom) ? opts.passiveFrom : 50000（非整数静默回落默认）
                    int from = opts?.passiveFrom is int f && IsInteger(f) ? f : 50000;
                    int to = opts?.passiveTo is int t && IsInteger(t) ? t : 51000;
                    if (from < 1 || to > 65535 || from > to) throw Errors.InvalidParam("被动端口范围非法");
                    return new[]
                    {
                        new DesiredRule
                        {
                            name = $"WinShare FTP Passive ({from}-{to}/TCP)",
                            ports = $"{from}-{to}",
                        },
                    };
                }
            case "webdav":
                return new[] { new DesiredRule { name = "WinShare WebDAV (80/TCP)", ports = "80" } };
            case "quic":
                return new[]
                {
                    new DesiredRule { name = "WinShare SMB QUIC (445/UDP)", ports = "445", protocol = FwProtocol.UDP },
                };
            default:
                // TS: `未知防火墙预设：${validateName(kind) ? kind : '***'}`
                throw Errors.InvalidParam($"未知防火墙预设：{(PsArgs.ValidateName(kind) ? kind : "***")}");
        }
    }

    /// <summary>TS: Number.isInteger(v)——本域入参为 int?，恒为整数；保留以显式对齐语义。</summary>
    private static bool IsInteger(int v) => true;
}
