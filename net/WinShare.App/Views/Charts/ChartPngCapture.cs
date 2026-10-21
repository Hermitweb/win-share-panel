using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShare.App.Dashboard;

namespace WinShare.App.Views.Charts;

/// <summary>
/// 图表区 PNG 导出（IChartPngCapture 的 WPF 实现）：
/// RenderTargetBitmap（PrintVisual 语义：视觉树直接进位图；pixelRatio 对齐 TS getDataURL pixelRatio:2）
/// + PngBitmapEncoder，背景色随主题（对齐 TS：dark #141B23 / light #F4FAFD，读 BrushChartBg）。
/// 页面代码-behind 在构造时 Register("topshares", 控件)；未注册/未渲染 → null（对齐 TS「图表尚未渲染」）。
/// </summary>
public sealed class ChartPngCapture : IChartPngCapture
{
    static readonly Dictionary<string, System.Windows.Controls.Control> Elements = new();
    static readonly object Gate = new();

    public static void Register(string key, System.Windows.Controls.Control element)
    {
        lock (Gate) Elements[key] = element;
    }

    public Task<byte[]?> CaptureAsync(string chartKey)
    {
        var tcs = new TaskCompletionSource<byte[]?>();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        dispatcher.Invoke(() =>
        {
            try
            {
                System.Windows.Controls.Control? el = null;
                lock (Gate) { Elements.TryGetValue(chartKey, out el); }
                tcs.SetResult(el is null ? null : CaptureNow(el));
            }
            catch
            {
                tcs.SetResult(null);
            }
        });
        return tcs.Task;
    }

    static byte[]? CaptureNow(System.Windows.Controls.Control el)
    {
        var w = (int)Math.Round(el.ActualWidth);
        var h = (int)Math.Round(el.ActualHeight);
        if (w < 8 || h < 8) return null; // 未渲染（隐藏页/空容器）

        const int pixelRatio = 2; // 对齐 TS getDataURL({ pixelRatio: 2 })
        var bg = ResolveBackground();

        // 1) 控件像素（透明底）
        var chart = new RenderTargetBitmap(w * pixelRatio, h * pixelRatio, 192, 192, PixelFormats.Pbgra32);
        chart.Render(el);

        // 2) 背景 + 控件合成（对齐 TS getDataURL backgroundColor）
        var final = new DrawingVisual();
        using (var dc = final.RenderOpen())
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, w * pixelRatio, h * pixelRatio));
            dc.DrawImage(chart, new Rect(0, 0, w * pixelRatio, h * pixelRatio));
        }
        var outBmp = new RenderTargetBitmap(w * pixelRatio, h * pixelRatio, 192, 192, PixelFormats.Pbgra32);
        outBmp.Render(final);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(outBmp));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    static Brush ResolveBackground()
    {
        var res = Application.Current?.Resources;
        if (res is not null && res["BrushChartBg"] is Brush b) return b;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F4FAFD")!);
    }
}
