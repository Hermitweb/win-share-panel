using WinShare.Core.Contracts;

namespace WinShare.App.Dashboard;

/// <summary>
/// 288 点环形缓冲（对齐 electron 采样器 5min × 24h 窗口语义）：
/// 写满后最旧点被覆盖，内存恒定；线程安全。作为 App 侧采样热路径，
/// 持久化 288 上限本身仍由 AppSettingsStore.PushHistory 兜住（与 TS stateStore 一致）。
/// </summary>
public sealed class HistoryRingBuffer
{
    public const int DefaultCapacity = 288;

    readonly HistoryPoint[] _buf;
    readonly object _gate = new();
    int _head;   // 下一个写入位置
    int _count;

    public HistoryRingBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buf = new HistoryPoint[capacity];
    }

    /// <summary>写入一个采样点（满容量时覆盖最旧点）。</summary>
    public void Push(HistoryPoint point)
    {
        lock (_gate)
        {
            _buf[_head] = point;
            _head = (_head + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }
    }

    /// <summary>按时间正序（旧→新）返回当前全部点的拷贝。</summary>
    public List<HistoryPoint> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<HistoryPoint>(_count);
            var start = _count < _buf.Length ? 0 : _head;
            for (var i = 0; i < _count; i++)
            {
                list.Add(_buf[(start + i) % _buf.Length]!);
            }
            return list;
        }
    }

    public int Count { get { lock (_gate) return _count; } }

    public int Capacity => _buf.Length;

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buf, 0, _buf.Length);
            _head = 0;
            _count = 0;
        }
    }
}
