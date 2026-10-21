using WinShare.Core.Contracts;

namespace WinShare.App.Events;

/// <summary>
/// 应用事件定义（事件总线上的 payload 类型）。
/// 命名与 Electron 版 IPC 事件对齐：theme-changed / refresh-requested / alert-raised /
/// state-saved / navigation-requested / trend-sampled。
/// </summary>

/// <summary>主题切换（设置页改主题后发布；界面层随之换资源字典）。</summary>
public sealed record ThemeChanged(ThemeMode theme);

/// <summary>请求全局刷新（侧栏命令、托盘菜单、F5 快捷键）。</summary>
public sealed record RefreshRequested();

/// <summary>告警规则命中（状态层评估器发布；托盘气泡 / 仪表板「需要关注」消费）。</summary>
public sealed record AlertRaised
{
    public string kind { get; init; } = "";        // smb1 | guest | weak-password | disk:{drive}
    public string level { get; init; } = "warn";   // danger | warn
    public string text { get; init; } = "";
}

/// <summary>状态持久化完成（主题/规则保存后发布）。</summary>
public sealed record StateSaved();

/// <summary>跨页导航请求（「需要关注」卡动作按钮；attention 透传给目标页做定位高亮）。</summary>
public sealed record NavigationRequested
{
    public string route { get; init; } = "/";
    public string? attention { get; init; }
}

/// <summary>趋势采样成功写入（采样器发布；仪表板可即时刷新曲线）。</summary>
public sealed record TrendSampled(HistoryPoint point);
