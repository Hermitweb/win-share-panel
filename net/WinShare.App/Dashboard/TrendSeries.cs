using WinShare.Core.Contracts;

namespace WinShare.App.Dashboard;

/// <summary>
/// 24h 趋势序列纯算法（逐行对齐 src/pages/Dashboard.tsx 的导出函数）：
/// - RecentPoints = recentHistoryPoints：24h 窗口过滤（容忍 +5min 时钟偏差）+ 排序 + 288 点上限
/// - ToTimeSeries = toTimeSeries：相邻采样间隔 > 2×采样周期 时插入 null 断点（停机缺口留白而非斜线假连接）
/// 数据源语义：主进程 5min × 288 点采样，服务停止时跳过不写点（见 TrendSampler）。
/// </summary>
public static class TrendSeries
{
    public const long SampleMs = 5 * 60 * 1000;
    public const long WindowMs = 24 * 60 * 60 * 1000;
    public const int MaxPoints = 288;

    /// <summary>[ts, value] 时间序列点；value=null 表示断点（两侧不连线）。</summary>
    public readonly record struct TrendValue(long Ts, long? Value);

    /// <summary>取最近 24h 的采样点：窗口过滤 + 排序 + 288 点上限。</summary>
    public static List<HistoryPoint> RecentPoints(IReadOnlyList<HistoryPoint>? points, long now)
    {
        return (points ?? new List<HistoryPoint>())
            .Where(p => p is not null
                       && double.IsFinite(p.ts)
                       && p.ts > now - WindowMs
                       && p.ts <= now + SampleMs)
            .OrderBy(p => p.ts)
            .TakeLast(MaxPoints)
            .ToList();
    }

    /// <summary>采样点 → 时间序列；缺口中心放一个 null 点，视觉上整段留白。</summary>
    public static List<TrendValue> ToTimeSeries(IReadOnlyList<HistoryPoint> points, Func<HistoryPoint, long> pick)
    {
        var outList = new List<TrendValue>();
        for (var i = 0; i < points.Count; i++)
        {
            if (i > 0 && points[i].ts - points[i - 1].ts > SampleMs * 2)
            {
                outList.Add(new TrendValue((long)Math.Round((points[i - 1].ts + points[i].ts) / 2.0), null));
            }
            outList.Add(new TrendValue(points[i].ts, pick(points[i])));
        }
        return outList;
    }

    public static List<TrendValue> SessionsSeries(IReadOnlyList<HistoryPoint> points)
        => ToTimeSeries(points, p => p.sessions);

    public static List<TrendValue> OpenFilesSeries(IReadOnlyList<HistoryPoint> points)
        => ToTimeSeries(points, p => p.openFiles);
}
