// 移植自 electron/lib/stateStore.test.ts（TS: stateStore.ts → C#: StateStore）。
// TS 以 vi.stubEnv('APPDATA', dir) 钉目录 + 真实落盘；C# 以 StateStore(dataDirOverride) 注入同一布局
// （{root}/WinSharePanel/state/appstate.json），测试互不影响。
// 语义适配标注：
//  - 契约 DTO 有类型边界：'非字符串 pinned 元素' 等脏值只能经文件层（JSON）注入，经 patch 注入的场景由超限收敛等价覆盖；
//  - alertRules patch 的 smb1Alert/weakPasswordAlert/diskLowGb 非可空 → 修改单个键时须以 prev 值构造完整对象（idleAlertMinutes 可 null）。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;

namespace WinShare.Core.Tests.Misc;

public class StateStoreTests
{
    // 自由 schema 播种辅助（键顺序 = 声明顺序，与 TS seedState 的 JSON 等价）
    private sealed class SeedDoc
    {
        public string? theme { get; init; }
        public object? advancedMode { get; init; }
        public object? pinned { get; init; }
        public object? alertRules { get; init; }
        public object? autoStart { get; init; }
        public object? dashboardHistory { get; init; }
        public object? journal { get; init; }
        public object? futureField { get; init; }
    }

    private readonly string _dir;
    private readonly StateStore _store;

    public StateStoreTests()
    {
        _dir = TestTmp.New("wsstate");
        _store = new StateStore(_dir);
    }

    private string StateFile => Path.Combine(_dir, "WinSharePanel", "state", "appstate.json");

    private void Seed(string raw)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, raw);
    }

    // 默认序列化保留 null 键（与 TS JSON.stringify 等价：seed 的 null 字段 = "文件里不存在该值"）
    private static string SeedJson(object seed) => JsonSerializer.Serialize(seed);

    private static object SeedState(
        string? theme = null,
        object? advancedMode = null,
        object? pinned = null,
        object? alertRules = null,
        object? autoStart = null,
        object? dashboardHistory = null,
        object? journal = null,
        object? futureField = null) =>
        new SeedDoc
        {
            theme = theme,
            advancedMode = advancedMode,
            pinned = pinned,
            alertRules = alertRules,
            autoStart = autoStart,
            dashboardHistory = dashboardHistory,
            journal = journal,
            futureField = futureField,
        };

    private JsonElement ReadPersisted()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
        return doc.RootElement.Clone();
    }

    private static void AssertDefaults(AppState s)
    {
        Assert.Equal(ThemeMode.Light, s.theme);
        Assert.True(s.advancedMode);
        Assert.Empty(s.pinned);
        Assert.Null(s.alertRules.idleAlertMinutes);
        Assert.True(s.alertRules.smb1Alert);
        Assert.True(s.alertRules.weakPasswordAlert);
        Assert.Equal(20, s.alertRules.diskLowGb);
        Assert.False(s.autoStart);
        Assert.Empty(s.dashboardHistory);
        Assert.Empty(s.journal);
    }

    // ===== loadState 旧/脏文件安全升级 =====

    // [Source: stateStore.test.ts#loadState 旧/脏文件安全升级 > 旧版文件缺新字段 → 逐字段补默认值]
    [Fact]
    public void Load_OldFileMissingNewFields_FillsDefaults()
    {
        Seed(SeedJson(SeedState(theme: "dark")));
        AppState s = _store.LoadState();
        Assert.Equal(ThemeMode.Dark, s.theme);
        Assert.True(s.advancedMode);
        Assert.Empty(s.pinned);
        Assert.Null(s.alertRules.idleAlertMinutes);
        Assert.True(s.alertRules.smb1Alert);
        Assert.True(s.alertRules.weakPasswordAlert);
        Assert.Equal(20, s.alertRules.diskLowGb);
        Assert.False(s.autoStart);
        Assert.Empty(s.dashboardHistory);
        Assert.Empty(s.journal);
    }

    // [Source: stateStore.test.ts#loadState 旧/脏文件安全升级 > 多余未知字段不穿透（收敛回已知 schema）]
    // 说明：C# 契约 DTO 无未知字段承载位，等价断言为"全部字段收敛回默认"。
    [Fact]
    public void Load_UnknownFieldsIgnored()
    {
        Seed(SeedJson(SeedState(theme: "light", futureField: new { x = 1 })));
        AssertDefaults(_store.LoadState());
    }

    // [Source: stateStore.test.ts#loadState 旧/脏文件安全升级 > 脏值逐键拒收：类型不符一律回默认]
    [Fact]
    public void Load_DirtyValuesRejectedPerKey()
    {
        Seed(SeedJson(SeedState(
            theme: "neon",
            advancedMode: "yes",
            pinned: new object[] { "ok", 42, null, "ok2" },
            alertRules: new { idleAlertMinutes = -5, smb1Alert = "true", weakPasswordAlert = (object)null, diskLowGb = "20" },
            autoStart: 1,
            dashboardHistory: new object[] { new { ts = "no" }, new { ts = 1, sessions = 2 } },
            journal: new object[] { new { id = "a" }, new { noId = true }, "junk", null })));
        AppState s = _store.LoadState();
        Assert.Equal(ThemeMode.Light, s.theme);
        Assert.True(s.advancedMode);
        Assert.Equal(new[] { "ok", "ok2" }, s.pinned);
        Assert.Null(s.alertRules.idleAlertMinutes);
        Assert.True(s.alertRules.smb1Alert);
        Assert.True(s.alertRules.weakPasswordAlert);
        Assert.Equal(20, s.alertRules.diskLowGb);
        Assert.False(s.autoStart);
        Assert.Single(s.dashboardHistory);
        Assert.Equal(1, s.dashboardHistory[0].ts);
        Assert.Equal(2, s.dashboardHistory[0].sessions);
        Assert.Single(s.journal);
        Assert.Equal("a", s.journal[0].id);
    }

    // [Source: stateStore.test.ts#loadState 旧/脏文件安全升级 > 合法数值键四舍五入收敛（小数分钟/GB 不炸）]
    [Fact]
    public void Load_NumberKeysRounded()
    {
        Seed(SeedJson(SeedState(alertRules: new { idleAlertMinutes = 12.7, diskLowGb = 33.2 })));
        AppState s = _store.LoadState();
        Assert.Equal(13, s.alertRules.idleAlertMinutes);
        Assert.Equal(33, s.alertRules.diskLowGb);
    }

    // ===== loadState 文件损坏回落默认（不抛错）=====

    // [Source: stateStore.test.ts#loadState 文件损坏回落默认（不抛错）> 非 JSON 垃圾 → 默认值，不抛异常]
    [Fact]
    public void Load_CorruptFileFallsBackToDefaults()
    {
        Seed("{{{not json}}}");
        // TS: expect(() => loadState()).not.toThrow() + theme 断言（LoadState 抛错即测试失败）
        AppState s = _store.LoadState();
        Assert.Equal(ThemeMode.Light, s.theme);
    }

    // [Source: stateStore.test.ts#loadState 文件损坏回落默认（不抛错）> 顶层数组/字符串/数字/null 一律回落默认]
    [Fact]
    public void Load_NonObjectTopLevelFallsBack()
    {
        foreach (string bad in new[] { "[]", "\"str\"", "42", "null" })
        {
            StateStore store = new(_dir);
            Seed(bad);
            AssertDefaults(store.LoadState());
        }
    }

    // [Source: stateStore.test.ts#loadState 文件损坏回落默认（不抛错）> 损坏后 saveState 可修复文件（写回合法 JSON）]
    [Fact]
    public void CorruptFile_RepairedBySave()
    {
        Seed("garbage-garbage");
        _store.LoadState();
        _store.SaveState(new AppStatePatch { theme = ThemeMode.Dark });
        Assert.Equal("dark", ReadPersisted().GetProperty("theme").GetString());
    }

    // [Source: stateStore.test.ts#loadState 文件损坏回落默认（不抛错）> 进程内缓存：命中后不再读文件（改文件不影响已缓存态）]
    [Fact]
    public void Load_CacheHitIgnoresFileChange()
    {
        AppState s1 = _store.LoadState();
        Seed("{{break}}");
        AppState s2 = _store.LoadState();
        Assert.Same(s1, s2);
    }

    // ===== 容量上限：截断保留最新 =====

    // [Source: stateStore.test.ts#容量上限 > journal 超 200 → 保留最后 200 条（最新在末尾）]
    [Fact]
    public void Journal_TruncatedTo200KeepingNewest()
    {
        var journal = Enumerable.Range(0, 210).Select(i => new
        {
            id = $"j{i}",
            ts = i,
            action = "create",
            protocol = "smb",
            name = $"s{i}",
            undoable = false,
        }).ToArray();
        Seed(SeedJson(SeedState(journal: journal)));
        AppState s = _store.LoadState();
        Assert.Equal(200, s.journal.Count);
        Assert.Equal("j10", s.journal[0].id);
        Assert.Equal("j209", s.journal[199].id);
    }

    // [Source: stateStore.test.ts#容量上限 > dashboardHistory 超 288 → 保留最后 288 个采样点]
    [Fact]
    public void History_TruncatedTo288KeepingNewest()
    {
        var history = Enumerable.Range(0, 300).Select(i => new { ts = i, sessions = 0, openFiles = 0 }).ToArray();
        Seed(SeedJson(SeedState(dashboardHistory: history)));
        AppState s = _store.LoadState();
        Assert.Equal(288, s.dashboardHistory.Count);
        Assert.Equal(12, s.dashboardHistory[0].ts);
        Assert.Equal(299, s.dashboardHistory[287].ts);
    }

    // [Source: stateStore.test.ts#容量上限 > pinned 超 50 → 保留最后 50 个]
    [Fact]
    public void Pinned_TruncatedTo50KeepingNewest()
    {
        var pinned = Enumerable.Range(0, 60).Select(i => $"smb:s{i}").ToArray();
        Seed(SeedJson(SeedState(pinned: pinned)));
        AppState s = _store.LoadState();
        Assert.Equal(50, s.pinned.Count);
        Assert.Equal("smb:s59", s.pinned[49]);
    }

    // [Source: stateStore.test.ts#容量上限 > addJournal 追加后总量仍受上限约束（新条目必在）]
    [Fact]
    public void AddJournal_TotalStillCapped()
    {
        var journal = Enumerable.Range(0, 200).Select(i => new
        {
            id = $"old{i}",
            ts = i,
            action = "create",
            protocol = "smb",
            name = $"s{i}",
            undoable = false,
        }).ToArray();
        Seed(SeedJson(SeedState(journal: journal)));
        JournalEntry e = _store.AddJournal(new JournalEntryDraft
        {
            action = JournalAction.Delete,
            protocol = Protocol.Smb,
            name = "newest",
            undoable = true,
        });
        AppState s = _store.LoadState();
        Assert.Equal(200, s.journal.Count);
        Assert.Equal(e.id, s.journal[199].id);
        Assert.DoesNotContain(s.journal, j => j.id == "old0");
    }

    // ===== saveState 浅合并与持久化 =====

    // [Source: stateStore.test.ts#saveState 浅合并 > alertRules 只改一键时其余键保持原值（不覆盖未传键）]
    // 说明：C# AlertRules 非可空键无法表达"未改"，第二笔以 prev 值构造完整对象（与 IStateStore 注释语义一致）。
    [Fact]
    public void Save_AlertRulesShallowMergeKeepsOtherKeys()
    {
        AppState s1 = _store.SaveState(new AppStatePatch
        {
            alertRules = new AlertRules { idleAlertMinutes = null, smb1Alert = true, weakPasswordAlert = true, diskLowGb = 50 },
        });
        Assert.Equal(50, s1.alertRules.diskLowGb);
        Assert.Null(s1.alertRules.idleAlertMinutes);
        Assert.True(s1.alertRules.smb1Alert);

        // 第二次只改 smb1Alert：50 是"上一次存的值"而非默认 20，浅合并必须保住
        AppState s2 = _store.SaveState(new AppStatePatch
        {
            alertRules = new AlertRules
            {
                idleAlertMinutes = s1.alertRules.idleAlertMinutes,
                smb1Alert = false,
                weakPasswordAlert = s1.alertRules.weakPasswordAlert,
                diskLowGb = s1.alertRules.diskLowGb,
            },
        });
        Assert.Equal(50, s2.alertRules.diskLowGb);
        Assert.False(s2.alertRules.smb1Alert);
    }

    // [Source: stateStore.test.ts#saveState 浅合并 > patch 未含的顶层键保持（theme 修改不丢 pinned）]
    [Fact]
    public void Save_TopLevelKeysPreservedWhenNotInPatch()
    {
        _store.SaveState(new AppStatePatch { pinned = new[] { "smb:a" }, theme = ThemeMode.Dark });
        AppState s = _store.SaveState(new AppStatePatch { theme = ThemeMode.Light });
        Assert.Equal(new[] { "smb:a" }, s.pinned);
        Assert.Equal(ThemeMode.Light, s.theme);
    }

    // [Source: stateStore.test.ts#saveState 浅合并 > 原子写：文件可读、内存与磁盘一致、无 .tmp 残留]
    [Fact]
    public void Save_AtomicWriteNoTmpResidue()
    {
        _store.SaveState(new AppStatePatch { autoStart = true });
        Assert.True(ReadPersisted().GetProperty("autoStart").GetBoolean());
        Assert.False(File.Exists(StateFile + ".tmp"));
        // TS: __resetStateCache() 后 loadState 仍为 true（读盘验证）
        AppState s = new StateStore(_dir).LoadState();
        Assert.True(s.autoStart);
    }

    // [Source: stateStore.test.ts#saveState 浅合并 > patch 携带脏值同样被逐键收敛（IPC 边界无类型保证）]
    // 说明：C# patch 有类型边界，等价场景 = patch 携带超限集合仍被容量收敛。
    [Fact]
    public void Save_PatchStillConvergedPerKey()
    {
        var pinned60 = Enumerable.Range(0, 60).Select(i => $"smb:s{i}").ToArray();
        _store.SaveState(new AppStatePatch { pinned = pinned60, autoStart = true });
        AppState s = _store.LoadState();
        Assert.Equal(50, s.pinned.Count);
        Assert.Equal("smb:s59", s.pinned[49]);
        Assert.True(s.autoStart);
    }

    // ===== journal 读写闭环 =====

    // [Source: stateStore.test.ts#journal 读写闭环 > addJournal 生成 id/ts；removeJournal 只删指定 id]
    [Fact]
    public void Journal_AddRemoveRoundTrip()
    {
        JournalEntry a = _store.AddJournal(new JournalEntryDraft
        {
            action = JournalAction.Delete, protocol = Protocol.Smb, name = "docs", undoable = true,
        });
        JournalEntry b = _store.AddJournal(new JournalEntryDraft
        {
            action = JournalAction.Permset, protocol = Protocol.Smb, name = "x", undoable = true,
        });
        // TS: expect(a.id).toMatch(/^j\d+-\d+$/)
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"^j\d+-\d+$"), a.id);
        Assert.NotEqual(a.id, b.id);
        Assert.Equal(new[] { a.id, b.id }, _store.LoadState().journal.Select(j => j.id).ToArray());
        _store.RemoveJournal(a.id);
        Assert.Equal(new[] { b.id }, _store.LoadState().journal.Select(j => j.id).ToArray());
        // 删不存在的 id 是安全的 no-op
        _store.RemoveJournal("ghost");
        Assert.Single(_store.LoadState().journal);
    }

    // [Source: stateStore.test.ts#journal 读写闭环 > pushHistory 按序追加点位]
    [Fact]
    public void PushHistory_AppendsInOrder()
    {
        _store.PushHistory(new HistoryPoint { ts = 1, sessions = 0, openFiles = 0 });
        _store.PushHistory(new HistoryPoint { ts = 2, sessions = 3, openFiles = 4 });
        Assert.Equal(new long[] { 1, 2 }, _store.LoadState().dashboardHistory.Select(h => h.ts).ToArray());
    }
}
