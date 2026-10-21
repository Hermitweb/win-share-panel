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

/// <summary>
/// 磁盘水位等级 → 展示文案（对齐 TS DISK_LEVEL_META：
/// ok=正常 / warn=偏低 / danger=紧张 / unknown=未知）。
/// </summary>
public sealed class DiskLevelTextConverter : IValueConverter
{
    public static readonly DiskLevelTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? s switch
        {
            "ok" => "正常",
            "warn" => "偏低",
            "danger" => "紧张",
            _ => "未知",
        } : "未知";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>磁盘水位等级 → 进度条前景色（对齐 TS DISK_LEVEL_META.color）。</summary>
public sealed class DiskLevelBrushConverter : IValueConverter
{
    public static readonly DiskLevelBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value is string s ? s switch
        {
            "ok" => "#52C41A",
            "warn" => "#FAAD14",
            "danger" => "#FF4D4F",
            _ => "#808080",
        } : "#808080";
        return (System.Windows.Media.Brush)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
