using WinShare.App.Dashboard;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests;

/// <summary>对齐 src/pages/Dashboard.tsx recentHistoryPoints / toTimeSeries 语义。</summary>
public class TrendSeriesTests
{
    static HistoryPoint P(long ts, int s, int f) => new() { ts = ts, sessions = s, openFiles = f };

    [Fact]
    public void RecentPoints_KeepsPointsInside24hWindow()
    {
        long now = 1_000_000_000_000;
        var points = new List<HistoryPoint>
        {
            P(now - TrendSeries.WindowMs - 1000, 1, 1), // 窗外（早于 24h）
            P(now - 3_600_000, 2, 2),                   // 1h 前，窗内
            P(now, 3, 3),                                // 当前，窗内
        };
        var recent = TrendSeries.RecentPoints(points, now);
        Assert.Equal(2, recent.Count);
        Assert.Equal(now - 3_600_000, recent[0].ts);
        Assert.Equal(now, recent[1].ts);
    }

    [Fact]
    public void RecentPoints_AllowsSmallClockSkewUpToOneSample()
    {
        long now = 1_000_000_000_000;
        var points = new List<HistoryPoint>
        {
            P(now + TrendSeries.SampleMs, 1, 1),        // 允许 +5min 时钟偏差
            P(now + TrendSeries.SampleMs + 1000, 2, 2), // 超出 → 剔除
        };
        var recent = TrendSeries.RecentPoints(points, now);
        Assert.Single(recent);
        Assert.Equal(now + TrendSeries.SampleMs, recent[0].ts);
    }

    [Fact]
    public void RecentPoints_SortsAndCapsAt288()
    {
        long now = 1_000_000_000_000;
        var points = new List<HistoryPoint>();
        for (long i = 300; i >= 1; i--)
        {
            points.Add(P(now - i * 1000, (int)i, 0)); // 乱序塞 300 个，全部在 24h 内
        }
        var recent = TrendSeries.RecentPoints(points, now);
        Assert.Equal(TrendSeries.MaxPoints, recent.Count);
        Assert.Equal(points.Max(p => p.ts), recent[^1].ts); // 正序
        for (int i = 1; i < recent.Count; i++)
        {
            Assert.True(recent[i - 1].ts <= recent[i].ts);
        }
    }

    [Fact]
    public void ToTimeSeries_InsertsNullBreakAtGapsLargerThanTwoSamples()
    {
        long baseTs = 1_000_000_000_000;
        // 两点间隔 15min > 2×5min（严格大于，对齐 TS p.ts - prev.ts > SAMPLE_MS * 2）→ 中间应插入 null 断点
        var points = new List<HistoryPoint>
        {
            P(baseTs, 1, 1),
            P(baseTs + 15 * 60_000, 5, 5),
        };
        var series = TrendSeries.SessionsSeries(points);
        Assert.Equal(3, series.Count);
        Assert.NotNull(series[0].Value);
        Assert.Null(series[1].Value); // 断点
        Assert.Equal(5, series[2].Value);
    }

    [Fact]
    public void ToTimeSeries_NoBreakWhenGapAtMostTwoSamples()
    {
        long baseTs = 1_000_000_000_000;
        var points = new List<HistoryPoint>
        {
            P(baseTs, 1, 1),
            P(baseTs + 2 * TrendSeries.SampleMs, 2, 2), // 恰好 2 周期，不插入
        };
        var series = TrendSeries.SessionsSeries(points);
        Assert.Equal(2, series.Count);
        Assert.All(series, s => Assert.NotNull(s.Value));
    }

    [Fact]
    public void OpenFilesSeries_PicksOpenFilesField()
    {
        var points = new List<HistoryPoint>
        {
            P(1, 10, 99),
            P(2, 20, 88),
        };
        var series = TrendSeries.OpenFilesSeries(points);
        Assert.Equal(99, series[0].Value);
        Assert.Equal(88, series[1].Value);
    }
}
