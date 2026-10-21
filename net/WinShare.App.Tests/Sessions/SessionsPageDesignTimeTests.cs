using System.Threading;
using WinShare.App.Navigation;
using WinShare.App.Sessions;
using WinShare.App.Views.Sessions;

namespace WinShare.App.Tests.Sessions;

/// <summary>
/// 设计期可实例化证明（验收标准）：SessionsPageView 构造无参、依赖全部延迟到 Loaded——
/// 无 AppServices 组合、无会话服务实现也可 new（不启动定时器、不取数、不崩）。
/// WPF 控件构造要求 STA 线程（InputManager）→ 测试在 STA 线程上执行。
/// </summary>
public class SessionsPageDesignTimeTests
{
    [Source("[Source: net/WinShare.App/DOCS.md §3 页面要求：UserControl；构造无参]")]
    [Fact]
    public void SessionsPage_可设计期实例化()
    {
        Object? view = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try { view = new SessionsPageView(); }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        Assert.IsAssignableFrom<System.Windows.Controls.UserControl>(view);
    }

    [Source("[Source: net/WinShare.App/DOCS.md §3 占位替换 API]")]
    [Fact]
    public void SessionsModule_Register_生成真实页面工厂()
    {
        Object? created = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try
            {
                var reg = new NavigationRegistry();
                SessionsModule.Register(reg);
                Assert.True(reg.Has("/sessions"));
                var page = reg.Find("/sessions")!;
                Assert.Equal("Nav.Sessions", page.titleKey);
                created = page.factory.Create();
            }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        Assert.IsType<SessionsPageView>(created);
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
