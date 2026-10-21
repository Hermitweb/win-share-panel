using System.Globalization;
using System.Windows;

namespace WinShare.App.I18n;

/// <summary>
/// 轻量 i18n（zh/en 资源字典热切换，对齐 antd 语言切换体验）：
/// 键 = 资源字典 x:Key；界面用 {DynamicResource Key} 引用，切语言 = 换字典。
/// 缺失键回退返回 key 本身（界面可见、不崩），失败面收敛在 Console/日志。
/// </summary>
public sealed class I18nManager
{
    readonly Application _app;

    public I18nManager(Application app)
    {
        _app = app;
    }

    public static readonly string[] SupportedCultures = { "zh", "en" };
    string _culture = ""; // 空=未初始化（首帧 App.Compose 调 SetCulture 装载字典）

    public string Culture => _culture;

    public event Action<string>? CultureChanged;

    public void SetCulture(string culture)
    {
        if (culture is not ("zh" or "en")) return;
        if (culture == _culture) return;
        _culture = culture;

        var dicts = _app.Resources.MergedDictionaries;
        for (var i = dicts.Count - 1; i >= 0; i--)
        {
            if (dicts[i].Source is { } s && s.OriginalString.Contains("Strings."))
            {
                dicts.RemoveAt(i);
            }
        }
        var uri = $"pack://application:,,,/I18n/Strings.{culture}.xaml";
        dicts.Add(new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });

        // 资源字典整组切换即完成界面语言切换（WPF DynamicResource 机制）；
        // 未设置框架级 Language 属性（.NET10 WPF 无 Application.Language 公开面，留 T3d 视需要补）

        CultureChanged?.Invoke(culture);
    }

    /// <summary>取键值（绑定外的场景；缺失回退 key）。</summary>
    public string T(string key)
    {
        object? v = Application.Current?.Resources[key];
        return v?.ToString() ?? key;
    }
}
