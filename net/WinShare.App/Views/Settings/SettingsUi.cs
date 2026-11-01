using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WinShare.App.Settings;

namespace WinShare.App.Views.Settings;

/// <summary>
/// 设置模块 UI 构造工具（纯代码构建，不依赖 XAML 资源，设计期/单测均可 new）。
/// 颜色一律优先 DynamicResource 主题 token（缺 Application/字典时回退 Light 值，
/// 值与 Theming/ThemeTokens.Light.xaml 对齐），不臆造色板。
/// </summary>
public static class SettingsUi
{
    // ===== 主题 token 取用（缺 Application/字典时回退 Light 值，与 DOCS.md Key 表一致） =====

    public static Brush Token(string key, Brush fallback)
    {
        var app = Application.Current;
        if (app is not null)
        {
            try
            {
                if (app.Resources.Contains(key) && app.Resources[key] is Brush b) return b;
            }
            catch { /* 资源缺失走回退色 */ }
        }
        return fallback;
    }

    public static Brush Token(string key, string hexFallback)
    {
        var fb = (Brush)new BrushConverter().ConvertFromString(hexFallback)!;
        return Token(key, fb);
    }

    /// <summary>
    /// 活引用（SetResourceReference）：控件属性跟随主题 token，热切换实时刷新。
    /// Token() 是构建期快照——代码生成的 UI 在主题切换后会残留旧色
    /// （v2.0.1 用户实测：浅色页面残留深色卡片）。**静态外观一律走本方法**；
    /// Token() 仅保留给「随状态变化的语义色」（每次状态变化时重新解析即可自愈）。
    /// key 缺失时属性保持 WPF 默认值（仅无主题字典的设计期/单测宿主会发生）。
    /// </summary>
    public static T Live<T>(this T el, System.Windows.DependencyProperty dp, string key) where T : FrameworkElement
    {
        el.SetResourceReference(dp, key);
        return el;
    }

    /// <summary>取 App.xaml 里的样式（缺 Application/资源时返回 null，调用方跳过设置）。</summary>
    public static Style? Style(string key)
    {
        var app = Application.Current;
        if (app is null) return null;
        try
        {
            if (app.Resources.Contains(key) && app.Resources[key] is Style s) return s;
        }
        catch { /* 资源缺失 */ }
        return null;
    }

    public static Border Card(System.Windows.UIElement content, double padding = 16)
    {
        // 优先套 App.xaml 的 CardStyle（磨砂玻璃 + 大圆角 + 柔光阴影）；缺资源时回退到手绘边框。
        // 之前这里硬编码了 #FFFFFF 底 + 6px 圆角，在浅色页面上几乎看不出卡片边界 → 整页像裸控件堆砌。
        // 手绘卡片（不用 CardStyle）：CardStyle 的 DropShadowEffect 在这两页 14 张卡上反复离屏渲染，
        // 且实测套上后应用设置页整页未测量（控件 BoundingRectangle=Empty、文字节点消失）。
        // 用玻璃底 + 大圆角 + 边框达到同样的层次感，零 Effect 开销。
        var border = new Border
        {
            Child = content,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(padding),
            Margin = new Thickness(0, 0, 0, 16),
        };
        return border.Live(Border.BackgroundProperty, "BrushBgMist")
                     .Live(Border.BorderBrushProperty, "BrushBorder");
    }

    /// <summary>页头标题（i18n 键 → PageTitle 样式；六页统一 22 号 token 字号）。</summary>
    public static TextBlock PageTitle(string resourceKey)
    {
        var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        if (Style("PageTitle") is { } s) tb.Style = s;
        else
        {
            tb.FontSize = 22;
            tb.FontWeight = FontWeights.SemiBold;
            tb.Foreground = Token("BrushText", "#1F2733");
        }
        // 走 DynamicResource 语义（SetResourceReference）：语言/键热替换时标题跟随
        tb.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, resourceKey);
        return tb;
    }

    public static TextBlock CardTitle(string text)
    {
        var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 12) };
        if (Style("SectionTitle") is { } s) tb.Style = s;
        else
        {
            tb.FontSize = 14;
            tb.FontWeight = FontWeights.SemiBold;
            tb.Foreground = Token("BrushText", "#1F2733");
        }
        return tb;
    }

    public static TextBlock FogText(string text, double top = 0, double bottom = 0)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, bottom),
        };
        return tb.Live(TextBlock.ForegroundProperty, "BrushFog");
    }

    /// <summary>Success/Error 提示面（订阅 VM 结果面；对齐 antd message + 卡片内 Alert 语义）。</summary>
    public static Border MessageStrip(SettingsCardBase vm)
    {
        var success = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }
            .Live(TextBlock.ForegroundProperty, "BrushSuccess");
        var error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }
            .Live(TextBlock.ForegroundProperty, "BrushDanger");
        void Update()
        {
            var errText = vm.Error ?? (vm as UpdateCheckerViewModel)?.ErrorText
                ?? (vm as SecurityCardViewModel)?.ReportError
                ?? (vm as ProtocolDetectionViewModel)?.DegradedNote;
            success.Text = vm.Success ?? "";
            success.Visibility = vm.Success is null ? Visibility.Collapsed : Visibility.Visible;
            error.Text = errText ?? "";
            error.Visibility = string.IsNullOrEmpty(errText) ? Visibility.Collapsed : Visibility.Visible;
        }
        vm.PropertyChanged += (_, _) => { Update(); };
        Update();
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(success);
        stack.Children.Add(error);
        return new Border { Padding = new Thickness(0), Child = stack, Margin = new Thickness(0, 6, 0, 0) };
    }

    /// <summary>卡片标题行（标题在上，操作按钮组在下逐行排列；纵向单列布局，按钮仍走 SettingsUi.Button 工厂）。</summary>
    public static StackPanel HeaderRow(string title, params Control[] extras)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var tb = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        }.Live(TextBlock.ForegroundProperty, "BrushText");
        p.Children.Add(tb);
        foreach (var c in extras)
        {
            c.HorizontalAlignment = HorizontalAlignment.Left;
            p.Children.Add(c);
        }
        return p;
    }

    public static Button Button(string text, RoutedEventHandler onClick, bool primary = false, string? stopHint = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), ToolTip = stopHint };
        // 套设计系统：主按钮=马卡龙渐变，次按钮=磨砂白底浅蓝描边（之前是裸 Button，系统灰底）
        if (Style(primary ? "PrimaryButton" : "DashButton") is { } s) b.Style = s;
        else
        {
            b.Padding = new Thickness(12, 4, 12, 4);
            b.MinHeight = 28;
            if (primary)
            {
                b.Live(System.Windows.Controls.Button.BackgroundProperty, "BrushPrimary");
                b.Foreground = Brushes.White;
                b.BorderThickness = new Thickness(0);
            }
        }
        if (onClick is not null) b.Click += onClick; // 禁用占位按钮允许 null 处理器
        return b;
    }

    /// <summary>开关字段（CheckBox + 标签；DataContext 取传入 VM 实例；纵向布局下每个开关独立一行）。</summary>
    public static CheckBox Toggle(string label, string vmPath, object vm)
    {
        var cb = new CheckBox { Content = label, Margin = new Thickness(0, 3, 16, 3) };
        if (Style("CheckBoxSoft") is { } boxStyle) cb.Style = boxStyle;
        else cb.Live(Control.ForegroundProperty, "BrushText");
        // 不显式设 DataContext：继承宿主面板（_form）的 DataContext（=卡 VM），
        // 表单回填时重置宿主 DataContext 即全量重拉
        _ = vm;
        cb.SetBinding(CheckBox.IsCheckedProperty, new Binding(vmPath) { Mode = BindingMode.TwoWay });
        return cb;
    }

    /// <summary>开关组（纵向单列：每个 CheckBox 独立一行；重渲染时只能 Clear+Add 回同一面板）。</summary>
    public static StackPanel ToggleGroup(params CheckBox[] boxes)
    {
        // 纵向单列。曾试过套 UniformGrid 做两列：UniformGrid 在纵向 StackPanel（高度无限）里
        // 会算出异常尺寸，把整页布局撑坏 —— 实测应用设置页所有控件 BoundingRectangle=Empty
        // （未测量未排列）、文字节点全部消失。保持最朴素的 StackPanel。
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var b in boxes) p.Children.Add(b);
        return p;
    }

    /// <summary>数字字段行（标签 + TextBox，失焦解析回 VM int/long；解析失败保留 VM 原值）。</summary>
    public static StackPanel NumberField(string label, string vmPath, object vm, bool isLong = false)
    {
        var tb = new TextBox { Width = 100, VerticalAlignment = VerticalAlignment.Center };
        if (Style("InputBoxSoft") is { } st1) tb.Style = st1;
        tb.SetBinding(TextBox.TextProperty, new Binding(vmPath)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            Converter = isLong ? new StringLongConverter() : new StringIntConverter(),
        });
        return FieldRow(label, tb);
    }

    /// <summary>文本字段行（标签 + TextBox 两-way）。</summary>
    public static StackPanel TextField(string label, string vmPath, object vm, double width = 240)
    {
        var tb = new TextBox { Width = width, VerticalAlignment = VerticalAlignment.Center };
        if (Style("InputBoxSoft") is { } st2) tb.Style = st2;
        tb.SetBinding(TextBox.TextProperty, new Binding(vmPath)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
        });
        return FieldRow(label, tb);
    }

    /// <summary>下拉字段行（string 选项集合 + 选中项写回 VM 字符串镜像属性）。</summary>
    public static StackPanel ComboField(string label, string vmPath, object vm, IReadOnlyList<string> items, double width = 160)
    {
        var cb = new ComboBox { Width = width, VerticalAlignment = VerticalAlignment.Center };
        if (Style("ComboBoxSoft") is { } comboStyle) cb.Style = comboStyle;
        cb.ItemsSource = items;
        cb.SetBinding(ComboBox.SelectedItemProperty, new Binding(vmPath) { Mode = BindingMode.TwoWay });
        return FieldRow(label, cb);
    }

    /// <summary>字段行（标签在上、控件在下；纵向单列，每个字段独占一块。
    /// 子元素顺序保持 [0]=标签、[1]=控件，调用方可按索引取控件（如告警卡 idleMap 提交））。</summary>
    public static StackPanel FieldRow(string label, Control control)
    {
        // 标签在左、控件在右（原先「标签在上、控件在下」的纵向堆叠会让每个字段占两行，
        // 整页看起来像一长串裸控件）。保持 Children[0]=标签 / Children[1]=控件 的索引契约。
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 150,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        }.Live(TextBlock.ForegroundProperty, "BrushFog"));
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(0);
        row.Children.Add(control);
        return row;
    }

    /// <summary>
    /// 服务状态面的子元素序列（刷新用：调用方 Clear 自身面板后 Add 这些**新构造**的子元素。
    /// 切勿把「另一个已挂载面板」的 Children 逐个搬过来——WPF 逻辑父唯一，会抛
    /// InvalidOperationException「指定的元素已经是另一元素的逻辑子元素」）。
    /// </summary>
    public static List<System.Windows.UIElement> SvcStatusStripChildren(WinShare.Core.Contracts.ServiceStatus? status, string protocolLabel)
    {
        _ = protocolLabel;
        var list = new List<System.Windows.UIElement>
        {
            new TextBlock { Text = "服务状态", Margin = new Thickness(0, 0, 0, 2) }
                .Live(TextBlock.ForegroundProperty, "BrushFog")
        };
        if (status is null)
        {
            list.Add(new TextBlock { Text = "未读取" }.Live(TextBlock.ForegroundProperty, "BrushFog"));
        }
        else
        {
            var running = status.status == WinShare.Core.Contracts.ServiceState.Running;
            list.Add(new TextBlock
            {
                Text = status.status.ToString(),
                Foreground = Token(running ? "BrushSuccess" : "BrushDanger", running ? "#52C41A" : "#FF4D4F"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
            });
            list.Add(new TextBlock { Text = $"启动类型：{status.startType}", Margin = new Thickness(0, 0, 0, 2) }.Live(TextBlock.ForegroundProperty, "BrushFog"));
            list.Add(new TextBlock { Text = $"服务名：{status.name}" }.Live(TextBlock.ForegroundProperty, "BrushFog"));
        }
        return list;
    }

    /// <summary>
    /// 服务状态面（纵向单列：状态 / 启动类型 / 服务名逐行排列；状态 null 时显示「未读取」）。
    /// 重渲染约束：调用方只能 Clear+Add 回同一面板（见 ProtocolSectionView 基座注释）。
    /// </summary>
    public static StackPanel SvcStatusStrip(WinShare.Core.Contracts.ServiceStatus? status, string protocolLabel)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var c in SvcStatusStripChildren(status, protocolLabel)) panel.Children.Add(c);
        return panel;
    }

    /// <summary>antd Tag 色名 → 画刷（green/orange/red/volcano/blue/gray → 主题 token；
    /// 原实现 Token("BrushText", hex) 恒返回 BrushText，所有状态色失效为单色——已修）。</summary>
    public static Brush TagColor(string antdColor) => Token(antdColor switch
    {
        "green" => "BrushSuccess",
        "orange" => "BrushWarnDot",
        "red" => "BrushDangerStrong",
        "volcano" => "BrushDangerDeep",
        "blue" => "BrushInfoFg",
        _ => "BrushFog",
    }, antdColor switch
    {
        "green" => "#52C41A",
        "orange" => "#FAAD14",
        "red" => "#FF4D4F",
        "volcano" => "#FF5533",
        "blue" => "#1677FF",
        _ => "#8C8C8C",
    });

    /// <summary>等宽日志框（只读、滚动、等宽字体；对齐 electron 版 pre 等宽语义）。</summary>
    public static TextBox MonospaceView(IReadOnlyList<string> lines, double height = 320)
    {
        var tb = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Height = height,
            Text = string.Join(Environment.NewLine, lines),
            BorderThickness = new Thickness(1),
        };
        return tb.Live(TextBox.BackgroundProperty, "BrushBg")
                  .Live(TextBox.ForegroundProperty, "BrushText")
                  .Live(TextBox.BorderBrushProperty, "BrushBorder");
    }
}

/// <summary>int ↔ 文本 绑定转换（解析失败返回 UnsetValue → VM 原值保留）。</summary>
public sealed class StringIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value?.ToString() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string s && int.TryParse(s.Trim(), out var n)) return n;
        return DependencyProperty.UnsetValue;
    }
}

/// <summary>long ↔ 文本 绑定转换（WebDAV 字节数字段）。</summary>
public sealed class StringLongConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value?.ToString() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string s && long.TryParse(s.Trim(), out var n)) return n;
        return DependencyProperty.UnsetValue;
    }
}
