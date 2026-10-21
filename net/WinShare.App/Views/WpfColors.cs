using System.Windows.Media;

namespace WinShare.App.Views;

/// <summary>hex 色值 → WPF 画刷（图表层专用；仪表板纯逻辑层不依赖 WPF 类型）。</summary>
public static class WpfColors
{
    public static Brush ToBrush(string hex, Brush fallback = default)
    {
        if (hex.StartsWith('#') && hex.Length == 7)
        {
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color c)
                {
                    return new SolidColorBrush(c);
                }
            }
            catch { }
        }
        return fallback ?? Brushes.Gray;
    }
}
