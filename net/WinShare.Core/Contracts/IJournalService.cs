namespace WinShare.Core.Contracts;

/// <summary>
/// 操作日志/撤销（对应 electron/services/journal.ts 导出：
/// listJournal / clearJournal / undoJournal）。
/// </summary>
public interface IJournalService
{
    /// <summary>TS: listJournal()（同步，从 stateStore 读）。</summary>
    IReadOnlyList<JournalEntry> ListJournal();

    /// <summary>TS: clearJournal()（同步），返回清除条数。</summary>
    int ClearJournal();

    /// <summary>TS: undoJournal(id)，按 adapter 路由撤销，返回说明文本。</summary>
    Task<string> UndoJournalAsync(string id);
}
