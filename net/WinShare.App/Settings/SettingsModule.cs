using WinShare.App.Navigation;
using WinShare.App.Views.Settings;

namespace WinShare.App.Settings;

/// <summary>
/// 设置模块注册入口（T3d 交付面）。组合根在 App.RegisterPages() 之后调用一次
/// （或调用处替换两条 PlaceholderPage 注册）：
/// <code>
/// WinShare.App.Settings.SettingsModule.Register(reg);
/// </code>
/// 同路由二次注册即替换占位页（NavigationRegistry.Register 语义，见 DOCS.md「导航注册 API」）。
/// titleKey/glyph 与 App.xaml.cs 现有占位注册逐字一致，侧栏外观不变。
/// </summary>
public static class SettingsModule
{
    public const string SettingsRoute = "/settings";
    public const string AppSettingsRoute = "/app-settings";

    /// <summary>把 /settings（服务器配置）与 /app-settings（应用设置）两条路由替换为真实页面。</summary>
    public static void Register(NavigationRegistry reg)
    {
        reg.Register(new AppPage
        {
            route = SettingsRoute,
            titleKey = "Nav.Settings",
            glyph = "\uE713",
            factory = new PageFactory(() => new SettingsPageView()),
        });
        reg.Register(new AppPage
        {
            route = AppSettingsRoute,
            titleKey = "Nav.AppSettings",
            glyph = "\uE115",
            factory = new PageFactory(() => new AppSettingsPageView()),
        });
    }
}
