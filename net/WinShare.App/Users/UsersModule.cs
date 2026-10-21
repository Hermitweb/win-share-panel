using WinShare.App.Navigation;
using WinShare.App.Views;
using WinShare.App.Views.Users;
using WinShare.Core.Contracts;

namespace WinShare.App.Users;

/// <summary>
/// 用户与权限模块装配（T3c）：
/// - 服务槽位（类比 AppServices.SystemService；契约服务实现落地后在组合根注入即点亮）
/// - Register(INavigationRegistry)：照 DOCS.md「占位替换 API」把 /users 占位页换成 UsersPageView
///   （不改任何既有共享文件；App.RegisterPages 里的一行占位注册由调度者替换为本模块调用，见接线清单）
/// </summary>
public static class UsersModule
{
    /// <summary>IUserService 服务槽（写路径：CRUD/批量/权限查看；null=空馈送降级）。</summary>
    public static IUserService? UserService { get; set; }

    /// <summary>IShareService 服务槽（读路径：矩阵共享列；null=空馈送降级）。</summary>
    public static IShareService? ShareService { get; set; }

    /// <summary>生产数据馈送（契约服务；缺席源空列表 + 错误信息，对齐 T3a 空馈送模式）。</summary>
    public static IUsersDataFeed BuildDataFeed() =>
        new ContractUsersDataFeed(() => UserService, () => ShareService);

    /// <summary>
    /// 生产 ViewModel（组合根装配后导出走 SaveFileDialog；设计期 Bus 为 null 时传 null 保持可实例化）。
    /// </summary>
    public static UsersViewModel CreateViewModel()
    {
        var composed = AppServices.Bus is not null;
        return new UsersViewModel(
            BuildDataFeed(),
            user: UserService,
            fileSaver: composed ? AppServices.SaveFileWithDialogAsync : null);
    }

    /// <summary>
    /// 占位替换注册（DOCS.md §3）：/users → UsersPageView。
    /// 调度者在 App.RegisterPages() 中调用：UsersModule.Register(AppServices.Registry)。
    /// </summary>
    public static void Register(NavigationRegistry reg)
    {
        reg.Register(new AppPage
        {
            route = "/users",
            titleKey = "Nav.Users",
            glyph = "◉",
            factory = new PageFactory(() => new UsersPageView()),
        });
    }
}
