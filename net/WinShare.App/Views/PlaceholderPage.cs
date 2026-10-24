using System.Windows;
using System.Windows.Controls;

namespace WinShare.App.Views;

/// <summary>
/// 占位页（**历史脚手架，当前无路由使用**）。
///
/// T3 期间用它给未实现的模块先挂一个占位页，后续模块用
/// `NavigationRegistry.Register(route, new AppPage { factory = ... })` 同路由二次注册覆盖
/// （同路由二次注册即替换）。T3b/c/d 已于 2026-10 全部交付，六条路由现在都由各自的
/// `XxxModule.Register(reg)` 直接注册真实页面，本类**不再被任何路由引用**。
///
/// 保留它只为将来新增模块时能临时挂载（先占位、后替换）；它不是当前装配流程的一环。
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
            Text = $"占位页（route={route}）。该路由尚未接入真实模块；接入后由模块的 Register 同路由覆盖本页，见 net/WinShare.App/DOCS.md「导航注册 API」。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8C8C8C")!),
        });
    }
}
