using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using WinShare.App.Events;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Charts;

/// <summary>
/// 24h 趋势折线图（WPF 原生 Drawing 控件；数据/断点算法全部复用纯函数 <see cref="Dashboard.TrendSeries"/>）。
/// 渲染语义对齐 Dashboard.tsx 的 ECharts 配置：
/// - x 轴 type:'time'（真实时间戳，稀疏段按真实时间拉开）
/// - 缺口 null 断点不连线（connectNulls:false）
/// - showSymbol = points.length < 12
/// - 双系列色 #1677FF / #FA8C16（对齐 TS TREND_COLOR_*；数据可视化色，不随主题变）
/// 渲染器可替换：本控件是「WpfDrawing」实现，T3b+ 接 LiveCharts2(SkiaSharpWPF) 时
/// 保持本控件属性面（Points/Mode/Height）不变，仅换 OnRender 后端（见 DOCS.md 图表渲染器扩展点）。
/// </summary>
public class TrendChartControl : System.Windows.Controls.Control
{
    // 画笔全部缓存复用（原 OnRender 每帧 new Pen/SolidColorBrush）；系列色冻结（主题无关），
    // 坐标轴/网格取主题 token（BrushBorder）——原先写死浅色值，深色主题下刺眼。
    static readonly Brush BrushSessions = Freeze("#66A3FF"); // v2.2.0 第四轮：暗底对比 3.78→6.09 达 AA
    static readonly Brush BrushOpenFiles = Freeze("#FA8C16");
    static readonly Pen SessionsPen = FrozenPen(BrushSessions, 2);
    static readonly Pen OpenFilesPen = FrozenPen(BrushOpenFiles, 2);
    static Pen _axisPen = MakeAxisPen();
    static Pen _gridPen = MakeGridPen();

    static Brush Freeze(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFrom(hex)!;
        b.Freeze();
        return b;
    }

    static Pen FrozenPen(Brush brush, double thickness, DashStyle? dash = null)
    {
        var clone = brush.Clone();
        clone.Freeze();
        var p = new Pen(clone, thickness);
        if (dash is not null) p.DashStyle = dash;
        p.Freeze();
        return p;
    }

    static Pen MakeAxisPen() => FrozenPen(Resolve("BrushBorder", "#C8CED6"), 1);
    static Pen MakeGridPen() => FrozenPen(Resolve("BrushBorder", "#E8EDF4"), 1, DashStyles.Dash);

    IUnsubscribe? _themeSub;

    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<HistoryPoint>), typeof(TrendChartControl),
            new PropertyMetadata((IReadOnlyList<HistoryPoint>?)null, OnVisualChanged));

    /// <summary>24h 窗口内采样点（旧→新）。</summary>
    public IReadOnlyList<HistoryPoint>? Points
    {
        get => (IReadOnlyList<HistoryPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(string), typeof(TrendChartControl),
            new PropertyMetadata("both", OnVisualChanged));

    /// <summary>both | sessions | openFiles（对齐 TS TrendMode）。</summary>
    public string Mode
    {
        get => (string)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as TrendChartControl)?.InvalidateVisual();

    public TrendChartControl()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        Height = 260; // 对齐 TS ReactECharts style height:260
        SnapsToDevicePixels = true;
        // 主题热切换 → 重建坐标轴画笔并重绘。控件随页面每次导航重建，Unloaded 必须退订。
        if (AppServices.Bus is { } bus)
        {
            _themeSub = bus.Subscribe<ThemeChanged>(_ =>
            {
                _axisPen = MakeAxisPen();
                _gridPen = MakeGridPen();
                InvalidateVisual();
            });
        }
        Unloaded += (_, _) => _themeSub?.Unsubscribe();
    }

    protected override void OnRender(System.Windows.Media.DrawingContext dc)
    {
        base.OnRender(dc);
        var pts = Points;
        if (pts is null || pts.Count == 0)
        {
            DrawEmpty(dc);
            return;
        }

        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 60 || h < 40) return;

        // 图区（对齐 ECharts grid：left 44, right 20, top 12, bottom 24）
        var left = 44.0;
        var right = 20.0;
        var top = Mode == "both" ? 30.0 : 12.0;
        var bottom = 24.0;
        var plotW = w - left - right;
        var plotH = h - top - bottom;
        if (plotW <= 10 || plotH <= 10) return;

        // x 轴时间域（真实时间戳 → 稀疏段拉开）
        var tMin = pts.Min(p => p.ts);
        var tMax = pts.Max(p => p.ts);
        if (tMax - tMin < 1) tMax = tMin + 1;
        Func<long, double> xOf = ts => left + (ts - tMin) * plotW / (tMax - tMin);

        // y 轴值域（minInterval:1 → 上限取整）
        var seriesAll = BuildSeries(pts);
        var allValues = seriesAll.SelectMany(s => s.Item1.Where(v => v.Value is not null).Select(v => v.Value!.Value))
            .DefaultIfEmpty(0L);
        var yMax = Math.Max(1.0, Math.Ceiling((double)allValues.Max()));
        Func<long, double, double> yOf = (_, v) => top + plotH - v * plotH / yMax;

        // 网格 + y 轴刻度（3 段；刻度值去重——此前 i=0..3 在 yMax=1 时产出 1,1,0,0 重复刻度）
        var gridPen = _gridPen;
        var drawnTicks = new HashSet<long>();
        for (var i = 0; i <= 3; i++)
        {
            var v = yMax * i / 3.0;
            var y = yOf(0, v);
            dc.DrawLine(gridPen, new Point(left, y), new Point(w - right, y));
            var tick = (long)Math.Round(v);
            if (drawnTicks.Add(tick))
                DrawText(dc, tick.ToString(), left - 8, y - 8, 11, Brushes.Fog);
        }
        // x 轴时间刻度（5 个）
        for (var i = 0; i <= 4; i++)
        {
            var ts = tMin + (long)((tMax - tMin) * i / 4.0);
            var x = xOf(ts);
            DrawText(dc, FormatTime(ts, tMax - tMin), x - 14, h - bottom + 6, 11, Brushes.Fog);
        }
        dc.DrawLine(_axisPen, new Point(left, h - bottom), new Point(w - right, h - bottom));

        // 折线（null 断点 = 线段断开；showSymbol = 点数 < 12）
        var showSymbol = pts.Count < 12;
        foreach (var (series, brush, pen) in seriesAll)
        {
            DrawLineSeries(dc, series, brush, pen, xOf, yOf, showSymbol);
        }

        // 图例（双模式顶部）：右对齐 + 按实测文本宽度排布。
        // 此前按「3 字宽 = 36px」估算且文字居中绘制，色块直接压在首字上（不可读）。
        if (Mode == "both")
        {
            const double ly = 8.0;
            const double dotR = 4.5;      // 圆点半径
            const double dotGap = 8;      // 圆点 → 文字
            const double itemGap = 20;    // 段间距
            var items = new (string Text, Brush Color)[]
            {
                ("连接数", BrushSessions),
                ("打开文件", BrushOpenFiles),
            };
            var widths = items.Select(it => MeasureText(it.Text, 12)).ToArray();
            double total = 0;
            for (int i = 0; i < items.Length; i++)
                total += dotR * 2 + dotGap + widths[i] + (i > 0 ? itemGap : 0);
            double lx = Math.Max(left, w - right - total);
            for (int i = 0; i < items.Length; i++)
            {
                DrawLegend(dc, lx, ly, items[i].Text, items[i].Color);
                lx += dotR * 2 + dotGap + widths[i] + itemGap;
            }
        }
    }

    List<(Dashboard.TrendSeries.TrendValue[], Brush, Pen)> BuildSeries(IReadOnlyList<HistoryPoint> pts)
    {
        var list = new List<(Dashboard.TrendSeries.TrendValue[], Brush, Pen)>();
        if (Mode is "both" or "sessions")
            list.Add((Dashboard.TrendSeries.SessionsSeries(pts).ToArray(), BrushSessions, SessionsPen));
        if (Mode is "both" or "openFiles")
            list.Add((Dashboard.TrendSeries.OpenFilesSeries(pts).ToArray(), BrushOpenFiles, OpenFilesPen));
        return list;
    }

    void DrawLineSeries(DrawingContext dc, Dashboard.TrendSeries.TrendValue[] series, Brush brush, Pen pen,
        Func<long, double> xOf, Func<long, double, double> yOf, bool showSymbol)
    {
        Point? last = null;
        foreach (var v in series)
        {
            if (v.Value is null)
            {
                last = null; // 断点：不连线
                continue;
            }
            var p = new Point(xOf(v.Ts), yOf(v.Ts, v.Value.Value));
            if (last is { } lp)
            {
                dc.DrawLine(pen, lp, p);
            }
            if (showSymbol)
            {
                dc.DrawEllipse(brush, null, p, 3, 3);
            }
            last = p;
        }
    }

    void DrawLegend(DrawingContext dc, double x, double y, string text, Brush color)
    {
        const double dotR = 4.5;
        dc.DrawEllipse(color, null, new Point(x + dotR, y + 6), dotR, dotR);
        // 左对齐（此前居中绘制导致色块压在首字上）
        DrawText(dc, text, x + dotR * 2 + 8, y, 12, Brushes.Ink, TextAlignment.Left);
    }

    /// <summary>文本宽度实测（图例排布用；避免按字数估算导致重叠）。</summary>
    static double MeasureText(string text, double size)
    {
        var ft = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), size, Brushes.Ink)
        { TextAlignment = TextAlignment.Left };
        return ft.WidthIncludingTrailingWhitespace;
    }

    void DrawEmpty(DrawingContext dc)
    {
        DrawText(dc, "（暂无采样点）", ActualWidth / 2 - 40, ActualHeight / 2, 12, Brushes.Fog);
    }

    static string FormatTime(long tsMs, long spanMs = 0)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).LocalDateTime;
        // v2.2.0 第二轮：采样窗口 < 3 分钟时精确到秒（此前 5 分钟窗口内 4 个刻度全是同一分钟，无法读出先后）
        return spanMs > 0 && spanMs < 180_000
            ? dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : dt.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush,
                  TextAlignment align = TextAlignment.Center)
    {
        var ft = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), size, brush)
        { TextAlignment = align };
        dc.DrawText(ft, new Point(x, y));
    }

    /// <summary>主题 token 解析（DynamicResource 对应的实色画刷；缺失回退浅色素值）。</summary>
    static Brush Resolve(string key, string fallbackHex)
    {
        var res = Application.Current?.Resources;
        if (res is not null && res[key] is Brush b) return b;
        return (Brush)new BrushConverter().ConvertFrom(fallbackHex)!;
    }

    static class Brushes
    {
        public static Brush Ink => Resolve("BrushText", "#1F2733");
        public static Brush Fog => Resolve("BrushFog", "#9AA3B2");
    }
}
