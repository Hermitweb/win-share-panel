using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 窗口行为卡（v2.2.0 安装与运行配置）：任务栏图标显示 + 关闭按钮行为。
/// 持久化到 appstate（showInTaskbar/closeToTray）；变更经 onApplied 回调立即生效
/// （AppSettingsPageView 接线 AppShell.ApplyWindowBehavior → MainWindow 跟随）。
/// </summary>
public sealed class WindowBehaviorCardViewModel : SettingsCardBase
{
    readonly AppSettingsStore _store;
    readonly Action<bool, bool>? _onApplied;

    public WindowBehaviorCardViewModel(AppSettingsStore store, Action<bool, bool>? onApplied = null)
    {
        _store = store;
        _onApplied = onApplied;
        var s = store.Load();
        ShowInTaskbar = s.showInTaskbar;
        CloseToTray = s.closeToTray;
        Raise();
    }

    public bool ShowInTaskbar { get; private set; }
    public bool CloseToTray { get; private set; }

    public Task<string?> SetShowTaskbarAsync(bool value) => Apply(new AppStatePatch { showInTaskbar = value }, value, CloseToTray, null);
    public Task<string?> SetCloseToTrayAsync(bool value) => Apply(new AppStatePatch { closeToTray = value }, ShowInTaskbar, value, null);

    Task<string?> Apply(AppStatePatch patch, bool show, bool close, string? successText)
    {
        if (Busy) return Task.FromResult<string?>(null);
        Busy = true;
        Raise();
        return Task.Run(() =>
        {
            try
            {
                _store.SaveState(patch);
                ShowInTaskbar = show;
                CloseToTray = close;
                // v2.2.0 第二轮评审：不再写常驻状态文案（此前「任务栏图标将保持显示」
                // 以绿色常驻在卡内，与设置项语义重复且误导）——变更即时生效 + 勾选态即反馈
                if (successText is not null) Success = successText;
                Raise();
                _onApplied?.Invoke(show, close);
                return null;
            }
            catch (Exception e)
            {
                Error = e.Message;
                Raise();
                return e.Message;
            }
            finally
            {
                Busy = false;
            }
        });
    }
}
