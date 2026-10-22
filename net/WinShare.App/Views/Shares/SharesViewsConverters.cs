using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WinShare.App.Shares;

namespace WinShare.App.Views.Shares;

/// <summary>访问级别英文取值 → 中文标签（TS ACCESS_LABEL；权限抽屉表格列用）。</summary>
public sealed class AccessLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value?.ToString() switch
        {
            "Full" => "完全控制",
            "Change" => "更改",
            "Read" => "只读",
            "Deny" => "拒绝",
            _ => value?.ToString() ?? "-",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
/// <summary>字符串非空 → Visible（协议专有列空单元格统一落 "-"，空串不渲染 Tag）。</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        !string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>置顶星标（TS StarFilled/StarOutlined：置顶=实心★，否则空心☆）。</summary>
public sealed class PinStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? "★" : "☆";

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 视图层构建辅助（WPF 依赖面，不写单测；纯逻辑在 Shares 模块）：
/// 协议未安装细分状态徽标（TS ProtocolCapabilityBanner renderStatusBadges 的样式等价面）。
/// </summary>
public static class SharesViewsConverters
{
    /// <summary>徽标 (text, kind) → 彩色小 Tag（kind=green|orange|red|volcano，对齐 antd Tag 色板）。</summary>
    public static void BuildBadges(Panel panel, IEnumerable<(string text, string kind)> badges)
    {
        foreach (var (text, kind) in badges)
        {
            var color = kind switch
            {
                "green" => Color.FromRgb(0x52, 0xC4, 0x1A),
                "orange" => Color.FromRgb(0xFA, 0x8C, 0x16),
                "red" => Color.FromRgb(0xFF, 0x4D, 0x4F),
                "volcano" => Color.FromRgb(0xFA, 0x54, 0x1C),
                _ => Color.FromRgb(0x8C, 0x8C, 0x8C),
            };
            var border = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 1, 8, 1),
                // 1 列纵向流：徽标自上而下堆叠，留底部间距
                Margin = new Thickness(0, 0, 0, 6),
                Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)),
            };
            border.Child = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(color) };
            panel.Children.Add(border);
        }
    }
}
