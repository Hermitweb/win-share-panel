namespace WinShare.Core.Contracts;

/// <summary>
/// 应用内检查更新（对应 electron/services/update.ts 导出：
/// compareVersions / checkForUpdate；常量 UPDATE_REPO / RELEASES_PAGE；
/// 本地 DEFAULT_TIMEOUT_MS = 8000、API_LATEST 指向 GitHub releases/latest API）。
/// TS checkForUpdate 第二参 fetchImpl 为可注入的 fetch（测试用），C# 侧实现类可自定注入点。
/// </summary>
public interface IUpdateService
{
    /// <summary>更新源仓库（TS: UPDATE_REPO）。</summary>
    string UpdateRepo { get; }

    /// <summary>发布页（TS: RELEASES_PAGE）。</summary>
    string ReleasesPage { get; }

    /// <summary>TS: compareVersions(a, b)（同步），semver 风格比较，负/0/正。</summary>
    int CompareVersions(string a, string b);

    /// <summary>
    /// TS: checkForUpdate(currentVersion, fetchImpl?, timeoutMs=8000)。
    /// 不抛错：失败收敛为 unavailable + reason。
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion, int timeoutMs = 8000);
}
