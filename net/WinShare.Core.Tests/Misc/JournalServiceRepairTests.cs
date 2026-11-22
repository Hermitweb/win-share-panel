using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Misc;
using Xunit;

namespace WinShare.Core.Tests.Misc;

/// <summary>
/// v2.3：JournalService.ListJournal() 对历史乱码台账的修复（UI 与撤销共用此出口）。
/// 真实乱码样本取自用户环境 appstate.json（2026-10 期间的 200 条台账）。
/// </summary>
public class JournalServiceRepairTests
{
    [Fact]
    public void ListJournal_历史乱码_修复为中文()
    {
        // 用测试 bin 目录（可写），避免系统 Temp 的 ACL/UAC 拒绝（同 AppSettingsStoreTests 惯例）
        var dir = Path.Combine(AppContext.BaseDirectory, "wshare-js-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string garbled = "鍏变韩宸插垹闄わ紙NFS 鍏变韩鍙参粨涓嶆敮鎸佷竴閿箰鍙€锛岃鎸夌暀妗х顫宸ラ€祤»";
            var state = new AppState
            {
                journal = new List<JournalEntry>
                {
                    new JournalEntry
                    {
                        id = "j1", ts = 1, action = JournalAction.Delete, protocol = Protocol.Nfs,
                        name = "test", detail = garbled, undoable = false,
                    },
                },
            };
            var store = new StateStore(dir);
            // StateStore 的 dataDirOverride 是 base：文件在 <dir>/WinSharePanel/state/appstate.json
            Directory.CreateDirectory(Path.GetDirectoryName(store.StateFile)!);
            File.WriteAllText(store.StateFile,
                System.Text.Json.JsonSerializer.Serialize(state), new System.Text.UTF8Encoding(true));

            var svc = new JournalService(store, share: null!, user: null!);
            var list = svc.ListJournal();

            Assert.Single(list);
            Assert.Contains("共享已删除", list[0].detail);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
