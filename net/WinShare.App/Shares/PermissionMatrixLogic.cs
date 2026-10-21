using System.Text;
using System.Text.Json;
using WinShare.Core.Contracts;

namespace WinShare.App.Shares;

/// <summary>
/// 权限矩阵纯逻辑（逐条对齐 src/components/PermissionMatrix.tsx 的模块级纯函数
/// 与页面聚合语义；矩阵 UI 在 Users 页使用，这里只交付计算层）：
/// - PickAccess ≙ pickAccess：同一账号多条权限按安全语义取最严格（Deny &gt; Full &gt; Change &gt; Read）
/// - BuildMatrix ≙ 页面 effect 内的按账号分组聚合
/// - IsWideAccount/EscapeHtml/BuildCsv/BuildJson/BuildHtmlReport ≙ 导出三件套
/// </summary>
public static class PermissionMatrixLogic
{
    /// <summary>访问级别取值（TS Access 联合）：Full/Change/Read/Deny/无条目"-"。</summary>
    public static class Access
    {
        public const string Full = "Full";
        public const string Change = "Change";
        public const string Read = "Read";
        public const string Deny = "Deny";
        public const string None = "-";
    }

    /// <summary>TS ACCESS_LABEL。</summary>
    public static readonly string[] AccessLabels = ["完全", "更改", "只读", "拒绝", "-"];

    /// <summary>TS PRIORITY：Deny=0 最严格；C# 侧 Deny 由 (deny=true 或 access=NoAccess) 派生。</summary>
    static readonly Dictionary<string, int> Priority = new()
    {
        [Access.Deny] = 0, [Access.Full] = 1, [Access.Change] = 2, [Access.Read] = 3,
    };

    /// <summary>单条权限 → 显示访问级别（PermissionDrawer.toRow 同规则）。</summary>
    public static string AccessOf(SharePermission p) =>
        p.deny || p.access == ShareAccess.NoAccess ? Access.Deny : p.access.ToString();

    /// <summary>
    /// TS pickAccess：同一账号在多个权限条目中时，按安全语义取最严格（Deny 优先）。
    /// 无条目返回 "-"。
    /// </summary>
    public static string PickAccess(IReadOnlyList<SharePermission>? perms)
    {
        if (perms is null || perms.Count == 0) return Access.None;
        return perms.Select(AccessOf).OrderBy(a => Priority[a]).First();
    }

    /// <summary>TS 页面聚合：按 share → account 分组 → PickAccess，无授权账号落 "-"。</summary>
    public static Dictionary<string, Dictionary<string, string>> BuildMatrix(
        IReadOnlyList<(Share share, IReadOnlyList<SharePermission> perms)> permLists,
        IReadOnlyList<(string account, string type)> accounts)
    {
        var matrix = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (share, perms) in permLists)
        {
            var grouped = new Dictionary<string, List<SharePermission>>();
            foreach (var p in perms ?? new List<SharePermission>())
            {
                if (!grouped.TryGetValue(p.account, out var list))
                    grouped[p.account] = list = new List<SharePermission>();
                list.Add(p);
            }
            var row = new Dictionary<string, string>();
            foreach (var (account, _) in accounts)
                row[account] = grouped.TryGetValue(account, out var ps) ? PickAccess(ps) : Access.None;
            matrix[share.name] = row;
        }
        return matrix;
    }

    /// <summary>
    /// TS isWideAccount：Everyone/Authenticated Users/Guests 等"代表一批人"的宽权限账号；
    /// Windows 侧可能带机器/域前缀（NT AUTHORITY\...），取末段比对。
    /// </summary>
    public static bool IsWideAccount(string name)
    {
        var bare = name.Contains('\\') ? name[(name.LastIndexOf('\\') + 1)..] : name;
        return bare.Trim().ToLowerInvariant() switch
        {
            "everyone" or "authenticated users" or "guests" => true,
            _ => false,
        };
    }

    /// <summary>
    /// TS escapeHtml：共享名/账号名/路径等一切来自系统的字符串拼进 HTML 前必经转义；
    /// & 必须最先替换，否则后续实体会被二次转义。
    /// </summary>
    public static string EscapeHtml(object? value)
    {
        var s = value?.ToString() ?? "";
        return s.Replace("&", "&amp;")
               .Replace("<", "&lt;")
               .Replace(">", "&gt;")
               .Replace("\"", "&quot;")
               .Replace("'", "&#39;");
    }

    static string Label(string a) => a switch
    {
        Access.Full => "完全",
        Access.Change => "更改",
        Access.Read => "只读",
        Access.Deny => "拒绝",
        _ => "-",
    };

    static string AccessClass(string a) => a switch
    {
        Access.Full => "full",
        Access.Change => "change",
        Access.Read => "read",
        Access.Deny => "deny",
        _ => "none",
    };

    /// <summary>TS exportCsv：BOM + 全格引号（"→""）+ 逗号列、换行行。</summary>
    public static string BuildCsv(
        IReadOnlyList<Share> shares,
        IReadOnlyList<(string account, string type)> accounts,
        Dictionary<string, Dictionary<string, string>> matrix)
    {
        var sb = new StringBuilder("\uFEFF");
        var header = new List<string> { "共享" };
        header.AddRange(accounts.Select(a => a.account));
        sb.AppendLine(string.Join(",", header.Select(Quote)));
        foreach (var s in shares)
        {
            var row = matrix.TryGetValue(s.name, out var m) ? m : new Dictionary<string, string>();
            var cells = new List<string> { s.name };
            cells.AddRange(accounts.Select(a =>
                Label(row.TryGetValue(a.account, out var v) ? v : Access.None)));
            sb.AppendLine(string.Join(",", cells.Select(Quote)));
        }
        return sb.ToString();
    }

    static string Quote(string c) => "\"" + c.Replace("\"", "\"\"") + "\"";

    /// <summary>TS exportJson：{ exportedAt, shares: [{ name, path, permissions: [...] }] }。</summary>
    public static string BuildJson(
        IReadOnlyList<Share> shares,
        IReadOnlyList<(string account, string type)> accounts,
        Dictionary<string, Dictionary<string, string>> matrix,
        DateTimeOffset exportedAt)
    {
        var data = new
        {
            exportedAt = exportedAt.ToString("O").Replace("+00:00", "Z"),
            shares = shares.Select(s => new
            {
                name = s.name,
                path = s.path,
                permissions = accounts.Select(a => new
                {
                    account = a.account,
                    accountType = a.type,
                    access = matrix.TryGetValue(s.name, out var m) && m.TryGetValue(a.account, out var v) ? v : Access.None,
                }).ToList(),
            }).ToList(),
        };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// TS buildHtmlReport：自包含单页 HTML 审计报告（内联样式、无外链、无脚本，打印友好）。
    /// 一切系统字符串经 EscapeHtml 后才拼进模板，防共享名/账号名注入标记。
    /// </summary>
    public static string BuildHtmlReport(
        IReadOnlyList<Share> shares,
        IReadOnlyList<(string account, string type)> accounts,
        Dictionary<string, Dictionary<string, string>> matrix,
        DateTime generatedAt)
    {
        string AccessOf(string shareName, string account) =>
            matrix.TryGetValue(shareName, out var m) && m.TryGetValue(account, out var v) ? v : Access.None;

        var rank = new[] { Access.Full, Access.Change, Access.Read, Access.Deny };
        var shareRows = shares.Select(s =>
        {
            var granted = accounts
                .Select(a => AccessOf(s.name, a.account))
                .Where(v => v != Access.None)
                .ToList();
            return (s, granted, highest: rank.FirstOrDefault(k => granted.Contains(k)));
        }).ToList();

        var totalEntries = shareRows.Sum(r => r.granted.Count);
        var wideFindings = accounts
            .Where(a => IsWideAccount(a.account))
            .Select(a => (account: a, rows: shareRows.Where(r => AccessOf(r.s.name, a.account) != Access.None).ToList()))
            .Where(f => f.rows.Count > 0)
            .ToList();

        var header =
            $"<header>\n<h1>谁能访问什么 · 共享权限审计报告</h1>\n" +
            $"<p class=\"meta\">生成时间：{EscapeHtml(generatedAt.ToString())}（本地） · {EscapeHtml(generatedAt.ToUniversalTime().ToString("O"))}（ISO）</p>\n" +
            $"<p class=\"meta\">概览：共享 {shares.Count} 个 · 账号 {accounts.Count} 个 · 共享权限条目 {totalEntries} 条</p>\n</header>";

        var wideSection = wideFindings.Count > 0
            ? "<h2>宽权限账号提示</h2>\n<div class=\"warnbox\">\n<ul>" +
              string.Join("", wideFindings.Select(f =>
                  $"<li class=\"wide\">{EscapeHtml(f.account.account)}（{EscapeHtml(f.account.type == "Group" ? "组" : "用户")}）：" +
                  $"{string.Join("、", f.rows.Select(r => EscapeHtml(r.s.name)))}</li>")) +
              "</ul>\n<p>风险说明：Everyone / Authenticated Users 等宽权限账号分别代表\"所有访客（含匿名）\"与\"所有已登录用户\"，一旦被授予「完全 / 更改」，任意满足条件的网络用户都可写入甚至删除共享数据，请重点复核。</p>\n</div>"
            : "<h2>宽权限账号提示</h2>\n<div class=\"okbox\">\n<p>未发现 Everyone / Authenticated Users / Guests 等宽权限账号获得共享授权。风险说明：此类宽权限账号若被授予「完全 / 更改」，任意网络用户都可能读写共享数据，请避免。</p>\n</div>";

        var overviewRows = string.Join("", shareRows.Select(r =>
            $"<tr><td>{EscapeHtml(r.s.name)}</td><td>{EscapeHtml(r.s.path)}</td><td>{EscapeHtml(ProtocolLabel(r.s.protocol))}</td>" +
            $"<td class=\"num\">{r.granted.Count}</td><td class=\"{(r.highest is null ? "" : "access-" + AccessClass(r.highest))}\">" +
            $"{(r.highest is null ? "无授权" : Label(r.highest))}</td></tr>"));
        var overviewTable =
            $"<h2>共享概览</h2>\n<table>\n<thead><tr><th>名称</th><th>路径</th><th>协议</th><th>授权数</th><th>最高权限</th></tr></thead>\n" +
            $"<tbody>{overviewRows}</tbody>\n</table>";

        var accountHeaders = string.Join("", accounts.Select(a =>
        {
            var wide = IsWideAccount(a.account);
            return $"<th{(wide ? " class=\"wide\"" : "")}>{EscapeHtml(a.account)}{(wide ? "（宽权限）" : "")}</th>";
        }));
        var detailRows = string.Join("", shares.Select(s =>
        {
            var cells = string.Join("", accounts.Select(a =>
            {
                var v = AccessOf(s.name, a.account);
                var wideCell = IsWideAccount(a.account) && v != Access.None ? " wide" : "";
                return $"<td class=\"access-{AccessClass(v)}{wideCell}\">{Label(v)}</td>";
            }));
            return $"<tr><td>{EscapeHtml(s.name)}</td>{cells}</tr>";
        }));
        var detailTable =
            $"<h2>账号 × 共享权限明细</h2>\n<table>\n<thead><tr><th>账号 / 共享</th>{accountHeaders}</tr></thead>\n" +
            $"<tbody>{detailRows}</tbody>\n</table>\n" +
            "<p class=\"meta\">图例：完全 = 读取/写入/删除/改权限；更改 = 读取/写入；只读 = 仅读取；拒绝 = 显式拒绝；- = 无共享权限条目。</p>";

        var footer =
            "<footer>\n<p><strong>边界说明：</strong>本报告仅共享权限（Share）层面统计，未合并计算 NTFS 有效权限，不代表任何账号最终实际可访问的范围；如需有效权限请结合 NTFS ACL 复核。</p>\n" +
            "<p>由 WinShare Panel 导出 · 自包含单页 HTML（内联样式，无脚本、无外部资源），可直接打印存档。</p>\n</footer>";

        var parts = new[]
        {
            "<!DOCTYPE html>",
            "<html lang=\"zh-CN\">",
            "<head>",
            "<meta charset=\"utf-8\" />",
            "<title>谁能访问什么 · 共享权限审计报告</title>",
            $"<style>{ReportCss}</style>",
            "</head>",
            "<body>",
            header,
            wideSection,
            overviewTable,
            detailTable,
            footer,
            "</body>",
            "</html>",
        };
        return string.Join("\n", parts);
    }

    static string ProtocolLabel(Protocol p) => p switch
    {
        Protocol.Smb => "SMB",
        Protocol.Nfs => "NFS",
        Protocol.Ftp => "FTP",
        Protocol.Webdav => "WebDAV",
        _ => p.ToString(),
    };

    /// <summary>TS REPORT_CSS：报告页内联样式（浅色底、打印友好；不引用任何外部资源）。</summary>
    public const string ReportCss =
        ":root{color-scheme:light}\n" +
        "body{margin:32px auto;max-width:1080px;padding:0 16px;font-family:\"Segoe UI\",\"Microsoft YaHei UI\",\"Microsoft YaHei\",system-ui,sans-serif;font-size:13px;line-height:1.6;color:#22303d;background:#f6f8fb;print-color-adjust:exact;-webkit-print-color-adjust:exact}\n" +
        "h1{margin:0 0 2px;font-size:22px}\n" +
        "h2{margin:26px 0 8px;font-size:16px;border-left:4px solid #3c65c3;padding-left:8px}\n" +
        ".meta{margin:2px 0;color:#5c6c7c}\n" +
        "table{width:100%;border-collapse:collapse;background:#fff;font-size:13px}\n" +
        "th,td{border:1px solid #c8d3df;padding:5px 8px;text-align:left;vertical-align:top}\n" +
        "th{background:#eef3fa;white-space:nowrap;font-weight:600}\n" +
        "td.num{text-align:right;white-space:nowrap}\n" +
        ".access-full{background:#e7f0ff}\n" +
        ".access-change{background:#f1ebfd}\n" +
        ".access-read{background:#f3f5f7}\n" +
        ".access-deny{background:#ffe6e4;color:#a4141a;font-weight:600}\n" +
        ".wide{background:#fff3cd;font-weight:700}\n" +
        ".warnbox{background:#fff8e6;border:1px solid #dfc26a;border-radius:6px;padding:10px 14px}\n" +
        ".okbox{background:#eef7ee;border:1px solid #9cc7a0;border-radius:6px;padding:10px 14px}\n" +
        "footer{margin-top:30px;border-top:1px solid #c8d3df;padding:12px 0 24px;font-size:12px}\n" +
        "@media print{body{margin:0;max-width:none;background:#fff;font-size:11px}h2{margin:14px 0 6px}tr{page-break-inside:avoid}}";
}
