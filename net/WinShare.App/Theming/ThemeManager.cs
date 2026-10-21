using System.Windows;
using WinShare.App.State;
using WinShare.Core.Contracts;
using ThemeMode = WinShare.Core.Contracts.ThemeMode;

namespace WinShare.App.Theming;

/// <summary>
/// 主题管理（对齐 TS ThemeProvider 的 dual-track 语义：暗色必须整组切换，不能半套）：
/// 当前 WPF 单轨（资源字典整组换），主题持久化走 appstate.json 的 theme 键
/// （AppSettingsStore.SaveState patch { theme }，与 TS 同字段）。
/// </summary>
public sealed class ThemeManager
{
    readonly Application _app;
    AppSettingsStore? _store;

    public ThemeManager(Application app)
    {
        _app = app;
    }

    /// <summary>当前生效主题字典在 MergedDictionaries 里的标记资源。</summary>
    const string ThemeDictKey = "ThemeTokens";

    /// <summary>注入状态层（App 启动时调）；未注入时 Apply 不持久化。</summary>
    public void BindStore(AppSettingsStore store) => _store = store;

    /// <summary>应用主题（整组资源字典切换）；persist=true 写 appstate.json theme 键。</summary>
    public void Apply(ThemeMode mode, bool persist = true)
    {
        var uri = mode == ThemeMode.Dark
            ? "pack://application:,,,/Theming/ThemeTokens.Dark.xaml"
            : "pack://application:,,,/Theming/ThemeTokens.Light.xaml";

        var dicts = _app.Resources.MergedDictionaries;
        // 移除旧主题字典（按标记移除，避免堆叠）
        for (var i = dicts.Count - 1; i >= 0; i--)
        {
            if (dicts[i].Source is { } s && s.OriginalString.Contains("ThemeTokens"))
            {
                dicts.RemoveAt(i);
            }
        }
        var dict = new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) };
        dict[ThemeDictKey] = mode; // 标记，供后续识别
        dicts.Add(dict);

        if (persist && _store is not null)
        {
            _store.SaveState(new AppStatePatch { theme = mode });
        }
    }

    /// <summary>从 appstate 读主题（T3d 启动恢复用）。</summary>
    public ThemeMode Current() =>
        _store?.Load().theme ?? ThemeMode.Light;
}
