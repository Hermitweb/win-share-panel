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
    string _route = "/";
    string? _attention;
    UserControl? _currentView;

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
        _currentView = registry.Find(route)!.factory.Create();
        RouteChanged?.Invoke(route);
    }

    public string? TakeAttention()
    {
        var a = _attention;
        _attention = null;
        return a;
    }
}
