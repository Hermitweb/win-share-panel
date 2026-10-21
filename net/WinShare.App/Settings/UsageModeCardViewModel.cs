using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 使用模式与外观卡（新手/专家 + 主题）。
/// [Source: src/components/AppSettingsCards.tsx UsageModeCard L53-128]
/// 应用结果写入应用状态（重启保持）；主题切换额外触发热切换委托（视图层接 ThemeManager + ThemeChanged 事件）。
/// </summary>
public sealed class UsageModeCardViewModel : SettingsCardBase
{
    readonly AppSettingsStore _store;
    readonly Action<ThemeMode>? _applyTheme;
    readonly Action? _onThemeChanged;

    public UsageModeCardViewModel(
        AppSettingsStore store,
        Action<ThemeMode>? applyTheme = null,
        Action? onThemeChanged = null)
    {
        _store = store;
        _applyTheme = applyTheme;
        _onThemeChanged = onThemeChanged;
        var s = store.Load();
        AdvancedMode = s.advancedMode;
        Theme = s.theme;
        Raise();
    }

    public bool AdvancedMode { get; private set; }
    public ThemeMode Theme { get; private set; }

    /// <summary>应用新手/专家模式（patch advancedMode；成功文案与 TS 逐字对齐）。</summary>
    public Task<string?> ApplyModeAsync(bool advanced)
    {
        if (Busy) return Task.FromResult<string?>(null);
        Busy = true;
        var t = Task.Run(() =>
        {
            try
            {
                _store.SaveState(new AppStatePatch { advancedMode = advanced });
                AdvancedMode = advanced;
                Success = advanced ? "已切换到专家模式：高级参数已展开" : "已切换到新手模式：高级参数已收起";
                Raise();
                return null;
            }
            catch (Exception e)
            {
                Error = e.Message;
                return e.Message;
            }
            finally
            {
                Busy = false;
            }
        });
        return t;
    }

    /// <summary>应用主题（patch theme + 热切换委托；TS 成功文案「主题已更新」）。</summary>
    public Task<string?> ApplyThemeAsync(ThemeMode mode)
    {
        if (Busy) return Task.FromResult<string?>(null);
        Busy = true;
        var t = Task.Run(() =>
        {
            try
            {
                _store.SaveState(new AppStatePatch { theme = mode });
                Theme = mode;
                _applyTheme?.Invoke(mode);
                _onThemeChanged?.Invoke();
                Success = "主题已更新";
                Raise();
                return null;
            }
            catch (Exception e)
            {
                Error = e.Message;
                return e.Message;
            }
            finally
            {
                Busy = false;
            }
        });
        return t;
    }
}
