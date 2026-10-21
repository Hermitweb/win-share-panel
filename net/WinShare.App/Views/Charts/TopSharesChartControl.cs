using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WinShare.App.Dashboard;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Charts;

/// <summary>
/// 热门共享连接数横向条形图（WPF 原生；对齐 Dashboard.tsx chartOption：
/// 条形按 topShares 顺序、色=协议配色 PROTOCOL_COLOR、圆角上端、barMaxWidth≈40、
/// x 轴=名称 category、tooltip 由界面层承载）。
/// 渲染器可替换（见 TrendChartControl 说明 / DOCS.md）。
/// </summary>
public class TopSharesChartControl : System.Windows.Controls.Control
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<TopShare>), typeof(TopSharesChartControl),
            new PropertyMetadata((IReadOnlyList<TopShare>?)null, OnVisualChanged));

    public IReadOnlyList<TopShare>? Items
    {
        get => (IReadOnlyList<TopShare>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as TopSharesChartControl)?.InvalidateVisual();

    public TopSharesChartControl()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        Height = 280; // 对齐 TS ReactECharts style height:280
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(System.Windows.Media.DrawingContext dc)
    {
        base.OnRender(dc);
        var items = Items;
        if (items is null || items.Count == 0)
        {
            DrawText(dc, "（暂无共享连接数据）", ActualWidth / 2 - 46, ActualHeight / 2, 12, Resolve("BrushFog", "#9AA3B2"));
            return;
        }

        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40) return;

        // 布局：左标签区 40%、右条形区；对齐 ECharts grid left:40
        var labelW = w * 0.40;
        var barLeft = labelW + 8;
        var barAreaW = w - barLeft - 20;
        if (barAreaW <= 10) return;

        var top = 20.0;
        var bottom = 30.0;
        var rowH = (h - top - bottom) / items.Count;
        var barH = Math.Min(20.0, rowH * 0.55);
        var maxConn = items.Max(i => i.connections);
        if (maxConn <= 0) maxConn = 1;

        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i]!;
            var y = top + rowH * i + (rowH - barH) / 2;

            // 名称标签（category 轴值）
            DrawText(dc, Shorten(it.name, 12), 8, y + barH / 2 - 8, 12, Resolve("BrushText", "#1F2733"));

            // 条形（上端圆角 6，对齐 ECharts borderRadius:[6,6,0,0]；宽≤40 由 barMaxWidth 语义 → 这里按占比 + 上限）
            var bw = Math.Min(40.0, barAreaW * it.connections / maxConn);
            var color = WpfColors.ToBrush(ProtocolPalette.Color(it.protocol));
            DrawRoundedTopBar(dc, barLeft, y, bw, barH, 6, color);

            // 值（对齐 tooltip 文本：连接数）
            DrawText(dc, it.connections.ToString(), barLeft + bw + 6, y + barH / 2 - 8, 12, Resolve("BrushFog", "#9AA3B2"));
        }

        // 底部说明（Top5）
        DrawText(dc, "Top5 · 连接数", 8, h - 18, 11, Resolve("BrushFog", "#9AA3B2"));
    }

    static string Shorten(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    static void DrawRoundedTopBar(DrawingContext dc, double x, double y, double w, double h, double r, Brush fill)
    {
        if (w <= 0) return;
        r = Math.Min(Math.Min(r, h / 2), w / 2);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(x + r, y), true, true);
            ctx.LineTo(new Point(x + w - r, y), true, false);
            ctx.ArcTo(new Point(x + w, y + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(x + w, y + h), true, false);
            ctx.LineTo(new Point(x, y + h), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(fill, null, geo);
    }

    static Brush Resolve(string key, string fallbackHex)
    {
        var res = Application.Current?.Resources;
        if (res is not null && res[key] is Brush b) return b;
        return (Brush)new BrushConverter().ConvertFrom(fallbackHex)!;
    }

    void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush)
    {
        var ft = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), size, brush);
        dc.DrawText(ft, new Point(x, y));
    }
}
