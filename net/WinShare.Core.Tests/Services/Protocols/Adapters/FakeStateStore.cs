// 协议适配器测试替身：假 IStateStore（addJournal 全量记录，不落盘）+ 假三大服务（只记录调用）。
// 对应 TS 适配器测试里 vi.mock('../lib/stateStore') 与 vi.mock('../../nfs' | '../../ftp' | '../../webdav')。
using WinShare.Core.Contracts;
using WinShare.Core.Lib;

namespace WinShare.Core.Tests.Services.Protocols.Adapters;

/// <summary>假 <see cref="IStateStore"/>：只关心 addJournal 的入参（操作日志侧无落盘副作用）。</summary>
internal sealed class FakeStateStore : IStateStore
{
    public List<JournalEntryDraft> Journal { get; } = new();

    public JournalEntry AddJournal(JournalEntryDraft entry)
    {
        Journal.Add(entry);
        return new JournalEntry
        {
            id = $"j{Journal.Count}",
            ts = 0,
            action = entry.action,
            protocol = entry.protocol,
            name = entry.name,
            detail = entry.detail,
            snapshot = entry.snapshot,
            undoable = entry.undoable,
        };
    }

    public AppState LoadState() => new();
    public AppState SaveState(AppStatePatch patch) => new();
    public void PushHistory(HistoryPoint point) { }
    public void RemoveJournal(string id) { }
    public void ResetStateCacheForTesting() { }
}
