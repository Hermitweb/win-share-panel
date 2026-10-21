using System.Threading;
using WinShare.Core.Contracts;

namespace WinShare.App.Services;

/// <summary>
/// 24h 趋势采样器（对齐 electron/main.ts startHistorySampler）：
/// 后台 5min 定时采样；服务未起（serviceStatus != Running）或查询失败时静默跳过本轮，
/// 不写垃圾 0 点；启动后先采一次（首屏趋势不至于空白）。
/// 容量上限（288）由落地侧（AppSettingsStore.PushHistory / stateStore）负责。
/// </summary>
public sealed class TrendSampler : IDisposable
{
    /// <summary>采样源抽象（生产实现：SystemServiceTrendSource；测试注入假源）。</summary>
    public interface ITrendSampleSource
    {
        /// <summary>取一个采样点；返回 null 表示本轮跳过（服务停了/查询失败）。</summary>
        Task<HistoryPoint?> GetSampleAsync();
    }

    readonly ITrendSampleSource _source;
    readonly HistoryPointSink _sink;
    readonly Func<long> _nowMs;
    readonly TimeSpan _interval;
    System.Threading.Timer? _timer;
    bool _started;
    readonly object _gate = new();

    /// <param name="source">采样源（调 ISystemService 契约的实现）。</param>
    /// <param name="sink">落地点：环形缓冲 + 持久化（见 <see cref="HistoryPointSink"/>）。</param>
    public TrendSampler(ITrendSampleSource source, HistoryPointSink sink,
        TimeSpan? interval = null, Func<long>? nowMs = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _interval = interval ?? TimeSpan.FromMinutes(5);
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>启动：立即采一次（对齐 main.ts「启动后先采一次」），再进入 5min 周期。</summary>
    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _timer = new System.Threading.Timer(async _ =>
            {
                try { await SampleOnceAsync(); } catch { /* 静默：单轮失败不影响后续 */ }
            }, null, TimeSpan.FromSeconds(1), _interval);
        }
        await SampleOnceAsync();
    }

    /// <summary>
    /// 单轮采样（测试入口）：取点 → null 跳过不写；否则写环形缓冲 + 持久化并回报点。
    /// 返回写入的点（null=跳过）。
    /// </summary>
    public async Task<HistoryPoint?> SampleOnceAsync()
    {
        HistoryPoint? point;
        try
        {
            point = await _source.GetSampleAsync();
        }
        catch
        {
            return null; // 对齐 main.ts：采样失败跳过本轮
        }
        if (point is null) return null;

        _sink.Buffer.Push(point);
        _sink.Persist(point);
        _sink.Reported?.Invoke(point);
        return point;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _started = false;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>采样点落地组合：环形缓冲（内存）+ 持久化（appstate.json）+ 可选回报（事件总线）。</summary>
public sealed record HistoryPointSink
{
    public required Dashboard.HistoryRingBuffer Buffer { get; init; }
    public required Action<HistoryPoint> Persist { get; init; }
    public Action<HistoryPoint>? Reported { get; init; }
}

/// <summary>生产采样源：经 ISystemService 契约取仪表板统计，服务非 Running 或异常 → 返回 null 跳过。</summary>
public sealed class SystemServiceTrendSource : TrendSampler.ITrendSampleSource
{
    readonly ISystemService _svc;
    readonly Func<long> _nowMs;

    public SystemServiceTrendSource(ISystemService svc, Func<long>? nowMs = null)
    {
        _svc = svc;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public async Task<HistoryPoint?> GetSampleAsync()
    {
        DashboardStats s;
        try
        {
            s = await _svc.GetDashboardStatsAsync();
        }
        catch
        {
            return null; // 查询失败：跳过本轮，不写垃圾点
        }
        if (s.serviceStatus != ServiceState.Running) return null;
        return new HistoryPoint
        {
            ts = _nowMs(),
            sessions = s.activeSessions,
            openFiles = s.openFiles,
        };
    }
}
