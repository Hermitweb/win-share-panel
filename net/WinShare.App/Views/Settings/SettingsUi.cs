using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WinShare.App.Settings;

namespace WinShare.App.Views.Settings;

/// <summary>
/// 设置模块 UI 构造工具（纯代码构建，不依赖 XAML 资源，设计期/单测均可 new）。
/// 颜色一律优先 DynamicResource 主题 token（缺 Application/字典时回退 Light 值，
/// 值与 Theming/ThemeTokens.Light.xaml 对齐），不臆造色板。
/// </summary>
public static class SettingsUi
{
    /// <summary>v2.2.0 第二轮：横向组合间距标尺（替代 8/10/12/14/24/28 魔法数）。</summary>
    public static class Gap
    {
        public const double S = 8;
        public const double M = 12;
        public const double L = 24;
    }
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
        // 优先套 App.xaml 的 CardStyle（磨砂玻璃 + 大圆角）；缺资源时回退到手绘边框。
        // 之前这里硬编码了 #FFFFFF 底 + 6px 圆角，在浅色页面上几乎看不出卡片边界 → 整页像裸控件堆砌。
        // 手绘卡片（不用 CardStyle）：CardStyle 的 DropShadowEffect 在这两页 14 张卡上反复离屏渲染，
        // 且实测套上后应用设置页整页未测量（控件 BoundingRectangle=Empty、文字节点消失）。
        // 用玻璃底 + 大圆角 + 边框达到同样的层次感，零 Effect 开销。
        // v2.3：卡面与仪表盘对齐——BrushCardGradient（顶部提亮的纵向微渐变）替代 BrushBgMist 平涂。
        var border = new Border
        {
            Child = content,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(padding),
            Margin = new Thickness(0, 0, 0, 16),
        };
        return border.Live(Border.BackgroundProperty, "BrushCardGradient")
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
            LineHeight = 20, // v2.2.0 第五批：12px 雾字行高 19→20（折行后过于紧凑）
            LineStackingStrategy = System.Windows.LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, top, 0, bottom),
        };
        return tb.Live(TextBlock.ForegroundProperty, "BrushFog");
    }

    /// <summary>
    /// 转圈加载指示（代码生成；用 BeginAnimation 而非 Storyboard.TargetName，避免多处同名作用域问题）。
    /// 圆环底色/弧线走主题 token（活引用，热切换跟随）。
    /// </summary>
    public static Grid Spinner(double size = 18)
    {
        double stroke = Math.Max(2, size / 9);
        double r = Math.Max(1, size / 2 - stroke / 2);
        double c = size / 2;
        var ring = new Ellipse { Width = size, Height = size, StrokeThickness = stroke };
        ring.SetResourceReference(Shape.StrokeProperty, "BrushBorder");
        var arc = new Path
        {
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse($"M {c:0.#},{stroke / 2:0.#} A {r:0.#},{r:0.#} 0 0 1 {c + r:0.#},{c:0.#}"),
        };
        arc.SetResourceReference(Shape.StrokeProperty, "BrushPrimary");
        var rotate = new RotateTransform { CenterX = c, CenterY = c };
        arc.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        });
        var box = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(ring);
        box.Children.Add(arc);
        return box;
    }

    /// <summary>局部加载行：转圈 + 说明文字（用于「读取中/检测中」等占位；文字垂直居中）。</summary>
    public static StackPanel LoadingRow(string text, double size = 16)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(Spinner(size));
        var tb = FogText(text);
        tb.VerticalAlignment = VerticalAlignment.Center;
        tb.Margin = new Thickness(8, 0, 0, 0);
        p.Children.Add(tb);
        return p;
    }

    /// <summary>
    /// 高级参数折叠区（v2.2.0 第二轮：设置页协议 Tab 的专家字段收敛——新手默认收起、
    /// 专家默认展开；展开后照常查看/编辑/提交，合并-回写语义下收起不影响现值）。
    /// Header 走活引用（主题热切换实时跟随）。
    /// </summary>
    public static Expander ExpanderField(string header, System.Windows.UIElement content, bool startExpanded)
    {
        var exp = new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = startExpanded,
            Margin = new Thickness(0, 4, 0, 8),
        };
        exp.SetResourceReference(Expander.ForegroundProperty, "BrushText");
        return exp;
    }

    /// <summary>Success/Error 提示面（订阅 VM 结果面；对齐 antd message + 卡片内 Alert 语义）。</summary>
    public static Border MessageStrip(SettingsCardBase vm)
    {
        var success = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }
            .Live(TextBlock.ForegroundProperty, "BrushSuccess");
        var error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }
            .Live(TextBlock.ForegroundProperty, "BrushDanger");
        var busyText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap }
            .Live(TextBlock.ForegroundProperty, "BrushFog");
        busyText.VerticalAlignment = VerticalAlignment.Center;
        busyText.Margin = new Thickness(8, 0, 0, 0);
        var busy = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        busy.Children.Add(Spinner(16));
        busy.Children.Add(busyText);
        void Update()
        {
            var errText = vm.Error ?? (vm as UpdateCheckerViewModel)?.ErrorText
                ?? (vm as SecurityCardViewModel)?.ReportError
                ?? (vm as ProtocolDetectionViewModel)?.DegradedNote;
            success.Text = vm.Success ?? "";
            success.Visibility = vm.Success is null ? Visibility.Collapsed : Visibility.Visible;
            error.Text = errText ?? "";
            error.Visibility = string.IsNullOrEmpty(errText) ? Visibility.Collapsed : Visibility.Visible;
            // v2.2.0：Busy 期间显示进度说明（此前 16-21s 拉取期间只有按钮禁用，静默得像卡死）
            busyText.Text = vm.BusyText ?? "正在执行…";
            busy.Visibility = vm.BusyVisible ? Visibility.Visible : Visibility.Collapsed;
        }
        // VM 的 PropertyChanged 可能在后台线程（Persist / 刷新续体）触发，
        // 直接写控件会抛「调用线程无法访问此对象」→ 统一转投 UI 线程。
        void UpdateOnUi()
        {
            if (success.Dispatcher.CheckAccess()) Update();
            else success.Dispatcher.BeginInvoke(new Action(Update));
        }
        vm.PropertyChanged += (_, _) => { UpdateOnUi(); };
        Update();
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(busy);
        stack.Children.Add(success);
        stack.Children.Add(error);
        return new Border { Padding = new Thickness(0), Child = stack, Margin = new Thickness(0, 6, 0, 0) };
    }

    /// <summary>卡片标题行（标题在上，操作组在下逐行排列；extras 可传 Control 或横向 FrameworkElement 组合行）。</summary>
    public static StackPanel HeaderRow(string title, params FrameworkElement[] extras)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (!string.IsNullOrEmpty(title))
        {
            var tb = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
            }.Live(TextBlock.ForegroundProperty, "BrushText");
            p.Children.Add(tb);
        }
        // v2.2.0 第二轮修复：多个操作此前各占一行（宽屏下挤成左侧窄柱）——统一横排一行。
        // extras 本身若是横向组合行（如检查更新卡的按钮组）则作为整体放入。
        if (extras.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in extras)
            {
                c.HorizontalAlignment = HorizontalAlignment.Left;
                if (c.Margin == default) c.Margin = new Thickness(0, 0, Gap.M, 0);
                row.Children.Add(c);
            }
            p.Children.Add(row);
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
    public static WrapPanel ToggleGroup(params CheckBox[] boxes)
    {
        // v2.2.0 第二轮：横排 WrapPanel（宽度不够自动换行）——此前纵排单列，宽屏下开关
        // 挤成左侧窄柱、右侧大片空白。注释曾警告 UniformGrid 在纵向 StackPanel（高度无限
        // 测量）里会算坏布局——WrapPanel 只按可用宽度换行、高度自然增长，无该问题。
        var p = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var b in boxes)
        {
            b.Margin = new Thickness(0, 4, 28, 4);
            p.Children.Add(b);
        }
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
    /// applyStartType 非空时「启动类型」行升级为下拉+应用按钮（v2.1 面板解锁）；
    /// 传 null 保持纯展示（测试/降级路径）。
    /// </summary>
    public static List<System.Windows.UIElement> SvcStatusStripChildren(
        WinShare.Core.Contracts.ServiceStatus? status, string protocolLabel,
        Func<string, Task>? applyStartType = null)
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
            list.Add(BuildStartTypeRow(status.startType, applyStartType));
            list.Add(new TextBlock { Text = $"服务名：{status.name}" }.Live(TextBlock.ForegroundProperty, "BrushFog"));
        }
        return list;
    }

    /// <summary>启动类型行：applyStartType 缺席 → 纯文本；否则下拉（自动/手动/禁用）+ 应用按钮。</summary>
    private static System.Windows.UIElement BuildStartTypeRow(string startType, Func<string, Task>? applyStartType)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = $"启动类型：{startType}",
            VerticalAlignment = VerticalAlignment.Center,
        }.Live(TextBlock.ForegroundProperty, "BrushFog"));
        if (applyStartType is null) return row;

        ComboBox cb = new()
        {
            Style = (Style?)System.Windows.Application.Current?.TryFindResource("ComboBoxSoft"),
            Width = 90,
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "选择后点击「应用」写入服务启动类型（对运行中的服务下次启动生效）",
        };
        foreach (var (label, token) in new[] { ("自动", "Automatic"), ("手动", "Manual"), ("禁用", "Disabled") })
        {
            cb.Items.Add(new ComboBoxItem { Content = label, Tag = token });
        }
        foreach (ComboBoxItem item in cb.Items)
        {
            if (string.Equals((string?)item.Tag, startType, StringComparison.OrdinalIgnoreCase))
            {
                cb.SelectedItem = item;
                break;
            }
        }
        row.Children.Add(cb);

        var apply = new Button
        {
            Content = "应用",
            Style = (Style?)System.Windows.Application.Current?.TryFindResource("DashButton"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        apply.Click += async (_, _) =>
        {
            if (cb.SelectedItem is not ComboBoxItem sel) return;
            string token = (string)sel.Tag;
            if (token == "Disabled")
            {
                var ok = System.Windows.MessageBox.Show(
                    "禁用自启动后服务将不再随系统启动（现存连接不受影响），继续？",
                    "启动类型确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ok != System.Windows.MessageBoxResult.Yes) return;
            }
            await applyStartType(token);
        };
        row.Children.Add(apply);
        return row;
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
