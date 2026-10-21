using System.Windows;
using System.Windows.Controls;

namespace WinShare.App.Views;

/// <summary>
/// 占位页工厂（非本任务模块先挂占位，标注 TODO 给后续 T3b/c/d 填）：
/// T3b/c/d 用 NavigationRegistry.Register(route, new AppPage { factory = ... }) 直接替换，
/// 占位页不注册第二次即自动失效（见 DOCS.md 占位页替换方式）。
/// </summary>
public sealed class PlaceholderPage : UserControl
{
    public PlaceholderPage(string route, string title)
    {
        Content = new StackPanel { Margin = new Thickness(24) };
        ((StackPanel)Content).Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        });
        ((StackPanel)Content).Children.Add(new TextBlock
        {
            // TODO(T3b/c/d)：替换此占位页为真实模块
            Text = $"占位页（route={route}）。本模块将由后续任务实现，见 net/WinShare.App/DOCS.md「导航注册 API」与「占位页替换方式」。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8C8C8C")!),
        });
    }
}
