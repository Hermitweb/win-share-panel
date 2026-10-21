using System.IO;
using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests;

/// <summary>AppSettingsStore 对齐 electron/lib/stateStore.ts 的 normalize / saveState / pushHistory / journal / refreshInterval。</summary>
public class AppSettingsStoreTests : IDisposable
{
    readonly string _dir;

    public AppSettingsStoreTests()
    {
        // 用测试 bin 目录（可写），避免系统 Temp 的 ACL/UAC 拒绝
        _dir = Path.Combine(AppContext.BaseDirectory, "wsp-state-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new AppSettingsStore(_dir);
        var s = store.Load();

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

    [Fact]
    public void Normalize_MalformedJson_ReturnsDefaults()
    {
        var s = AppSettingsStore.NormalizeFromJson("not json at all");
        Assert.Equal(ThemeMode.Light, s.theme);
        Assert.True(s.advancedMode);
        Assert.Empty(s.journal);
    }

    [Fact]
    public void Normalize_SetsFreshDarkAndHistoryFromJson()
    {
        var s = AppSettingsStore.NormalizeFromJson(
            """{"theme":"dark","dashboardHistory":[{"ts":123,"sessions":4,"openFiles":2}]}""");
        Assert.Equal(ThemeMode.Dark, s.theme);
        var h = s.dashboardHistory!;
        Assert.Single(h);
        Assert.Equal(123, h[0].ts);
        Assert.Equal(4, h[0].sessions);
        Assert.Equal(2, h[0].openFiles);
    }

    [Fact]
    public void Normalize_TruncatesPinnedTo50()
    {
        var pins = string.Join(",", Enumerable.Range(1, 60).Select(i => $"\"s{i}\""));
        var json = "{\"pinned\":[" + pins + "]}";
        var s = AppSettingsStore.NormalizeFromJson(json);
        Assert.Equal(AppSettingsStore.PinnedMax, s.pinned!.Count);
        Assert.Equal("s60", s.pinned[^1]); // 保留尾部
    }

    [Fact]
    public void SaveState_PersistsToFileAndReloads()
    {
        var store = new AppSettingsStore(_dir);
        store.SaveState(new AppStatePatch
        {
            theme = ThemeMode.Dark,
            advancedMode = false,
            autoStart = true,
            // 契约 AppStatePatch.alertRules 为整体 AlertRules（无逐键 Partial，缺口已列回报）：
            // C# 侧调用方需携带全量规则，store 做整体替换 + normalize 收敛
            alertRules = new AlertRules { idleAlertMinutes = null, smb1Alert = false, weakPasswordAlert = true, diskLowGb = 20 },
        });

        var file = Path.Combine(_dir, "appstate.json");
        Assert.True(File.Exists(file));
        var text = File.ReadAllText(file);
        // 字段名 camelCase 且与 TS appstate.json 兼容
        Assert.Contains("\"theme\"", text);
        Assert.Contains("\"dark\"", text);
        Assert.Contains("\"alertRules\"", text);
        Assert.Contains("\"smb1Alert\"", text);
        Assert.Contains("\"weakPasswordAlert\"", text);
        Assert.Contains("\"diskLowGb\"", text);

        // 重新打开进程，缓存复位后能读回
        var store2 = new AppSettingsStore(_dir);
        store2.ResetCacheForTesting();
        var s = store2.Load();
        Assert.Equal(ThemeMode.Dark, s.theme);
        Assert.False(s.advancedMode);
        Assert.True(s.autoStart);
        Assert.False(s.alertRules.smb1Alert);
        Assert.True(s.alertRules.weakPasswordAlert);
        Assert.Equal(20, s.alertRules.diskLowGb);
    }

    [Fact]
    public void PushHistory_AppendsAndCapsAt288()
    {
        var store = new AppSettingsStore(_dir);
        for (int i = 0; i < 300; i++)
        {
            store.PushHistory(new HistoryPoint { ts = i, sessions = i, openFiles = i });
        }
        var s = store.Load();
        Assert.Equal(AppSettingsStore.HistoryMax, s.dashboardHistory!.Count);
        Assert.Equal(299, s.dashboardHistory[^1].ts); // 尾部=最新
    }

    [Fact]
    public void AddJournal_GeneratesIdAndTs_CapsAt200()
    {
        var store = new AppSettingsStore(_dir);
        for (int i = 0; i < 210; i++)
        {
            store.AddJournal(new JournalEntryDraft
            {
                action = JournalAction.Create,
                protocol = Protocol.Smb,
                name = "s",
                undoable = false,
            });
        }
        var s = store.Load();
        Assert.Equal(AppSettingsStore.JournalMax, s.journal!.Count);
        Assert.All(s.journal, j => Assert.False(string.IsNullOrEmpty(j.id)));
        Assert.All(s.journal, j => Assert.True(j.ts > 0));
    }

    [Fact]
    public void RemoveJournal_RemovesById()
    {
        var store = new AppSettingsStore(_dir);
        var j1 = store.AddJournal(new JournalEntryDraft { action = JournalAction.Delete, protocol = Protocol.Nfs, name = "a" });
        var j2 = store.AddJournal(new JournalEntryDraft { action = JournalAction.Delete, protocol = Protocol.Ftp, name = "b" });
        store.RemoveJournal(j1.id);
        var s = store.Load();
        var names = s.journal!.Select(j => j.name).ToList();
        Assert.DoesNotContain("a", names);
        Assert.Contains("b", names);
    }

    [Fact]
    public void RefreshInterval_DefaultsTo10_AndPersists()
    {
        var store = new AppSettingsStore(_dir);
        Assert.Equal(10, store.GetRefreshInterval());

        store.SaveRefreshInterval(30);
        var store2 = new AppSettingsStore(_dir);
        Assert.Equal(30, store2.GetRefreshInterval());
    }

    [Fact]
    public void RefreshInterval_InvalidValue_ClampsTo10()
    {
        var store = new AppSettingsStore(_dir);
        store.SaveRefreshInterval(7); // 非 1|5|10|30
        var store2 = new AppSettingsStore(_dir);
        Assert.Equal(10, store2.GetRefreshInterval());
    }

    [Fact]
    public void JsonWritten_IsCamelCaseAndTsCompatible()
    {
        var store = new AppSettingsStore(_dir);
        store.PushHistory(new HistoryPoint { ts = 1, sessions = 2, openFiles = 3 });
        var text = File.ReadAllText(Path.Combine(_dir, "appstate.json"));
        // TS 版字段名逐一对齐（camelCase，与 electron/types.ts AppState 一致）
        Assert.Contains("\"theme\"", text);
        Assert.Contains("\"advancedMode\"", text);
        Assert.Contains("\"pinned\"", text);
        Assert.Contains("\"alertRules\"", text);
        Assert.Contains("\"autoStart\"", text);
        Assert.Contains("\"dashboardHistory\"", text);
        Assert.Contains("\"journal\"", text);
        Assert.Contains("\"ts\":1", text.Replace(" ", ""));
    }
}
