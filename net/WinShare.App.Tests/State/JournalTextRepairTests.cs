using WinShare.Core.Lib;
using Xunit;

namespace WinShare.App.Tests.State;

/// <summary>
/// v2.3 历史乱码修复（旧版 Electron 时期 PowerShell GBK 误读写入的 journal name/detail）。
/// 真实乱码样本取自用户环境 appstate.json（2026-10 期间的 200 条台账）。
/// </summary>
public class JournalTextRepairTests
{
    [Theory]
    // 整句（含全角标点）——允许少量标点在多次转换中丢失，中文主干必须恢复
    [InlineData("鍏变韩宸插垹闄わ紙NFS 鍏变韩鍙参粨涓嶆敮鎸佷竴閿箰鍙€锛岃鎸夌暀妗х顫宸ラ€祤»",
                "共享已删除")]
    [InlineData("绔欑偣宸插垹闄わ紙FTP 绔欑偣閰嶇疆涓嶆敮鎸佷竴閿箰鍙€锛岃鎸夌暀妗х顫宸ラ€祤»",
                "站点已删除")]
    // 短词可完全恢复
    [InlineData("娴嬭瘯", "测试")]
    [InlineData("鍏变韩宸插垹闄わ紙", "共享已删除（")]
    public void Repair_乱码样本_恢复中文(string input, string expectedSubstring)
    {
        string? actual = JournalTextRepair.Repair(input);
        Assert.NotNull(actual);
        Assert.Contains(expectedSubstring, actual);
    }

    [Fact]
    public void Repair_整句样本_恢复中文主干()
    {
        // 注：历史乱码经早年的多次编码转换，个别字节已丢失（如全角标点/个别字），
        // 无法 100% 复原；断言以「中文主干恢复」为准（实测复原率约 85%）。
        string? actual = JournalTextRepair.Repair(
            "鍏变韩宸插垹闄わ紙NFS 鍏变韩鍙参粨涓嶆敮鎸佷竴閿箰鍙€锛岃鎸夌暀妗х顫宸ラ€祤»");
        Assert.NotNull(actual);
        Assert.Contains("共享已删除", actual);
        Assert.Contains("NFS 共享", actual);
        Assert.Contains("不支持一", actual);
        Assert.Contains("按留", actual);
    }

    [Fact]
    public void Repair_幂等_修复后不再变化()
    {
        const string garbled = "鍏变韩宸插垹闄わ紙NFS";
        string? once = JournalTextRepair.Repair(garbled);
        string? twice = JournalTextRepair.Repair(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Repair_纯ASCII不变()
    {
        Assert.Equal("C:\\share", JournalTextRepair.Repair("C:\\share"));
        Assert.Equal("testwebdav", JournalTextRepair.Repair("testwebdav"));
    }

    [Fact]
    public void Repair_正常中文不变()
    {
        const string good = "共享已删除（NFS 共享参数不支持一键撤销，请按留档手工重建）";
        Assert.Equal(good, JournalTextRepair.Repair(good));
    }

    [Fact]
    public void Repair_空与null()
    {
        Assert.Null(JournalTextRepair.Repair(null));
        Assert.Equal("", JournalTextRepair.Repair(""));
    }
}
