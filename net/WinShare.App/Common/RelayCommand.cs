using System.Windows.Input;

namespace WinShare.App.Common;

/// <summary>
/// 可复命命令（纯 .NET，不依赖 WPF 控件类型；界面侧直接绑定）。
/// 原先 Sessions/Users/Dashboard/Shares 四模块各持一份同构副本，现统一收口到 Common。
/// </summary>
public sealed class RelayCommand : ICommand
{
    readonly Action _sync;
    readonly Func<Task>? _async;

    public RelayCommand(Action action)
    {
        _sync = action ?? throw new ArgumentNullException(nameof(action));
    }

    public RelayCommand(Func<Task> action)
    {
        _async = action ?? throw new ArgumentNullException(nameof(action));
        _sync = () => { };
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        // 不能在 UI 线程同步阻塞异步（GetResult）：_async 的延续默认回到 UI 线程 → 死锁。
        // 改为 fire-and-forget；异步错误由各 VM 的状态语义回报（对齐 Shares 模块写法）。
        if (_async is not null) _ = _async();
        else _sync();
    }

    public async Task ExecuteAsync()
    {
        if (_async is not null) await _async();
        else _sync();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 参数化命令（强类型参数；DataGrid 行按钮绑定，Parameter=行 VM 或协议枚举）。
/// CanExecute 恒真——能力门控（CanToggle/SupportsPermissions）由行属性驱动视图显隐 +
/// 命令处理器内再查一次（双重保险，对齐 TS 能力位语义）。
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    readonly Action<T>? _sync;
    readonly Func<T, Task>? _async;

    public RelayCommand(Action<T> action)
    {
        _sync = action ?? throw new ArgumentNullException(nameof(action));
    }

    public RelayCommand(Func<T, Task> action)
    {
        _async = action ?? throw new ArgumentNullException(nameof(action));
    }

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        if (parameter is not T t) return;
        if (_async is not null)
        {
            Task result;
            try
            {
                result = _async(t);
            }
            catch
            {
                // 同步段异常（VM 状态校验类）：界面文案语义由各命令自管
                return;
            }
            return; // fire-and-forget；异步错误经 VM StatusMsg 语义回报
        }
        _sync!(t);
    }
}
