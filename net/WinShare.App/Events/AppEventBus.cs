namespace WinShare.App.Events;

/// <summary>取消订阅句柄。</summary>
public interface IUnsubscribe { void Unsubscribe(); }

/// <summary>
/// 应用内事件总线（轻量 pub/sub，线程安全；对齐 Electron 版 main↔renderer IPC 事件面）。
/// 事件类型见 <see cref="AppEvents"/>。订阅者异常隔离：单个 handler 抛错不影响其他 handler。
/// </summary>
public interface IAppEventBus
{
    /// <summary>订阅某事件类型的全部发布；返回取消句柄。</summary>
    IUnsubscribe Subscribe<TEvent>(Action<TEvent> handler);

    /// <summary>同步发布（调用线程）。</summary>
    void Publish<TEvent>(TEvent evt);

    /// <summary>当前某事件类型的订阅数（测试/诊断用）。</summary>
    int Subscribers<TEvent>();
}

public sealed class AppEventBus : IAppEventBus
{
    readonly object _gate = new();
    readonly Dictionary<Type, List<Delegate>> _subs = new();

    public IUnsubscribe Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        List<Delegate> bucket;
        lock (_gate)
        {
            _subs.TryGetValue(typeof(TEvent), out bucket);
            bucket ??= new List<Delegate>();
            _subs[typeof(TEvent)] = bucket;
            bucket.Add(handler);
        }
        return new Handle(this, typeof(TEvent), handler);
    }

    public void Publish<TEvent>(TEvent evt)
    {
        List<Delegate> snapshot;
        lock (_gate)
        {
            if (!_subs.TryGetValue(typeof(TEvent), out var bucket) || bucket.Count == 0) return;
            snapshot = bucket.ToList();
        }
        foreach (var d in snapshot)
        {
            try
            {
                ((Action<TEvent>)d!)(evt);
            }
            catch
            {
                // 单 handler 异常隔离：不破坏其他订阅者
            }
        }
    }

    public int Subscribers<TEvent>()
    {
        lock (_gate)
        {
            return _subs.TryGetValue(typeof(TEvent), out var b) ? b.Count : 0;
        }
    }

    sealed class Handle(IAppEventBus bus, Type type, Delegate handler) : IUnsubscribe
    {
        bool _done;
        public void Unsubscribe()
        {
            if (_done) return;
            _done = true;
            lock (((AppEventBus)bus)._gate)
            {
                if (((AppEventBus)bus)._subs.TryGetValue(type, out var b))
                    b.Remove(handler);
            }
        }
    }
}
