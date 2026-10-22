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

    public static Border Card(System.Windows.UIElement content, double padding = 16)
    {
        return new Border
        {
            Background = Token("BrushCard", "#FFFFFF"),
            BorderBrush = Token("BrushBorder", "#E4E9F0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(padding),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content,
        };
    }

    public static TextBlock CardTitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("BrushText", "#1F2733"),
            Margin = new Thickness(0, 0, 0, 10),
        };
    }

    public static TextBlock FogText(string text, double top = 0, double bottom = 0)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Token("BrushFog", "#731F2733"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, bottom),
        };
    }

    /// <summary>Success/Error 提示面（订阅 VM 结果面；对齐 antd message + 卡片内 Alert 语义）。</summary>
    public static Border MessageStrip(SettingsCardBase vm)
    {
        var success = new TextBlock
        {
            FontSize = 12,
            Foreground = Token("BrushSuccess", "#52C41A"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var error = new TextBlock
        {
            FontSize = 12,
            Foreground = Token("BrushDanger", "#FF4D4F"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
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
        p.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("BrushText", "#1F2733"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var c in extras)
        {
            c.HorizontalAlignment = HorizontalAlignment.Left;
            p.Children.Add(c);
        }
        return p;
    }

    public static Button Button(string text, RoutedEventHandler onClick, bool primary = false, string? stopHint = null)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(6, 0, 0, 0),
            MinHeight = 28,
            ToolTip = stopHint,
        };
        if (onClick is not null) b.Click += onClick; // 禁用占位按钮允许 null 处理器
        if (primary)
        {
            b.Background = Token("BrushPrimary", "#1677FF");
            b.Foreground = Brushes.White;
            b.BorderThickness = new Thickness(0);
        }
        return b;
    }

    /// <summary>开关字段（CheckBox + 标签；DataContext 取传入 VM 实例；纵向布局下每个开关独立一行）。</summary>
    public static CheckBox Toggle(string label, string vmPath, object vm)
    {
        var cb = new CheckBox
        {
            Content = label,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = Token("BrushText", "#1F2733"),
        };
        // 不显式设 DataContext：继承宿主面板（_form）的 DataContext（=卡 VM），
        // 表单回填时重置宿主 DataContext 即全量重拉
        _ = vm;
        cb.SetBinding(CheckBox.IsCheckedProperty, new Binding(vmPath) { Mode = BindingMode.TwoWay });
        return cb;
    }

    /// <summary>开关组（纵向单列：每个 CheckBox 独立一行；重渲染时只能 Clear+Add 回同一面板）。</summary>
    public static StackPanel ToggleGroup(params CheckBox[] boxes)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var b in boxes) p.Children.Add(b);
        return p;
    }

    /// <summary>数字字段行（标签 + TextBox，失焦解析回 VM int/long；解析失败保留 VM 原值）。</summary>
    public static StackPanel NumberField(string label, string vmPath, object vm, bool isLong = false)
    {
        var tb = new TextBox { Width = 100, VerticalAlignment = VerticalAlignment.Center };
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
        cb.ItemsSource = items;
        cb.SetBinding(ComboBox.SelectedItemProperty, new Binding(vmPath) { Mode = BindingMode.TwoWay });
        return FieldRow(label, cb);
    }

    /// <summary>字段行（标签在上、控件在下；纵向单列，每个字段独占一块。
    /// 子元素顺序保持 [0]=标签、[1]=控件，调用方可按索引取控件（如告警卡 idleMap 提交））。</summary>
    public static StackPanel FieldRow(string label, Control control)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Token("BrushText", "#1F2733"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2),
        });
        control.HorizontalAlignment = HorizontalAlignment.Left;
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
            new TextBlock
            {
                Text = "服务状态",
                Foreground = Token("BrushFog", "#731F2733"),
                Margin = new Thickness(0, 0, 0, 2),
            }
        };
        if (status is null)
        {
            list.Add(new TextBlock { Text = "未读取", Foreground = Token("BrushFog", "#731F2733") });
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
            list.Add(new TextBlock { Text = $"启动类型：{status.startType}", Foreground = Token("BrushFog", "#731F2733"), Margin = new Thickness(0, 0, 0, 2) });
            list.Add(new TextBlock { Text = $"服务名：{status.name}", Foreground = Token("BrushFog", "#731F2733") });
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

    /// <summary>antd Tag 色名 → 画刷（green/orange/red/volcano/blue/gray，暗色降饱和由视图全局 token 兜底）。</summary>
    public static Brush TagColor(string antdColor) => Token("BrushText", antdColor switch
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
            Background = Token("BrushBg", "#F4FAFD"),
            Foreground = Token("BrushText", "#1F2733"),
            BorderThickness = new Thickness(1),
            BorderBrush = Token("BrushBorder", "#E4E9F0"),
        };
        return tb;
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
