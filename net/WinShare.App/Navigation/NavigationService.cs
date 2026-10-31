using System.Windows.Controls;

namespace WinShare.App.Navigation;

/// <summary>
/// 导航服务（WPF 框架页切换层）：CurrentPage 绑定主窗口 ContentControl；
/// RouteChanged 事件供侧栏高亮与「需要关注」跳转订阅。
/// </summary>
public interface INavigationService
{
    string CurrentRoute { get; }
    UserControl? CurrentView { get; }
    event Action<string>? RouteChanged;
    void Navigate(string route, string? attention = null);
    /// <summary>目标页透传的定位参数（如 disk:E:），框架消费后清零。</summary>
    string? TakeAttention();
}

public sealed class NavigationService(NavigationRegistry registry) : INavigationService
{
    // 初值必须是空串（=「尚未导航」）：若初值写成 "/"，首次 Navigate("/") 会命中
    // 「同路由不重建视图」分支直接返回，_currentView 永远为 null → 启动后主内容区空白。
    string _route = "";
    string? _attention;
    UserControl? _currentView;

    // 页面缓存（对齐 Electron 的路由保活）三次实现均已回退，不再走这条路：
    // 缓存 + ContentControl 重挂载会让页面在快速切换时大面积失败（30 次切换 19 次失败）。
    // 现实方案=每次 Navigate factory.Create() 重建 + 各页 Loaded 走 PageRefreshThrottle 节流。

    public string CurrentRoute => _route;
    public UserControl? CurrentView => _currentView;

    public event Action<string>? RouteChanged;

    public void Navigate(string route, string? attention = null)
    {
        if (!registry.Has(route)) return; // 未注册路由忽略（对齐 React Router 空跳转）
        _attention = attention;
        if (route == _route)
        {
            // 同路由再导航（如「查看共享」在共享页内）：只换注意参数，不重建视图
            RouteChanged?.Invoke(route);
            return;
        }
        _route = route;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 【第三次回退缓存】缓存 + ContentControl 重挂载会让页面在快速切换时大面积失败
        // （30 次切换 19 次失败）。三次尝试均失败，不再走这条路。
        _currentView = registry.Find(route)!.factory.Create();
        WinShare.Core.Lib.Logger.Info("main", $"[nav] 构造 {route} {sw.Elapsed.TotalMilliseconds:F1}ms");
        RouteChanged?.Invoke(route);
    }

    public string? TakeAttention()
    {
        var a = _attention;
        _attention = null;
        return a;
    }
}
