using WinShare.Core.Contracts;

namespace WinShare.App.State;

/// <summary>
/// 告警规则评估器（C# 版 buildAttentionItems，逐条对齐 src/pages/Dashboard.tsx）：
/// 只呈现仪表板上下文里真实可判定的四类：SMB1、访客/不安全来宾登录、空口令/永不过期账户、
/// 磁盘低于阈值。idleAlertMinutes 有意不在此处——没有会话空闲数据，按"不臆造告警"原则省略。
/// </summary>
public static class AlertEvaluator
{
    /// <summary>评估输入（字段与 TS AttentionInput 对齐）。</summary>
    public sealed record AttentionInput
    {
        public AlertRules alertRules { get; init; } = new();
        /// <summary>SMB 配置探测结果；null=数据源不可用（如实跳过该项）。</summary>
        public SmbServerConfig? smbConfig { get; init; }
        public SecurityReport? security { get; init; }
        public IReadOnlyList<DiskUsage> disks { get; init; } = new List<DiskUsage>();
    }

    /// <summary>「需要关注」条目（字段与 TS AttentionItem 对齐；route/attention 供导航透传）。</summary>
    public sealed record AttentionItem
    {
        public string key { get; init; } = "";
        public string level { get; init; } = "warn";   // danger | warn
        public string text { get; init; } = "";
        public string actionLabel { get; init; } = "";
        public string route { get; init; } = "/settings";
        public string? attention { get; init; }
    }

    /// <summary>逐条对齐 Dashboard.tsx buildAttentionItems。</summary>
    public static List<AttentionItem> Evaluate(AttentionInput input)
    {
        var items = new List<AttentionItem>();
        var rules = input.alertRules ?? new AlertRules();

        // ① SMB1
        if (rules.smb1Alert && input.smbConfig is { enableSMB1Protocol: true })
        {
            items.Add(new AttentionItem
            {
                key = "smb1",
                level = "danger",
                text = "SMB1 协议仍在启用，这是被公开攻击的旧协议，建议关闭并观察旧客户端",
                actionLabel = "关闭 SMB1",
                route = "/settings",
                attention = "smb1",
            });
        }

        // ② 访客 / 不安全来宾登录（weakPasswordAlert 门控）
        if (rules.weakPasswordAlert && input.smbConfig is { } cfg)
        {
            var flags = new List<string>();
            if (cfg.enableGuestUserAccess) flags.Add("访客用户访问");
            if (cfg.enableInsecureGuestLogons) flags.Add("不安全来宾登录");
            if (flags.Count > 0)
            {
                items.Add(new AttentionItem
                {
                    key = "guest",
                    level = cfg.enableInsecureGuestLogons ? "danger" : "warn",
                    text = $"已开启{string.Join("与", flags)}，匿名客户端无需有效凭据即可探测共享",
                    actionLabel = "调整访客策略",
                    route = "/settings",
                    attention = "guest",
                });
            }
        }

        // ③ 空口令 / 永不过期账户（weakPasswordAlert 门控）
        if (rules.weakPasswordAlert && input.security is { } sec)
        {
            var issues = sec.issues ?? new List<SecurityIssue>();
            var blank = issues.Where(i => i.level == SecurityLevel.Fail).ToList();
            var neverExpire = issues.Where(i => (i.issue ?? "").Contains("永不过期")).ToList();
            if (blank.Count > 0 || neverExpire.Count > 0)
            {
                var parts = new List<string>();
                if (blank.Count > 0) parts.Add($"{blank.Count} 个启用账户可空口令登录");
                if (neverExpire.Count > 0) parts.Add($"{neverExpire.Count} 个账户密码永不过期");
                items.Add(new AttentionItem
                {
                    key = "password",
                    level = blank.Count > 0 ? "danger" : "warn",
                    text = $"{string.Join("，", parts)}（共检查 {sec.Checked} 个启用账户）",
                    actionLabel = "查看账户",
                    route = "/users",
                    attention = "weak-password",
                });
            }
        }

        // ④ 磁盘低空间（diskLowGb > 0 才告警；阈值分级与 src/utils/shareDefaults.ts diskLevel 对齐：
        //    比例或绝对值任一命中：danger=freePct<=5 || freeGB<=lowGb/4；warn=freePct<=10 || freeGB<=lowGb）
        if (rules.diskLowGb > 0)
        {
            foreach (var d in input.disks ?? new List<DiskUsage>())
            {
                var lvl = DiskLevel(d, lowPct: 10, lowGb: rules.diskLowGb);
                if (lvl is "warn" or "danger")
                {
                    items.Add(new AttentionItem
                    {
                        key = $"disk:{d.drive}",
                        level = lvl == "danger" ? "danger" : "warn",
                        text = $"{d.drive} 盘剩余 {d.freeGB} GB / 共 {d.totalGB} GB，已低于告警阈值 {rules.diskLowGb} GB，共享写入可能失败",
                        actionLabel = "查看共享",
                        route = "/shares",
                        attention = $"disk:{d.drive}",
                    });
                }
            }
        }

        return items;
    }

    /// <summary>TS diskLevel(d, {lowGb})，lowPct 默认 10 固定（Dashboard 调用侧语义）。</summary>
    public static string DiskLevel(DiskUsage? d, int lowPct = 10, int lowGb = 20)
    {
        if (d is null) return "unknown";
        if (d.freePct <= lowPct / 2.0 || d.freeGB <= lowGb / 4.0) return "danger";
        if (d.freePct <= lowPct || d.freeGB <= lowGb) return "warn";
        return "ok";
    }
}
