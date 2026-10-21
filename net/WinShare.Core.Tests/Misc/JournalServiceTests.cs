// [Source: electron/services/journal.ts] —— 操作回收站：列表/清空/撤销守卫与分派。
// 测试策略：用真实 ShareService/UserService + 假 IPsInvoker（命令层替身），
// 避免手写 20+ 成员的接口桩；守卫路径在触达命令前就应短路。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Misc;
using WinShare.Core.Services.Shares;
using WinShare.Core.Services.Users;

namespace WinShare.Core.Tests.Misc;

public class JournalServiceTests
{
    /// <summary>可编排假 store：journal 可变，记录 RemoveJournal/SaveState 调用。</summary>
    private sealed class StubStore : IStateStore
    {
        public List<JournalEntry> Entries { get; } = new();
        public List<string> Removed { get; } = new();
        public int SaveCalls { get; private set; }

        public JournalEntry AddJournal(JournalEntryDraft entry) => throw new NotSupportedException();
        public AppState LoadState() => new()
        {
            journal = Entries,
            pinned = Array.Empty<string>(),
            dashboardHistory = Array.Empty<HistoryPoint>(),
        };
        public AppState SaveState(AppStatePatch patch) { SaveCalls++; return new AppState(); }
        public void PushHistory(HistoryPoint point) { }
        public void RemoveJournal(string id) => Removed.Add(id);
        public void ResetStateCacheForTesting() { }
    }

    /// <summary>命令层替身：记录命令串，按需返回 JSON 或抛错。</summary>
    private sealed class FakePs : IPsInvoker
    {
        public List<string> Json { get; } = new();
        public List<string> Void { get; } = new();
        public Func<string, JsonElement>? JsonHandler { get; set; }
        public Exception? VoidError { get; set; }

        public Task<JsonElement> RunJsonAsync(string command, int timeoutMs, int retries)
        {
            Json.Add(command);
            JsonElement result = JsonHandler is null
                ? JsonDocument.Parse("[]").RootElement.Clone()
                : JsonHandler(command);
            return Task.FromResult(result);
        }

        public Task RunVoidAsync(string command, int timeoutMs, int retries)
        {
            Void.Add(command);
            return VoidError is null ? Task.CompletedTask : Task.FromException(VoidError);
        }
    }

    private static JournalEntry Entry(JournalAction action, object? snapshot, bool undoable = true, Protocol protocol = Protocol.Smb) => new()
    {
        id = "j1",
        ts = 1,
        action = action,
        protocol = protocol,
        name = "s1",
        detail = "d",
        snapshot = snapshot,
        undoable = undoable,
    };

    private static (JournalService svc, StubStore store, FakePs ps) Build()
    {
        var store = new StubStore();
        var ps = new FakePs();
        var share = new ShareService(ps, store);
        var user = new UserService(ps, store);
        return (new JournalService(store, share, user), store, ps);
    }

    // [Source: journal.ts#listJournal] 新→旧
    [Fact]
    public void ListJournal_ReturnsNewestFirst()
    {
        (JournalService svc, StubStore store, _) = Build();
        store.Entries.Add(Entry(JournalAction.Create, null) with { id = "a" });
        store.Entries.Add(Entry(JournalAction.Create, null) with { id = "b" });
        IReadOnlyList<JournalEntry> list = svc.ListJournal();
        Assert.Equal("b", list[0].id);
        Assert.Equal("a", list[1].id);
    }

    // [Source: journal.ts#clearJournal] 返回清除条数并落盘一次
    [Fact]
    public void ClearJournal_ReturnsCount()
    {
        (JournalService svc, StubStore store, _) = Build();
        store.Entries.Add(Entry(JournalAction.Create, null));
        store.Entries.Add(Entry(JournalAction.Create, null));
        Assert.Equal(2, svc.ClearJournal());
        Assert.Equal(1, store.SaveCalls);
    }

    // [Source: journal.ts#undoJournal] 三条守卫：不存在 / 不可撤销 / 非 SMB
    [Theory]
    [InlineData("missing", true, Protocol.Smb, "操作记录不存在或已被清理")]
    [InlineData("j1", false, Protocol.Smb, "该操作不支持撤销（仅留档）")]
    [InlineData("j1", true, Protocol.Nfs, "NFS 暂不支持一键撤销，请按留档手工重建")]
    public async Task UndoJournal_Guards(string id, bool undoable, Protocol proto, string expected)
    {
        (JournalService svc, StubStore store, FakePs ps) = Build();
        store.Entries.Add(Entry(JournalAction.Delete, null, undoable, proto));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => svc.UndoJournalAsync(id));
        Assert.Contains(expected, ex.Message);
        Assert.Empty(ps.Json);   // 守卫必须在触达命令层之前短路
        Assert.Empty(ps.Void);
        Assert.Empty(store.Removed);
    }

    // [Source: journal.ts#undoJournal] create 仅留档
    [Fact]
    public async Task UndoCreate_NotUndoable()
    {
        (JournalService svc, StubStore store, _) = Build();
        store.Entries.Add(Entry(JournalAction.Create, null));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => svc.UndoJournalAsync("j1"));
        Assert.Contains("不支持撤销", ex.Message);
    }

    // [Source: journal.ts#undoJournal] delete 缺快照 → 明确报错
    [Fact]
    public async Task UndoDelete_MissingSnapshot_Throws()
    {
        (JournalService svc, StubStore store, _) = Build();
        store.Entries.Add(Entry(JournalAction.Delete, null));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => svc.UndoJournalAsync("j1"));
        Assert.Contains("缺少删除前快照", ex.Message);
    }

    // [Source: journal.ts#undoJournal] delete：共享已存在 → 如实报错（不静默跳过）
    [Fact]
    public async Task UndoDelete_ShareAlreadyExists_Throws()
    {
        (JournalService svc, StubStore store, FakePs ps) = Build();
        ShareSnapshot snap = new()
        {
            name = "s1",
            path = @"C:\x",
            permissions = new List<SharePermission>(),
        };
        store.Entries.Add(Entry(JournalAction.Delete, snap));
        // listShares 回读到一个同名共享
        ps.JsonHandler = _ => JsonDocument.Parse(
            "[{\"Name\":\"s1\",\"Path\":\"C:\\\\x\",\"Description\":\"\",\"ShareType\":0}]").RootElement.Clone();

        AppException ex = await Assert.ThrowsAsync<AppException>(() => svc.UndoJournalAsync("j1"));
        Assert.Contains("已存在，无需撤销", ex.Message);
        Assert.Empty(store.Removed); // 失败不消费记录
    }

    // [Source: journal.ts#undoJournal] permset 缺权限快照 → 明确报错
    [Fact]
    public async Task UndoPermset_MissingSnapshot_Throws()
    {
        (JournalService svc, StubStore store, _) = Build();
        store.Entries.Add(Entry(JournalAction.Permset, null));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => svc.UndoJournalAsync("j1"));
        Assert.Contains("缺少权限快照", ex.Message);
    }

    // [Source: journal.ts#undoJournal] toggle-off：disabled 通道失败且无快照 → 报「恢复失败」
    [Fact]
    public async Task UndoToggleOff_NoSnapshot_ReportsRestoreFailure()
    {
        (JournalService svc, StubStore store, FakePs ps) = Build();
        store.Entries.Add(Entry(JournalAction.ToggleOff, null));
        ps.VoidError = new Exception("disabled.json 无记录");

        AppException ex = await Assert.ThrowsAsync<AppException>(() => svc.UndoJournalAsync("j1"));
        Assert.Contains("恢复失败", ex.Message);
    }
}
