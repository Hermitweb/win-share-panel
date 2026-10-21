using WinShare.App.Dashboard;
using WinShare.App.Services;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests;

/// <summary>
/// 采样器对齐 electron/main.ts startHistorySampler：
/// 服务未起/查询失败 → 跳过不写点；Running → 写 (ts, sessions, openFiles)；首屏即采一次。
/// </summary>
public class TrendSamplerTests
{
    sealed class FakeSource : TrendSampler.ITrendSampleSource
    {
        readonly HistoryPoint? _point;
        readonly Exception? _ex;
        int _calls;
        public int Calls => _calls;

        public FakeSource(HistoryPoint? point = null, Exception? ex = null)
        {
            _point = point;
            _ex = ex;
        }

        public Task<HistoryPoint?> GetSampleAsync()
        {
            Interlocked.Increment(ref _calls);
            if (_ex is not null) throw _ex;
            return Task.FromResult(_point);
        }
    }

    static HistoryPointSink MakeSink(out List<HistoryPoint> persisted)
    {
        var buffer = new HistoryRingBuffer(288);
        var list = new List<HistoryPoint>();
        persisted = list;
        return new HistoryPointSink
        {
            Buffer = buffer,
            Persist = p => { lock (list) list.Add(p); },
        };
    }

    [Fact]
    public async Task SampleOnce_ServiceStopped_ReturnsNullAndWritesNothing()
    {
        var sink = MakeSink(out var persisted);
        var sampler = new TrendSampler(new FakeSource(null), sink, nowMs: () => 123);

        var result = await sampler.SampleOnceAsync();

        Assert.Null(result);
        Assert.Empty(persisted);
        Assert.Equal(0, sink.Buffer.Count);
    }

    [Fact]
    public async Task SampleOnce_Running_WritesPointToBufferAndSink()
    {
        var sink = MakeSink(out var persisted);
        var point = new HistoryPoint { ts = 999, sessions = 4, openFiles = 9 };
        var sampler = new TrendSampler(new FakeSource(point), sink, nowMs: () => 123);

        var result = await sampler.SampleOnceAsync();

        Assert.NotNull(result);
        Assert.Equal(point, persisted.Single());
        Assert.Equal(1, sink.Buffer.Count);
        Assert.Equal(4, sink.Buffer.Snapshot()[0].sessions);
    }

    [Fact]
    public async Task SampleOnce_SourceThrows_SkipsRound()
    {
        var sink = MakeSink(out var persisted);
        var sampler = new TrendSampler(new FakeSource(null, new IOException("查询失败")), sink);

        var result = await sampler.SampleOnceAsync();

        Assert.Null(result);
        Assert.Empty(persisted); // 对齐 main.ts：采样失败静默跳过，不写垃圾点
    }

    [Fact]
    public async Task Start_SamplesOnceImmediately_ThenTimer()
    {
        var sink = MakeSink(out var persisted);
        var point = new HistoryPoint { ts = 1, sessions = 1, openFiles = 1 };
        var source = new FakeSource(point);
        var sampler = new TrendSampler(source, sink, interval: null, nowMs: () => 555);

        await sampler.StartAsync();

        Assert.Equal(1, source.Calls); // 启动即采一次（首屏不空白）
        Assert.Single(persisted);
        sampler.Stop();
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        var sink = MakeSink(out var persisted);
        var source = new FakeSource(new HistoryPoint { ts = 1, sessions = 1, openFiles = 1 });
        var sampler = new TrendSampler(source, sink, nowMs: () => 1);

        await sampler.StartAsync();
        await sampler.StartAsync();

        Assert.Equal(1, source.Calls); // 二次启动不重复采
    }

    [Fact]
    public void SystemServiceTrendSource_NonRunningService_ReturnsNull()
    {
        var svc = new FakeSystemService(ServiceState.Stopped);
        var source = new SystemServiceTrendSource(svc, nowMs: () => 42);
        var p = source.GetSampleAsync().GetAwaiter().GetResult();
        Assert.Null(p);
        Assert.Equal(1, svc.StatCalls);
    }

    [Fact]
    public void SystemServiceTrendSource_Running_ReturnsPoint()
    {
        var svc = new FakeSystemService(ServiceState.Running, activeSessions: 7, openFiles: 2);
        var source = new SystemServiceTrendSource(svc, nowMs: () => 42);
        var p = source.GetSampleAsync().GetAwaiter().GetResult();
        Assert.NotNull(p);
        Assert.Equal(42, p!.ts);
        Assert.Equal(7, p.sessions);
        Assert.Equal(2, p.openFiles);
    }

    [Fact]
    public void SystemServiceTrendSource_ServiceThrows_ReturnsNull()
    {
        var svc = new FakeSystemService(ServiceState.Running, ex: new Exception("powershell down"));
        var source = new SystemServiceTrendSource(svc, nowMs: () => 1);
        Assert.Null(source.GetSampleAsync().GetAwaiter().GetResult());
    }

    sealed class FakeSystemService : ISystemService
    {
        readonly ServiceState _state;
        readonly int _sessions, _files;
        readonly Exception? _ex;
        public int StatCalls;

        public FakeSystemService(ServiceState state, int activeSessions = 0, int openFiles = 0, Exception? ex = null)
        {
            _state = state;
            _sessions = activeSessions;
            _files = openFiles;
            _ex = ex;
        }

        public Task<DashboardStats> GetDashboardStatsAsync()
        {
            Interlocked.Increment(ref StatCalls);
            if (_ex is not null) throw _ex;
            return Task.FromResult(new DashboardStats
            {
                shareCount = 0,
                activeSessions = _sessions,
                openFiles = _files,
                serviceStatus = _state,
                topShares = Array.Empty<TopShare>(),
                byProtocol = new Dictionary<Protocol, ProtocolStats>(),
            });
        }

        // 以下契约面未用，给最小实现
        public Task<UserInfo> GetCurrentUserAsync() => throw new NotImplementedException();
        public Task<bool> IsAdminAsync() => throw new NotImplementedException();
        public Task RelaunchAsAdminAsync() => throw new NotImplementedException();
        public Task<string> GetAuditLogAsync() => throw new NotImplementedException();
        public Task<HealthCheckResult> HealthCheckAsync() => throw new NotImplementedException();
        public Task<OsInfo> GetOsInfoAsync(OsInfoOptions? opts = null) => throw new NotImplementedException();
        public AppState LoadState() => throw new NotImplementedException();
        public AppState SaveState(AppStatePatch patch) => throw new NotImplementedException();
        public JournalEntry AddJournal(JournalEntryDraft entry) => throw new NotImplementedException();
        public void RemoveJournal(string id) { }
        public void PushHistory(HistoryPoint point) { }
    }
}
