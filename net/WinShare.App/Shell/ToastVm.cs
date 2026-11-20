using System;
using System.Collections.Generic;
using System.Threading;

namespace WinShare.App.Shell;

/// <summary>Toast 级别（对齐设置卡 MessageStrip 的三态：成功/警告/错误）。</summary>
public enum ToastLevel { Success, Warn, Danger }

/// <summary>单条 Toast（Id 稳定供关闭/超时定位；At 供超时收敛计算）。</summary>
public sealed record ToastItem(int Id, ToastLevel Level, string Text, DateTimeOffset At);

/// <summary>
/// Toast 纯逻辑（无 UI/Dispatcher 依赖，表驱动单测）：
/// - 堆叠上限 MaxVisible，超出顶掉最旧（错误已落应用日志，不因堆积挤丢反馈通道）
/// - 超时收敛 PruneExpired(now)：Success 4s / Warn 6s / Danger 永驻（需手动关闭）
/// - Push/Dismiss 可从后台线程调用（UI 侧自行调度重建）
/// </summary>
public sealed class ToastVm
{
    public const int MaxVisible = 3;
    public static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan WarnTtl = TimeSpan.FromSeconds(6);

    /// <summary>级别 → 自动消失时长（Danger 永驻 = Timeout.InfiniteTimeSpan，需手动关）。</summary>
    public static TimeSpan DurationFor(ToastLevel level) => level switch
    {
        ToastLevel.Success => SuccessTtl,
        ToastLevel.Warn => WarnTtl,
        _ => Timeout.InfiniteTimeSpan,
    };

    readonly object _gate = new();
    readonly List<ToastItem> _items = new();
    int _seq;

    /// <summary>当前可见条目（最旧在前）。</summary>
    public IReadOnlyList<ToastItem> Items
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    /// <summary>内容变化（新增/顶掉/超时/手动关闭）时触发；可能来自后台线程，UI 侧自行调度。</summary>
    public event Action? Changed;

    /// <summary>入列一条；超上限时顶掉最旧。返回入列条目。</summary>
    public ToastItem Push(ToastLevel level, string text)
    {
        var item = new ToastItem(Interlocked.Increment(ref _seq), level, text, DateTimeOffset.Now);
        lock (_gate)
        {
            _items.Add(item);
            while (_items.Count > MaxVisible) _items.RemoveAt(0);
        }
        Changed?.Invoke();
        return item;
    }

    /// <summary>手动关闭指定条目；返回是否存在。</summary>
    public bool Dismiss(int id)
    {
        bool hit;
        lock (_gate) hit = _items.RemoveAll(i => i.Id == id) > 0;
        if (hit) Changed?.Invoke();
        return hit;
    }

    /// <summary>超时收敛（宿主定时器驱动；danger 永驻）。返回是否有条目被清走。</summary>
    public bool PruneExpired(DateTimeOffset now)
    {
        bool hit;
        lock (_gate)
        {
            int before = _items.Count;
            _items.RemoveAll(i => i.Level switch
            {
                ToastLevel.Success => now - i.At >= SuccessTtl,
                ToastLevel.Warn => now - i.At >= WarnTtl,
                _ => false,
            });
            hit = _items.Count != before;
        }
        if (hit) Changed?.Invoke();
        return hit;
    }
}
