using WinShare.App.Settings;
using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Settings;

/// <summary>
/// 日志/审计查看器测试（等宽滚动 + 级别过滤；审计=journal 台账末 200 窗口）。
/// [Source: src/components/LogPanels.tsx（末 200 条/非 JSONL 行保留）+ LogSink.cs 行格式]
/// </summary>
public class LogViewerViewModelTests
{
    static string TempLogDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "wsp-log-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 应用日志_尾300行语义()
    {
        var dir = TempLogDir();
        var lines = Enumerable.Range(1, 320).Select(i => $"2026-01-01 00:0{i % 60}:00 [info] [t] line{i}").ToArray();
        File.WriteAllLines(Path.Combine(dir, "app.log"), lines, new System.Text.UTF8Encoding(false));
        var text = AppLogReader.ReadTail(300, dir);
        var got = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(300, got.Length);
        Assert.Contains("line31", got[0]); // 第 31 行起（丢最早 20 行）
        Assert.Contains("line320", got[^1]);
    }

    [Fact]
    public void 应用日志_级别过滤_脏行不穿透()
    {
        var text = """
        2026-01-01 00:00:01 [info] [main] ok
        2026-01-01 00:00:02 [warn] [smb] low
        2026-01-01 00:00:03 [error] [crash] boom
        脏行没有级别标记
        """;
        var all = AppLogReader.FilterByLevel(text, "all").Split('\n').Count(l => l.Trim().Length > 0);
        Assert.Equal(4, all);
        var warn = AppLogReader.FilterByLevel(text, "warn").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        Assert.Single(warn);
        Assert.Contains("[warn]", warn[0]);
        var err = AppLogReader.FilterByLevel(text, "error").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        Assert.Single(err);
        Assert.Contains("boom", err[0]);
    }

    [Fact]
    public void 应用日志_文件缺失_空串不抛()
    {
        Assert.Equal("", AppLogReader.ReadTail(300, TempLogDir()));
    }

    [Fact]
    public void 审计行_journal动作中文化_结果可撤销区分()
    {
        Assert.Equal("创建", AuditLogViewModel.ActionLabel(JournalAction.Create));
        Assert.Equal("删除", AuditLogViewModel.ActionLabel(JournalAction.Delete));
        Assert.Equal("停止", AuditLogViewModel.ActionLabel(JournalAction.ToggleOff));
        Assert.Equal("改权限", AuditLogViewModel.ActionLabel(JournalAction.Permset));
    }

    [Fact]
    public void 审计台账_最近200窗口_行结构()
    {
        var dir = TempLogDir();
        var store = new AppSettingsStore(dir + "\\state");
        for (int i = 0; i < 210; i++)
        {
            store.AddJournal(new JournalEntryDraft
            {
                action = JournalAction.Create,
                protocol = Protocol.Smb,
                name = $"share{i}",
                detail = i == 209 ? "配额超限" : null,
                undoable = true,
            });
        }
        var vm = new AuditLogViewModel(store);
        Assert.Equal(AppSettingsStore.JournalMax, vm.Rows.Count); // 200 窗口
        var first = vm.Rows.First();
        var last = vm.Rows[^1];
        Assert.Contains("share209", last.Target);
        Assert.Contains("SMB", first.Target);
        Assert.Equal("可撤销", last.Result);
        Assert.False(first.Raw);

        // 结果过滤
        store.RemoveJournal(first.Key);
        var vm2 = new AuditLogViewModel(store);
        var kept = vm2.Filter("可撤销");
        Assert.Equal(vm2.Rows.Count, kept.Count);
    }
}
