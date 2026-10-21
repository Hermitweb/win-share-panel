using WinShare.App.Navigation;
using WinShare.App.Views;
using WinShare.App.Views.Sessions;
using WinShare.Core.Contracts;

namespace WinShare.App.Sessions;

/// <summary>
/// 会话监控模块装配（T3c）：
/// - 服务槽位（ISessionService=SMB 会话；IProtocolAdapter 字典=NFS 会话面；
///   类比 AppServices.SystemService 槽位模式，契约服务实现落地后组合根注入即点亮）
/// - Register：照 DOCS.md「占位替换 API」把 /sessions 占位页换成 SessionsPageView
///   （不改任何既有共享文件；App.RegisterPages 里的一行占位注册由调度者替换为本模块调用，见接线清单）
/// </summary>
public static class SessionsModule
{
    /// <summary>ISessionService 服务槽（SMB 会话/打开文件；null=空馈送降级）。</summary>
    public static ISessionService? SessionService { get; set; }

    /// <summary>
    /// 协议适配器槽（NFS 会话面；key=协议）。缺席=该协议空态（对齐 TS adapter 缺席静默）。
    /// FTP/WebDAV 无原生会话 API，不在此列。
    /// </summary>
    public static IReadOnlyDictionary<Protocol, IProtocolAdapter>? Adapters { get; set; }

    /// <summary>生产数据馈送（契约服务；缺席源空列表，对齐 T3a 空馈送模式）。</summary>
    public static ISessionsDataFeed BuildDataFeed() =>
        new ContractSessionsDataFeed(() => SessionService, () => Adapters);

    /// <summary>生产 ViewModel（纯逻辑；通知/气泡由视图接线）。</summary>
    public static SessionsViewModel CreateViewModel() =>
        new(BuildDataFeed());

    /// <summary>
    /// 占位替换注册（DOCS.md §3）：/sessions → SessionsPageView。
    /// 调度者在 App.RegisterPages() 中调用：SessionsModule.Register(AppServices.Registry)。
    /// </summary>
    public static void Register(NavigationRegistry reg)
    {
        reg.Register(new AppPage
        {
            route = "/sessions",
            titleKey = "Nav.Sessions",
            glyph = "\uE9D9",
            factory = new PageFactory(() => new SessionsPageView()),
        });
    }
}
