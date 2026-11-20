namespace WinShare.App.Shell;

/// <summary>
/// 全局断点常量（v2.2.0 第五批：单一来源）。
/// 此前侧栏折叠阈值（1100）与表格隐藏列阈值（1280 / 1024）各写各的，
/// 1024–1099 宽度会同时命中两套规则 → 列被裁、名称列消失、协议标签压扁。
/// 现在所有断点判断都从这里取，区间互斥且连续。
/// </summary>
public static class Breakpoints
{
    /// <summary>≥1440：桌面宽——全部列显示，侧栏展开文字。</summary>
    public const double L = 1440;

    /// <summary>&lt;1440：隐藏描述类长列。</summary>
    public const double M = 1440;

    /// <summary>&lt;1100：侧栏图标化 + 隐藏更多派生列。</summary>
    public const double S = 1100;

    /// <summary>&lt;900：窄屏（平板竖屏）——仅保留主识别列。</summary>
    public const double XS = 900;

    /// <summary>侧栏是否收为图标态。</summary>
    public static bool IsCompactNav(double availableWidth) => availableWidth < S;

    /// <summary>是否隐藏描述/作者等长文本列。</summary>
    public static bool HideLongColumns(double availableWidth) => availableWidth < M;

    /// <summary>是否隐藏加密/连接数等派生列（只留名称/协议/状态/操作）。</summary>
    public static bool HideDerivedColumns(double availableWidth) => availableWidth < S;
}
