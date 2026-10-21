using System.Text;
using WinShare.Core.Contracts;

namespace WinShare.App.Users;

/// <summary>矩阵单元格访问级别（对齐 PermissionMatrix.tsx <c>Access = 'Full'|'Change'|'Read'|'Deny'|'-'</c>；None = '-'）。</summary>
public enum MatrixAccess { None, Full, Change, Read, Deny }

/// <summary>矩阵账号（对齐 TS accounts = 用户 + 组，{ name, type: 'User'|'Group' }）。</summary>
public sealed record MatrixAccount(string name, string type);

/// <summary>
/// 权限矩阵纯函数（逐行对齐 src/components/PermissionMatrix.tsx；不依赖 WPF/DOM）：
/// PickAccess（Deny 优先安全语义）/ IsWideAccount / EscapeHtml / CSV/JSON/HTML 报告构建。
/// </summary>
public static class PermissionMatrix
{
    /// <summary>访问级别 → 中文标签（对齐 ACCESS_LABEL）。</summary>
    public static string AccessLabel(MatrixAccess a) => a switch
    {
        MatrixAccess.Full => "完全",
        MatrixAccess.Change => "更改",
        MatrixAccess.Read => "只读",
        MatrixAccess.Deny => "拒绝",
        _ => "-",
    };

    /// <summary>
    /// 同一账号多条权限条目取最严格：Deny > Full > Change > Read
    /// [Source: src/components/PermissionMatrix.tsx pickAccess（PRIORITY: Deny:0, Full:1, Change:2, Read:3）]
    /// </summary>
    public static MatrixAccess PickAccess(IEnumerable<SharePermission> perms)
    {
        var list = perms is null ? new List<SharePermission>() : perms.ToList();
        if (list.Count == 0) return MatrixAccess.None;
        int priority(MatrixAccess a) => a switch
        {
            MatrixAccess.Deny => 0,
            MatrixAccess.Full => 1,
            MatrixAccess.Change => 2,
            MatrixAccess.Read => 3,
            _ => 4,
        };
        return list
            .Select(p => p.deny || p.access == ShareAccess.NoAccess
                ? MatrixAccess.Deny
                : MapShareAccess(p.access))
            .OrderBy(a => priority(a))
            .First();

        static MatrixAccess MapShareAccess(ShareAccess a) => a switch
        {
            ShareAccess.Full => MatrixAccess.Full,
            ShareAccess.Change => MatrixAccess.Change,
            ShareAccess.Read => MatrixAccess.Read,
            _ => MatrixAccess.Deny,
        };
    }

    /// <summary>
    /// 宽权限账号判定（Everyone/Authenticated Users/Guests；Windows 带机器/域前缀取末段）
    /// [Source: src/components/PermissionMatrix.tsx isWideAccount（WIDE_ACCOUNT_NAMES）]
    /// </summary>
    static readonly string[] WideAccountNames = { "everyone", "authenticated users", "guests" };

    public static bool IsWideAccount(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string bare = name!.Contains('\\') ? name[(name.LastIndexOf('\\') + 1)..] : name;
        return WideAccountNames.Contains(bare.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// HTML 实体转义：& 必须最先替换，否则后续实体被二次转义
    /// [Source: src/components/PermissionMatrix.tsx escapeHtml]
    /// </summary>
    public static string EscapeHtml(object? value)
    {
        var s = value?.ToString() ?? "";
        return s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    static string Cell(object c) => "\"" + c.ToString()!.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// 矩阵 CSV（BOM + 双引号转义 + 逗号列；行=共享，首列共享名，其余列=账号，值=中文标签）
    /// [Source: src/components/PermissionMatrix.tsx exportCsv]
    /// </summary>
    public static string BuildMatrixCsv(IReadOnlyList<Share> shares,
        IReadOnlyList<MatrixAccount> accounts,
        IReadOnlyDictionary<string, Dictionary<string, MatrixAccess>> matrix)
    {
        var header = new List<string> { "共享" };
        header.AddRange(accounts.Select(a => a.name));
        var rows = new List<List<string>> { header };
        foreach (var s in shares)
        {
            var m = matrix.TryGetValue(s.name, out var row) ? row : new Dictionary<string, MatrixAccess>();
            var line = new List<string> { s.name };
            foreach (var a in accounts)
                line.Add(AccessLabel(m.TryGetValue(a.name, out var v) ? v : MatrixAccess.None));
            rows.Add(line);
        }
        var csv = string.Join("\n", rows.Select(r => string.Join(",", r.Select(Cell))));
        return "\uFEFF" + csv;
    }

    /// <summary>
    /// 矩阵 JSON（exportedAt ISO + shares[].permissions[]；access 用 Access 英文字面量，'-' 表示无条目）
    /// [Source: src/components/PermissionMatrix.tsx exportJson]
    /// </summary>
    public static string BuildMatrixJson(IReadOnlyList<Share> shares,
        IReadOnlyList<MatrixAccount> accounts,
        IReadOnlyDictionary<string, Dictionary<string, MatrixAccess>> matrix,
        DateTimeOffset exportedAt)
    {
        var sb = new StringBuilder();
        sb.Append("{\n"); // T3d 最小修复（原字符字面量含 2 字符）
        sb.Append($"  \"exportedAt\": \"{exportedAt.UtcDateTime:O}\",\n");
        sb.Append("  \"shares\": [\n");
        for (int i = 0; i < shares.Count; i++)
        {
            var s = shares[i];
            var m = matrix.TryGetValue(s.name, out var row) ? row : new Dictionary<string, MatrixAccess>();
            sb.Append("    {");
            sb.Append($"\"name\": {Json(s.name)}, \"path\": {Json(s.path)},");
            sb.Append("\n      \"permissions\": [");
            for (int j = 0; j < accounts.Count; j++)
            {
                var a = accounts[j];
                var v = m.TryGetValue(a.name, out var cell) ? cell : MatrixAccess.None;
                sb.Append($"\n        {{\"account\": {Json(a.name)}, \"accountType\": {Json(a.type)}, \"access\": {Json(AccessLiteral(v))}}},");
                if (j < accounts.Count - 1) sb.Append('\n');
            }
            sb.Append("\n      ]\n    }");
            sb.Append(i < shares.Count - 1 ? "," : "");
            sb.Append('\n');
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();

        static string Json(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static string AccessLiteral(MatrixAccess a) => a switch
        {
            MatrixAccess.Full => "Full",
            MatrixAccess.Change => "Change",
            MatrixAccess.Read => "Read",
            MatrixAccess.Deny => "Deny",
            _ => "-",
        };
    }

    /// <summary>协议 → 中文标签（对齐 PROTOCOL_LABEL）。</summary>
    static string ProtocolLabel(Protocol p) => p switch
    {
        Protocol.Smb => "SMB",
        Protocol.Nfs => "NFS",
        Protocol.Ftp => "FTP",
        Protocol.Webdav => "WebDAV",
        _ => p.ToString(),
    };

    /// <summary>
    /// 自包含 HTML 审计报告（内联样式、无外链、无脚本；一切系统字符串经 EscapeHtml）
    /// [Source: src/components/PermissionMatrix.tsx buildHtmlReport]
    /// </summary>
    public static string BuildHtmlReport(IReadOnlyList<Share> shares,
        IReadOnlyList<MatrixAccount> accounts,
        IReadOnlyDictionary<string, Dictionary<string, MatrixAccess>> matrix,
        DateTimeOffset generatedAt)
    {
        const string reportCss =
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
            "footer{margin-top:30px;border-top:1px solid #c8d3df;padding:12px 0 24px;color:#5c6c7c;font-size:12px}\n" +
            "@media print{body{margin:0;max-width:none;background:#fff;font-size:11px}h2{margin:14px 0 6px}tr{page-break-inside:avoid}}";

        MatrixAccess AccessOf(string shareName, string account) =>
            matrix.TryGetValue(shareName, out var row) && row.TryGetValue(account, out var v) ? v : MatrixAccess.None;

        // 最高权限展示序（对齐 TS RANK: Full, Change, Read, Deny —— 非安全序，仅展示口径）
        var rank = new[] { MatrixAccess.Full, MatrixAccess.Change, MatrixAccess.Read, MatrixAccess.Deny };

        var shareRows = shares.Select(s =>
        {
            var granted = accounts.Select(a => AccessOf(s.name, a.name))
                .Where(v => v != MatrixAccess.None).ToList();
            var highest = rank.FirstOrDefault(k => granted.Contains(k));
            return (Share: s, Granted: granted, Highest: highest, HasHighest: granted.Count > 0);
        }).ToList();

        var totalEntries = shareRows.Sum(r => r.Granted.Count);

        var wideFindings = accounts.Where(a => IsWideAccount(a.name))
            .Select(a =>
            {
                var rows = shareRows.Where(r => AccessOf(r.Share.name, a.name) != MatrixAccess.None).ToList();
                return (Account: a, Rows: rows);
            })
            .Where(f => f.Rows.Count > 0).ToList();

        var sb = new StringBuilder();
        string accessClass(MatrixAccess v) => v switch
        {
            MatrixAccess.Full => "access-full",
            MatrixAccess.Change => "access-change",
            MatrixAccess.Read => "access-read",
            MatrixAccess.Deny => "access-deny",
            _ => "access-none",
        };

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine("<title>谁能访问什么 · 共享权限审计报告</title>");
        sb.AppendLine($"<style>{reportCss}</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // header
        sb.AppendLine("<header>");
        sb.AppendLine("<h1>谁能访问什么 · 共享权限审计报告</h1>");
        sb.AppendLine($"<p class=\"meta\">生成时间：{EscapeHtml(generatedAt.LocalDateTime.ToString())}（本地） · {EscapeHtml(generatedAt.UtcDateTime.ToString("O"))}（ISO）</p>");
        sb.AppendLine($"<p class=\"meta\">概览：共享 {shares.Count} 个 · 账号 {accounts.Count} 个 · 共享权限条目 {totalEntries} 条</p>");
        sb.AppendLine("</header>");

        // 宽权限账号提示
        sb.AppendLine("<h2>宽权限账号提示</h2>");
        if (wideFindings.Count > 0)
        {
            sb.AppendLine("<div class=\"warnbox\">");
            sb.AppendLine("<ul>" + string.Join("", wideFindings.Select(f =>
                $"<li class=\"wide\">{EscapeHtml(f.Account.name)}（{EscapeHtml(f.Account.type == "Group" ? "组" : "用户")}）：" +
                string.Join("、", f.Rows.Select(r => EscapeHtml(r.Share.name))) + "</li>")) + "</ul>");
            sb.AppendLine("<p>风险说明：Everyone / Authenticated Users 等宽权限账号分别代表“所有访客（含匿名）”与“所有已登录用户”，一旦被授予「完全 / 更改」，任意满足条件的网络用户都可写入甚至删除共享数据，请重点复核。</p>");
            sb.AppendLine("</div>");
        }
        else
        {
            sb.AppendLine("<div class=\"okbox\">");
            sb.AppendLine("<p>未发现 Everyone / Authenticated Users / Guests 等宽权限账号获得共享授权。风险说明：此类宽权限账号若被授予「完全 / 更改」，任意网络用户都可能读写共享数据，请避免。</p>");
            sb.AppendLine("</div>");
        }

        // 共享概览
        sb.AppendLine("<h2>共享概览</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<thead><tr><th>名称</th><th>路径</th><th>协议</th><th>授权数</th><th>最高权限</th></tr></thead>");
        sb.Append("<tbody>");
        foreach (var r in shareRows)
        {
            var cls = r.HasHighest ? accessClass(r.Highest) : "";
            sb.AppendLine($"<tr><td>{EscapeHtml(r.Share.name)}</td><td>{EscapeHtml(r.Share.path)}</td>" +
                $"<td>{EscapeHtml(ProtocolLabel(r.Share.protocol))}</td>" +
                $"<td class=\"num\">{r.Granted.Count}</td>" +
                $"<td class=\"{cls}\">{(r.HasHighest ? AccessLabel(r.Highest) : "无授权")}</td></tr>");
        }
        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");

        // 账号 × 共享明细
        sb.AppendLine("<h2>账号 × 共享权限明细</h2>");
        sb.Append("<table>\n<thead><tr><th>账号 / 共享</th>");
        foreach (var a in accounts)
        {
            var wide = IsWideAccount(a.name);
            sb.Append($"<th{(wide ? " class=\"wide\"" : "")}>{EscapeHtml(a.name)}{(wide ? "（宽权限）" : "")}</th>");
        }
        sb.AppendLine("</tr></thead>");
        sb.Append("<tbody>");
        foreach (var s in shares)
        {
            sb.Append($"<tr><td>{EscapeHtml(s.name)}</td>");
            foreach (var a in accounts)
            {
                var v = AccessOf(s.name, a.name);
                var wideCell = IsWideAccount(a.name) && v != MatrixAccess.None ? " wide" : "";
                sb.Append($"<td class=\"{accessClass(v)}{wideCell}\">{AccessLabel(v)}</td>");
            }
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("<p class=\"meta\">图例：完全 = 读取/写入/删除/改权限；更改 = 读取/写入；只读 = 仅读取；拒绝 = 显式拒绝；- = 无共享权限条目。</p>");

        // footer
        sb.AppendLine("<footer>");
        sb.AppendLine("<p><strong>边界说明：</strong>本报告仅共享权限（Share）层面统计，未合并计算 NTFS 有效权限，不代表任何账号最终实际可访问的范围；如需有效权限请结合 NTFS ACL 复核。</p>");
        sb.AppendLine("<p>由 WinShare Panel 导出 · 自包含单页 HTML（内联样式，无脚本、无外部资源），可直接打印存档。</p>");
        sb.AppendLine("</footer>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>
    /// 并发上限拉取（对齐 TS mapPool：limit 个 worker、共享游标、shouldCancel 逐任务检查；
    /// 取消后停止派发，返回空表——调用方检查取消位后丢弃结果，TS 同款语义）
    /// [Source: src/components/PermissionMatrix.tsx mapPool]
    /// </summary>
    public static async Task<List<TOut>> MapPool<TIn, TOut>(IReadOnlyList<TIn> items, int limit,
        Func<TIn, Task<TOut>> fn, Func<bool> shouldCancel)
    {
        if (items.Count == 0) return new List<TOut>();
        var results = new TOut[items.Count];
        var cursor = 0;
        var gate = new object();
        int next()
        {
            lock (gate)
            {
                if (cursor >= items.Count) return -1;
                return cursor++;
            }
        }
        var workers = Enumerable.Range(0, Math.Min(limit, items.Count)).Select(async _ =>
        {
            while (true)
            {
                if (shouldCancel()) return;
                var i = next();
                if (i < 0) return;
                try
                {
                    results[i] = await fn(items[i]);
                }
                catch
                {
                    if (shouldCancel()) return;
                    throw;
                }
            }
        }).ToList();
        await Task.WhenAll(workers);
        if (shouldCancel()) return new List<TOut>(); // 取消 → 调用方丢弃（对齐 TS dead/cancelRef 分支）
        return results.ToList();
    }
}
