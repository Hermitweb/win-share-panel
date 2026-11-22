using System.Windows.Controls;

namespace WinShare.App.Views.Controls;

/// <summary>
/// Hero 矢量插画（v2.3）：渐变球体 + 公转轨道环，参考稿主视觉的纯 XAML 近似。
/// 逻辑为零——仅 InitializeComponent；动画触发器内联在 XAML。
/// </summary>
public partial class HeroOrbit : UserControl
{
    public HeroOrbit()
    {
        InitializeComponent();
    }
}
