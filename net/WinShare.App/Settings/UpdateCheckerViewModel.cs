using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 检查更新卡（只检查与提示，不自动下载安装）。
/// [Source: src/components/UpdateChecker.tsx] 失败路径为重点：unavailable 必须显示
/// 「检查更新失败 + 原因 + 可开下载页手动查看」，绝不显示成「已是最新」。
/// </summary>
public sealed class UpdateCheckerViewModel : SettingsCardBase
{
    readonly IUpdateService? _svc;
    readonly string _currentVersion;
    readonly Action<string>? _openExternal;

    public UpdateCheckResult? Result { get; private set; }
    public string? ErrorText { get; private set; }

    /// <summary>最近一次结果的可展示文案（三态：失败 / 有新版本 / 已是最新）。</summary>
    public string? StatusText
    {
        get
        {
            if (ErrorText is not null) return null;
            if (Result is not { } r) return null;
            if (r.unavailable) return $"检查更新失败：{r.reason}（可直接打开下载页手动查看；或在能访问 GitHub 的网络/代理下重试）";
            return r.hasUpdate ? $"发现新版本 {r.latest}" : "当前已是最新版本";
        }
    }

    public string ReleasesPage => _svc?.ReleasesPage ?? "https://github.com/Hermitweb/win-share-panel/releases/latest";

    public UpdateCheckerViewModel(
        IUpdateService? service,
        string currentVersion,
        Action<string>? openExternal = null)
    {
        _svc = service;
        _currentVersion = currentVersion;
        _openExternal = openExternal;
        if (service is null)
        {
            ErrorText = "更新检查服务未注册（等待 Core 服务层实现）：检查更新不可用";
        }
        Raise();
    }

    public async Task CheckAsync()
    {
        if (_svc is null || Busy) return;
        Busy = true;
        ErrorText = null;
        try
        {
            Result = await _svc.CheckForUpdateAsync(_currentVersion);
            Success = null;
        }
        catch (Exception e)
        {
            // 通道级异常（服务实现内部失败）——照样落在卡片里，不只在瞬时提示里
            Result = null;
            ErrorText = e.Message;
        }
        finally
        {
            Busy = false;
        }
        Raise(nameof(Result));
    }

    /// <summary>打开下载页（对齐 TS openRelease：优先结果里的 releaseUrl，回退服务发布页；失败记卡片错误）。</summary>
    public Task<string?> OpenReleaseAsync()
    {
        ErrorText = null;
        var url = Result?.releaseUrl is { Length: > 0 } u ? u : ReleasesPage;
        if (_openExternal is null)
        {
            ErrorText = $"无法打开浏览器，请手动访问：{url}";
            Raise(nameof(ErrorText));
            return Task.FromResult<string?>(ErrorText);
        }
        try
        {
            _openExternal(url);
            return Task.FromResult<string?>(null);
        }
        catch (Exception e)
        {
            ErrorText = e.Message;
            Raise(nameof(ErrorText));
            return Task.FromResult<string?>(ErrorText);
        }
    }
}
