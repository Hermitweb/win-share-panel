using System.Windows.Controls;

namespace WinShare.App.Navigation;

/// <summary>
/// 导航注册 API（T3b/c/d 扩展点）：
/// 页面 = 路由 + 标题（i18n key）+ 图标（段字符）+ UserControl 工厂。
/// 工厂返回 UserControl，注册时不实例化——页面懒创建，首访才建。
/// </summary>
public interface IPageFactory
{
    UserControl Create();
}

/// <summary>一个可注册页面。TitleKey 走 i18n 资源字典；Glyph 为侧栏图标（Unicode 段字符，非 WPF 图标包依赖）。</summary>
public sealed record AppPage
{
    public string route { get; init; } = "/";
    public string titleKey { get; init; } = "";
    public string glyph { get; init; } = "•";
    public required IPageFactory factory { get; init; }
}

/// <summary>
/// 页面注册表（对齐 src/components/Layout.tsx menus 数组 + React Router 页面映射）：
/// 默认 6 路由（/、/shares、/users、/sessions、/settings、/app-settings），
/// 本任务只填 仪表板，其余挂占位页（标注 TODO 给 T3b/c/d 替换）。
/// </summary>
public sealed class NavigationRegistry
{
    readonly Dictionary<string, AppPage> _pages = new();

    /// <summary>注册/替换某路由的页面（同路由二次注册=替换，T3b/c/d 接入即覆盖占位）。</summary>
    public void Register(AppPage page) => _pages[page.route] = page;

    public IReadOnlyCollection<AppPage> Pages => _pages.Values.OrderBy(p => p.route == "/" ? 0 : 1)
        .ThenBy(p => p.titleKey).ToList();

    /// <summary>路由 → 标题 i18n key（框架层找页面用）。</summary>
    public AppPage? Find(string route) => _pages.TryGetValue(route, out var p) ? p : null;

    /// <summary>路由是否已注册。</summary>
    public bool Has(string route) => _pages.ContainsKey(route);
}
