using System.Globalization;
using System.Windows.Data;

namespace WinShare.App.Views;

/// <summary>告警等级 → 展示文案（对齐 TS Tag：danger=高风险 / warn=注意）。</summary>
public sealed class StateLevelConverter : IValueConverter
{
    public static readonly StateLevelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s == "danger" ? "高风险" : "注意";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>告警等级 → Tag 背景（对齐 antd Tag red/orange 底色）。</summary>
public sealed class LevelBrushConverter : IValueConverter
{
    public static readonly LevelBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value is string s && s == "danger" ? "#FFF1F0" : "#FFF7E6";
        return (System.Windows.Media.Brush)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
