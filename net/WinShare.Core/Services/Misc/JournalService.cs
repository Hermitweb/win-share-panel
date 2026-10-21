// 移植自 electron/services/journal.ts（操作回收站：最近 N 条操作 + 一键撤销）。
// 撤销只对有快照且系统层面可还原的操作开放：
//   delete(smb)     → 按快照重建共享（含权限/高级选项）
//   toggle-off(smb) → 走既有 disabled.json 恢复通道（失败则快照兜底）
//   permset(smb)    → 回写改前权限快照
// create 与非 SMB 删除仅留档不撤销。撤销成功即消费该记录，避免同一快照被重复回放。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;

namespace WinShare.Core.Services.Misc;

/// <summary>操作日志/撤销（TS: journal.ts）。</summary>
public sealed class JournalService : IJournalService
{
    private readonly IStateStore _store;
    private readonly IShareService _share;
    private readonly IUserService _user;

    public JournalService(IStateStore store, IShareService share, IUserService user)
    {
        _store = store;
        _share = share;
        _user = user;
    }

    /// <summary>TS: listJournal()——新→旧（UI 直接渲染）。</summary>
    public IReadOnlyList<JournalEntry> ListJournal()
    {
        List<JournalEntry> journal = _store.LoadState().journal?.ToList() ?? new List<JournalEntry>();
        journal.Reverse();
        return journal;
    }

    /// <summary>TS: clearJournal()——返回清除条数。</summary>
    public int ClearJournal()
    {
        int n = _store.LoadState().journal?.Count ?? 0;
        _store.SaveState(new AppStatePatch { journal = Array.Empty<JournalEntry>() });
        return n;
    }

    /// <summary>TS: undoJournal(id)——按 action 分派撤销，返回人类可读说明。</summary>
    public async Task<string> UndoJournalAsync(string id)
    {
        JournalEntry? entry = _store.LoadState().journal?.FirstOrDefault(j => j.id == id);
        if (entry is null) throw Errors.InvalidParam("操作记录不存在或已被清理");
        if (!entry.undoable) throw Errors.InvalidParam("该操作不支持撤销（仅留档）");
        if (entry.protocol != Protocol.Smb)
        {
            throw Errors.InvalidParam($"{entry.protocol.ToString().ToUpperInvariant()} 暂不支持一键撤销，请按留档手工重建");
        }

        string message;
        switch (entry.action)
        {
            case JournalAction.Delete:
            {
                ShareSnapshot snapshot = RequireSnapshot(entry, "缺少删除前快照，无法撤销");
                // 共享若已被重新创建（人工或后续操作），恢复会撞名——如实报错而不是静默跳过
                bool exists = (await _share.ListSharesAsync()).Any(s => s.name == entry.name);
                if (exists) throw Errors.CommandFailed($"共享\"{entry.name}\"已存在，无需撤销");
                await _share.RestoreShareAsync(snapshot);
                message = $"已恢复共享\"{entry.name}\"（路径 {snapshot.path}，{snapshot.permissions?.Count ?? 0} 条授权）";
                break;
            }
            case JournalAction.ToggleOff:
            {
                // 优先走 disabled.json 通道（与用户在列表里点"启用"完全同路径）
                try
                {
                    await _share.ToggleShareAsync(entry.name, true);
                    message = $"已重新启用共享\"{entry.name}\"";
                }
                catch (Exception e)
                {
                    string first = e.Message;
                    ShareSnapshot? snapshot = TrySnapshot(entry);
                    if (snapshot is null) throw Errors.CommandFailed($"恢复失败：{first}");
                    // disabled.json 记录已被消费（例如用户手工启用过）→ 用 journal 快照兜底重建
                    bool exists = (await _share.ListSharesAsync()).Any(s => s.name == entry.name);
                    if (exists) throw Errors.CommandFailed($"共享\"{entry.name}\"已存在，无需撤销");
                    await _share.RestoreShareAsync(snapshot);
                    message = $"已按快照恢复共享\"{entry.name}\"（禁用记录已被消费，改用快照重建）";
                }
                break;
            }
            case JournalAction.Permset:
            {
                List<SharePermission> perms = RequirePermissions(entry);
                await _user.SetSharePermissionsAsync(entry.name, perms);
                message = $"已回滚\"{entry.name}\"的共享权限到改前状态（{perms.Count} 条）";
                break;
            }
            case JournalAction.Create:
            default:
                throw Errors.InvalidParam("该操作不支持撤销（仅留档）");
        }

        _store.RemoveJournal(id);
        Logger.Info("journal", $"[undoJournal] 撤销成功: {entry.action} {entry.protocol} {entry.name}");
        return message;
    }

    // TS: isShareSnapshot(s) —— !!s && !Array.isArray(s) && typeof s.path === 'string'
    private static ShareSnapshot? TrySnapshot(JournalEntry entry)
    {
        object? raw = entry.snapshot;
        if (raw is null) return null;
        if (raw is ShareSnapshot direct) return direct;
        if (raw is JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty("path", out JsonElement p) || p.ValueKind != JsonValueKind.String) return null;
            try
            {
                return el.Deserialize<ShareSnapshot>(JsonCompat.Read);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private static ShareSnapshot RequireSnapshot(JournalEntry entry, string message) =>
        TrySnapshot(entry) ?? throw Errors.InvalidParam(message);

    // TS: Array.isArray(entry.snapshot) ? entry.snapshot as SharePermission[]
    private static List<SharePermission> RequirePermissions(JournalEntry entry)
    {
        object? raw = entry.snapshot;
        if (raw is IReadOnlyList<SharePermission> list) return list.ToList();
        if (raw is JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Array) throw Errors.InvalidParam("缺少权限快照，无法撤销");
            try
            {
                List<SharePermission>? parsed = el.Deserialize<List<SharePermission>>(JsonCompat.Read);
                return parsed ?? throw Errors.InvalidParam("缺少权限快照，无法撤销");
            }
            catch (JsonException)
            {
                throw Errors.InvalidParam("缺少权限快照，无法撤销");
            }
        }
        throw Errors.InvalidParam("缺少权限快照，无法撤销");
    }
}
