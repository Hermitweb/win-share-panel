using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinShare.App.Settings;

/// <summary>
/// 设置卡片 VM 公共基座（纯逻辑，不依赖 WPF 控件；xUnit 直测）。
/// 统一三态结果面：Busy（操作中）/Success（最近成功文案）/Error（最近失败文案），
/// 视图层据此渲染 antd message 等价提示（对齐 electron 版「成功/失败必须可见」语义）。
/// </summary>
public abstract class SettingsCardBase : INotifyPropertyChanged
{
    bool _busy;
    string? _success;
    string? _error;

    public bool Busy
    {
        get => _busy;
        protected set { _busy = value; Raise(); }
    }

    /// <summary>最近一次成功文案（null=暂无）；设值即清 Error。</summary>
    public string? Success
    {
        get => _success;
        protected set { _success = value; if (value is not null) _error = null; Raise(); }
    }

    /// <summary>最近一次失败文案（null=暂无）；设值即清 Success。</summary>
    public string? Error
    {
        get => _error;
        protected set { _error = value; if (value is not null) _success = null; Raise(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
