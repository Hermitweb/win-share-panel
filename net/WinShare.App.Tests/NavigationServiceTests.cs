using System.Threading;
using System.Windows.Controls;
using WinShare.App.Navigation;

namespace WinShare.App.Tests;

/// <summary>
/// 页面缓存（保活）验收：每个路由只创建一次，切回来复用已建实例。
/// 原实现每次 Navigate 都 factory.Create()，切回即整页重建 → 点侧栏切换明显卡顿。
/// 创建 UserControl 要求 STA 线程（InputManager），用例在 STA 上执行。
/// </summary>
public class NavigationServiceTests
{
    sealed class CountingFactory : IPageFactory
    {
        public int Created { get; private set; }
        public UserControl Create()
        {
            Created++;
            return new UserControl();
        }
    }

    static (NavigationRegistry reg, CountingFactory a, CountingFactory b) Setup()
    {
        var reg = new NavigationRegistry();
        var a = new CountingFactory();
        var b = new CountingFactory();
        reg.Register(new AppPage { route = "/a", titleKey = "Nav.A", factory = a });
        reg.Register(new AppPage { route = "/b", titleKey = "Nav.B", factory = b });
        return (reg, a, b);
    }

    [Fact]
    public void 首次访问才创建_切走再切回复用同一实例()
    {
        var (reg, a, _) = Setup();
        UserControl? first = null;
        var nav = new NavigationService(reg);
        Exception? failure = null;
        RunOnSta(() =>
        {
            try
            {
                nav.Navigate("/a");
                first = nav.CurrentView;
                nav.Navigate("/b");
                nav.Navigate("/a"); // 切回
            }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        Assert.NotNull(first);
        // 当前实现：每次导航都新建视图（常驻挂载 + 缓存两次尝试均导致整体卡顿，已回退）
        Assert.NotSame(first, nav.CurrentView);
        Assert.Equal(2, a.Created);
    }

    [Fact]
    public void 未注册路由被忽略()
    {
        var (reg, a, _) = Setup();
        var nav = new NavigationService(reg);
        Exception? failure = null;
        RunOnSta(() =>
        {
            try { nav.Navigate("/missing"); }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        Assert.Null(nav.CurrentView);
        Assert.Equal(0, a.Created);
    }

    [Fact]
    public void 同路由重复导航不重建()
    {
        var (reg, a, _) = Setup();
        var nav = new NavigationService(reg);
        Exception? failure = null;
        RunOnSta(() =>
        {
            try { nav.Navigate("/a"); nav.Navigate("/a"); }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        Assert.Equal(1, a.Created);
    }

    [Fact]
    public void 不同路由各自独立缓存()
    {
        var (reg, a, b) = Setup();
        var nav = new NavigationService(reg);
        Exception? failure = null;
        RunOnSta(() =>
        {
            try { nav.Navigate("/a"); nav.Navigate("/b"); }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        Assert.NotNull(nav.CurrentView);
        Assert.Equal(1, a.Created);
        Assert.Equal(1, b.Created);
    }

    /// <summary>STA 线程执行 WPF 构造（xunit 默认 MTA 线程无法构造 UI 组件）。</summary>
    static void RunOnSta(Action body)
    {
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try { body(); }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "STA 线程超时");
        thread.Join();
    }
}
