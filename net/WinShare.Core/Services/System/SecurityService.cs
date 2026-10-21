// 移植自 electron/services/security.ts（账号安全体检：弱口令策略层面的可探测项 + 常见合规点）。
// PS 命令串从 TS 原文件逐字复制；TS 的 runPowerShell<RawUser | RawUser[]> 单对象/数组两种形状
// 归一、脏条目过滤、禁用账号跳过等语义一并保留。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;

namespace WinShare.Core.Services.Host;

/// <summary>ISecurityService 实现（账号安全体检）。</summary>
public sealed class SecurityService : ISecurityService
{
    private readonly IPsInvoker _ps;

    public SecurityService(IPsInvoker? ps = null) => _ps = ps ?? PsRunner.Default;

    /// <summary>TS: const DAY = 24 * 3600 * 1000。</summary>
    private const double DayMs = 24 * 3600 * 1000;

    /// <summary>
    /// TS: securityReport() —— Get-LocalUser 一次查询后本地判级（不发第二个命令）。
    /// 判级顺序与 TS 逐字对齐：PasswordRequired === false → fail 并 continue（短路该用户其余检查）。
    /// </summary>
    public async Task<SecurityReport> GetSecurityReportAsync()
    {
        // TS: runPowerShell<RawUser | RawUser[]>('Get-LocalUser | Select-Object Name,Enabled,PasswordRequired,PasswordExpires,PasswordLastSet', { retries: 0 })
        JsonElement raw = await _ps.RunJsonAsync(
            "Get-LocalUser | Select-Object Name,Enabled,PasswordRequired,PasswordExpires,PasswordLastSet",
            PsRunner.DefaultTimeoutMs, 0);
        // TS: (Array.isArray(raw) ? raw : [raw]).filter((u) => u && u.Name)
        var arr = SystemJsCompat.ToArray(raw).FindAll(u => u.ValueKind == JsonValueKind.Object
            && SystemJsCompat.NonEmptyStringProp(u, "Name") is not null);

        var issues = new List<SecurityIssue>();
        long now = SystemJsCompat.NowMs();
        int checkedCount = 0;
        foreach (JsonElement u in arr)
        {
            // TS: if (!u.Enabled) continue —— 禁用账号整体跳过（不出 issue、不计入 checked）
            if (!SystemJsCompat.IsExplicitTrue(u, "Enabled")) continue;
            checkedCount++;
            string name = SystemJsCompat.NonEmptyStringProp(u, "Name")!;

            // TS: if (u.PasswordRequired === false) { issues.push(fail); continue }
            if (SystemJsCompat.IsExplicitFalse(u, "PasswordRequired"))
            {
                issues.Add(new SecurityIssue
                {
                    user = name,
                    level = SecurityLevel.Fail,
                    issue = "账号设置了\"密码永不需要\"，可空口令登录",
                });
                continue;
            }

            // TS: if (u.PasswordExpires === false) issues.push(warn 永不过期)
            if (SystemJsCompat.IsExplicitFalse(u, "PasswordExpires"))
            {
                issues.Add(new SecurityIssue
                {
                    user = name,
                    level = SecurityLevel.Warn,
                    issue = "密码设置为永不过期（共享账号建议定期轮换）",
                });
            }

            // TS: if (u.PasswordLastSet) { set = Date.parse(...); if (!isNaN(set) && now - set > 180*DAY) warn }
            string? lastSet = SystemJsCompat.NonEmptyStringProp(u, "PasswordLastSet");
            if (lastSet is not null)
            {
                double set = SystemJsCompat.DateParseMs(lastSet);
                if (SystemJsCompat.IsFiniteNumber(set) && now - set > 180 * DayMs)
                {
                    issues.Add(new SecurityIssue
                    {
                        user = name,
                        level = SecurityLevel.Warn,
                        issue = $"密码超过 180 天未修改（{SystemJsCompat.IsoDate(set)}）",
                    });
                }
            }
        }

        // TS: return { checked: arr.filter((u) => u.Enabled).length, issues, at: now }
        return new SecurityReport
        {
            Checked = checkedCount,
            issues = issues,
            at = now,
        };
    }
}
