using System;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using WinShare.App.Shell;
using WinShare.App.Tests.Users;

namespace WinShare.App.Tests.Shell;

/// <summary>
/// 统一图标验收：仓库根 win-share-panel.png 派生的 resources/logo.png 与 resources/icon.ico
/// 都已内嵌进程序集，窗口标题栏图标与系统托盘图标都能加载出来。WPF 图像类型要求 STA 线程。
/// </summary>
public class AppIconTests
{
    [Source("[Source: 仓库根 win-share-panel.png → resources/logo.png（WPF Resource 内嵌 .g.resources）]")]
    [Fact]
    public void 内嵌logo_已嵌入且为256x256PNG()
    {
        BitmapImage? bmp = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try { bmp = AppIcon.WindowIcon(); }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        Assert.NotNull(bmp);
        Assert.Equal(256, bmp!.PixelWidth);
        Assert.Equal(256, bmp.PixelHeight);
    }

    [Source("[Source: 窗口标题栏图标（对齐 Electron TitleBar 的 logo.png）]")]
    [Fact]
    public void 窗口图标_可加载()
    {
        BitmapImage? icon = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try { icon = AppIcon.WindowIcon(); }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        Assert.NotNull(icon);
        Assert.Equal(256, icon!.PixelWidth);
    }

    [Source("[Source: 系统托盘图标（System.Drawing.Icon 只认 ICO，对齐 Electron createTray）]")]
    [Fact]
    public void 托盘图标_可加载()
    {
        System.Drawing.Icon? icon = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try { icon = AppIcon.TrayIcon(); }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        Assert.NotNull(icon);
        Assert.True(icon!.Width > 0 && icon.Height > 0, "托盘图标尺寸为空");
    }

    /// <summary>STA 线程执行 WPF 图像构造（xunit 默认 MTA 线程无法构造 WPF 类型）。</summary>
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
