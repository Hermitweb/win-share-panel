namespace WinShare.Core.Contracts;

/// <summary>
/// 「检查更新」结果（TS: electron/services/update.ts UpdateCheckResult。
/// 更新源不可达时 unavailable=true、latest 为空串——UI 走「检查失败」分支，不能当成「已是最新」）。
/// v2.3 新增 currentAheadOfRelease：当前版本高于已发布的最新版本（本地为未发布的开发版），
/// UI 必须如实提示，不能报「已是最新」——开发进度领先于发布是常态。
/// </summary>
public record UpdateCheckResult
{
    public string current { get; init; }
    public string latest { get; init; }
    public bool hasUpdate { get; init; }
    /// <summary>当前版本 &gt; 已发布最新版本（未发布开发版；v2.3）。</summary>
    public bool currentAheadOfRelease { get; init; }
    public string releaseUrl { get; init; }
    public string? publishedAt { get; init; }
    public bool unavailable { get; init; }
    public string? reason { get; init; }
}
