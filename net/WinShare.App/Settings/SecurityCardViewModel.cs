using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 账号安全体检卡（空口令/永不过期/超 180 天未改；只报服务真探测过的项）。
/// [Source: src/components/AppSettingsCards.tsx SecurityCard L545-640]
/// 级别映射逐字对齐 TS ISSUE_LEVEL：fail=严重(red) / warn=提醒(orange)。
/// </summary>
public sealed class SecurityCardViewModel : SettingsCardBase
{
    readonly ISecurityService? _svc;

    public SecurityCardViewModel(ISecurityService? service)
    {
        _svc = service;
        if (service is null)
        {
            Error = "账号体检服务未注册（等待 Core 服务层实现）：无法枚举本地账号";
        }
        Raise();
    }

    public SecurityReport? Report { get; private set; }
    public string? ReportError { get; private set; }

    /// <summary>体检中（对齐 TS「正在枚举本地账号（Get-LocalUser）…」占位态判定：error&&!data）。</summary>
    public bool Running { get; private set; }

    /// <summary>成功态文案（对齐 TS：「未发现空口令 / 密码永不过期 / 超 180 天未修改的账号（共检查 N 个启用账号）」）。</summary>
    public string? NoIssuesText => Report is { issues: var list } && list.Count == 0
        ? $"未发现空口令 / 密码永不过期 / 超 180 天未修改的账号（共检查 {Report.Checked} 个启用账号）"
        : null;

    public bool HasIssues => Report is { } r && r.issues.Count > 0;

    public async Task RunAsync()
    {
        if (_svc is null) return;
        if (Running) return;
        Running = true;
        ReportError = null;
        Raise(nameof(Running));
        try
        {
            Report = await _svc.GetSecurityReportAsync();
        }
        catch (Exception e)
        {
            ReportError = $"体检失败：{e.Message}";
        }
        finally
        {
            Running = false;
            Raise(nameof(Running));
        }
    }

    /// <summary>级别 → (标签, antd 色名) [对齐 TS ISSUE_LEVEL]。</summary>
    public static (string Label, string Color) LevelInfo(SecurityLevel level) => level switch
    {
        SecurityLevel.Fail => ("严重", "red"),
        SecurityLevel.Warn => ("提醒", "orange"),
        _ => ("提醒", "orange"),
    };
}
