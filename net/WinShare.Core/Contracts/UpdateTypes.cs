namespace WinShare.Core.Contracts;

/// <summary>
/// 「检查更新」结果（TS: electron/services/update.ts UpdateCheckResult。
/// 更新源不可达时 unavailable=true、latest 为空串——UI 走「检查失败」分支，不能当成「已是最新」）。
/// </summary>
public record UpdateCheckResult
{
    public string current { get; init; }
    public string latest { get; init; }
    public bool hasUpdate { get; init; }
    public string releaseUrl { get; init; }
    public string? publishedAt { get; init; }
    public bool unavailable { get; init; }
    public string? reason { get; init; }
}
