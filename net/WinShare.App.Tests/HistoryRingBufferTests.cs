using WinShare.App.Dashboard;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests;

public class HistoryRingBufferTests
{
    static HistoryPoint P(long ts, int s, int f) => new() { ts = ts, sessions = s, openFiles = f };

    [Fact]
    public void Push_UnderCapacity_KeepsAllInOrder()
    {
        var buf = new HistoryRingBuffer(288);
        for (long i = 1; i <= 10; i++) buf.Push(P(i, (int)i, (int)i * 2));

        var snap = buf.Snapshot();
        Assert.Equal(10, snap.Count);
        Assert.Equal(1, snap[0].ts);
        Assert.Equal(10, snap[9].ts);
        Assert.Equal(20, snap[9].openFiles);
    }

    [Fact]
    public void Push_OverCapacity_DropsOldestKeepsNewest()
    {
        var buf = new HistoryRingBuffer(5);
        for (long i = 1; i <= 8; i++) buf.Push(P(i, 0, 0));

        var snap = buf.Snapshot();
        Assert.Equal(5, snap.Count);
        Assert.Equal(4, snap[0].ts); // 1..3 被覆盖
        Assert.Equal(8, snap[4].ts);
        Assert.Equal(5, buf.Count);
    }

    [Fact]
    public void Snapshot_ReturnsDefensiveCopy()
    {
        var buf = new HistoryRingBuffer(10);
        buf.Push(P(1, 1, 1));
        var s1 = buf.Snapshot();
        s1.Add(P(99, 9, 9));
        Assert.Single(buf.Snapshot()); // 外部修改不影响缓冲
    }

    [Fact]
    public void Clear_EmptiesBuffer()
    {
        var buf = new HistoryRingBuffer(10);
        buf.Push(P(1, 1, 1));
        buf.Clear();
        Assert.Equal(0, buf.Count);
        Assert.Empty(buf.Snapshot());
    }

    [Fact]
    public void DefaultCapacity_Is288()
    {
        Assert.Equal(288, HistoryRingBuffer.DefaultCapacity);
        Assert.Equal(288, new HistoryRingBuffer().Capacity);
    }
}
