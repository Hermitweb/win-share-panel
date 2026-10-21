using WinShare.Core.Contracts;

namespace WinShare.App.Dashboard;

/// <summary>
/// 仪表板 CSV 导出（逐字段对齐 src/pages/Dashboard.tsx exportCsv）：
/// 列=两列（指标/值）；数据行顺序与 TS 完全一致；
/// 所有单元格双引号包裹、内部 " 转义为 ""、行间 \n、文件头带 BOM（\uFEFF）。
/// </summary>
public static class CsvExporter
{
    /// <summary>生成带 BOM 的 CSV 文本（文件级；调用方直接落盘）。</summary>
    public static string BuildCsv(DashboardStats stats)
    {
        var rows = BuildRows(stats);
        var csv = string.Join('\n', rows.Select(r => string.Join(',', r.Select(Cell))));
        return "\uFEFF" + csv;
    }

    /// <summary>纯行数据（不带 BOM/引号；供测试与复用）。</summary>
    public static List<string[]> BuildRows(DashboardStats stats)
    {
        var rows = new List<string[]>
        {
            new[] { "指标", "值" },
            new[] { "共享总数", stats.shareCount.ToString() },
            new[] { "活跃会话", stats.activeSessions.ToString() },
            new[] { "打开文件", stats.openFiles.ToString() },
            new[] { "服务状态", stats.serviceStatus.ToString() },
        };

        foreach (var p in ProtocolPalette.Order)
        {
            var info = stats.byProtocol.TryGetValue(p, out var v) ? v : default!;
            rows.Add(new[]
            {
                $"协议:{ProtocolPalette.Label(p)}",
                $"共享 {(info?.shares ?? 0)} / 会话 {(info?.sessions ?? 0)}",
            });
        }

        foreach (var s in stats.topShares ?? new List<TopShare>())
        {
            rows.Add(new[]
            {
                $"热门:{ProtocolPalette.Label(s.protocol)}:{s.name}",
                s.connections.ToString(),
            });
        }

        return rows;
    }

    /// <summary>TS 逐格转义：String(c) 后 " → ""，整格加引号。</summary>
    static string Cell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
