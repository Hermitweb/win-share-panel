using WinShare.App.Navigation;
using WinShare.App.Views;
using WinShare.App.Views.Shares;
using WinShare.Core.Contracts;

namespace WinShare.App.Shares;

/// <summary>
/// 共享管理模块装配（T3b 交付入口；任务书原签名 Register(INavigationRegistry) ——
/// 仓库实际提供的是具体类 NavigationRegistry（T3a DOCS.md 占位替换 API），
/// 为避免改动共享文件 / 与并行任务 T3c/T3d 命名冲突，此处以具体类为入参。
/// 调度者接线：在 App.RegisterPages() 中调用
///   WinShare.App.Shares.SharesModule.Register(reg);
/// 即可把 /shares 路由从 PlaceholderPage 换成 SharesPageView（同路由二次注册=替换）。
/// </summary>
public static class SharesModule
{
    public static void Register(NavigationRegistry reg)
    {
        reg.Register(new AppPage
        {
            route = "/shares",
            titleKey = "Nav.Share",
            glyph = "\uE8B7",
            factory = new PageFactory(() => new SharesPageView()),
        });
    }
}

/// <summary>
/// 契约服务槽位（组合根扩展点，同 AppServices.SystemService 模式）：
/// Core 服务实现（T-core）落地后在 App.OnStartup 装配处设置，
/// SharesPageView 构造 ViewModel 时读取——未设置时全链路走占位降级路径。
/// </summary>
public static class SharesServices
{
    public static IShareService? Share { get; set; }
    public static IPresetService? Preset { get; set; }
    public static IUserService? User { get; set; }
    public static IDiskService? Disk { get; set; }
    public static IProtocolService? Protocol { get; set; }
}
