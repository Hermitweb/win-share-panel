// Lib 自定接口（不在 Contracts 冻结面内）：应用级持久状态的注入点。
// TS stateStore.ts 是模块级函数（transfer.ts 直接 import）；C# 侧实例化 + 接口化，
// 服务域（transfer 等）经 IStateStore 注入，测试可替身（同 TS vi.mock('../lib/stateStore') 手法）。
using WinShare.Core.Contracts;

namespace WinShare.Core.Lib;

/// <summary>
/// 应用级持久状态存取（TS: electron/lib/stateStore.ts 导出：
/// loadState / saveState / pushHistory / addJournal / removeJournal / __resetStateCache）。
/// </summary>
public interface IStateStore
{
    /// <summary>TS: loadState()——读 appstate.json 并逐字段收敛；带进程内缓存；损坏回落默认不抛错。</summary>
    AppState LoadState();

    /// <summary>
    /// TS: saveState(patch)——prev + patch 浅合并（alertRules 逐键浅合并）→ normalize 收敛 → 原子落盘（tmp+rename）。
    /// C# 契约语义：patch 各可空字段非 null 才替换；alertRules 非 null 时整体替换
    /// （其中 int? 的 idleAlertMinutes 为 null 表示"未改"）。
    /// </summary>
    AppState SaveState(AppStatePatch patch);

    /// <summary>TS: pushHistory(point)——按序追加采样点（容量 288，保留最新）。</summary>
    void PushHistory(HistoryPoint point);

    /// <summary>TS: addJournal(entry)——生成 id/ts 后追加（容量 200，保留最新）。</summary>
    JournalEntry AddJournal(JournalEntryDraft entry);

    /// <summary>TS: removeJournal(id)——撤销成功后消费该条记录（删除不存在的 id 是安全 no-op）。</summary>
    void RemoveJournal(string id);

    /// <summary>TS: __resetStateCache()——复位进程内缓存与 journal 序号（仅测试用）。</summary>
    void ResetStateCacheForTesting();
}
