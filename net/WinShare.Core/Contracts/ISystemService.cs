namespace WinShare.Core.Contracts;

/// <summary>
/// 系统信息与持久状态（对应 electron/services/system.ts 导出：
/// getCurrentUser / isAdmin / relaunchAsAdmin / getDashboardStats / getAuditLog /
/// healthCheck / getOsInfo；
/// 以及 electron/lib/stateStore.ts 导出：loadState / saveState / addJournal /
/// removeJournal / pushHistory；
/// electron/lib/audit.ts readAuditLog 与 system.ts getAuditLog 同源。
/// TS 测试钩子 __resetStateCache 不属于契约面。
/// </summary>
public interface ISystemService
{
    /// <summary>TS: getCurrentUser()。</summary>
    Task<UserInfo> GetCurrentUserAsync();

    Task<bool> IsAdminAsync();

    /// <summary>TS: relaunchAsAdmin()（Electron 语义；WPF 侧对应请求提升 UAC 重启，实现侧决定）。</summary>
    Task RelaunchAsAdminAsync();

    Task<DashboardStats> GetDashboardStatsAsync();

    /// <summary>TS: getAuditLog()（审计日志文本）。</summary>
    Task<string> GetAuditLogAsync();

    Task<HealthCheckResult> HealthCheckAsync();

    /// <summary>TS: getOsInfo(opts: { refresh?: boolean })。</summary>
    Task<OsInfo> GetOsInfoAsync(OsInfoOptions? opts = null);

    /// <summary>TS: stateStore.loadState()（同步）。</summary>
    AppState LoadState();

    /// <summary>TS: stateStore.saveState(patch: AppStatePatch)（同步），返回合并后全量。</summary>
    AppState SaveState(AppStatePatch patch);

    /// <summary>TS: stateStore.addJournal(entry: Omit&lt;JournalEntry,'id'|'ts'&gt;)，返回补全后的条目。</summary>
    JournalEntry AddJournal(JournalEntryDraft entry);

    /// <summary>TS: stateStore.removeJournal(id)（同步）。</summary>
    void RemoveJournal(string id);

    /// <summary>TS: stateStore.pushHistory(point)（同步）。</summary>
    void PushHistory(HistoryPoint point);
}
