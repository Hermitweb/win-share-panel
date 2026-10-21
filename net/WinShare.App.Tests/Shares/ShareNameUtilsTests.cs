using WinShare.App.Shares;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Shares;

/// <summary>
/// 共享名/盘符/水位纯函数测试 [Source: src/utils/shareDefaults.ts]。
/// </summary>
public class ShareNameUtilsTests
{
    // ===== shareNameFromPath [Source: shareDefaults.ts#shareNameFromPath] =====

    [Fact]
    public void ShareNameFromPath_TakesLastSegment()
    {
        Assert.Equal("SharedDocs", ShareNameUtils.ShareNameFromPath(@"D:\Share\SharedDocs"));
        Assert.Equal("docs", ShareNameUtils.ShareNameFromPath("/mnt/share/docs"));
    }

    [Fact]
    public void ShareNameFromPath_TrimsTrailingSeparators()
    {
        Assert.Equal("Docs", ShareNameUtils.ShareNameFromPath(@"E:\Docs\"));
        Assert.Equal("Docs", ShareNameUtils.ShareNameFromPath(@"E:\Docs\\\\"));
    }

    [Fact]
    public void ShareNameFromPath_DriveRootReturnsEmpty()
    {
        // 盘符根不是文件夹名，返回空让调用方保留用户输入
        Assert.Equal("", ShareNameUtils.ShareNameFromPath(@"D:"));
        Assert.Equal("", ShareNameUtils.ShareNameFromPath(@"d"));
    }

    [Fact]
    public void ShareNameFromPath_StripsLeadingDotAndDollar()
    {
        // [Source: shareDefaults.test.ts L39] 前导 $ 剥掉：$hared → hared
        Assert.Equal("hared", ShareNameUtils.ShareNameFromPath(@"C:\Users\x\$hared"));
        // 前导点同样剥掉
        Assert.Equal("Hidden", ShareNameUtils.ShareNameFromPath(@"E:\.Hidden"));
    }

    [Fact]
    public void ShareNameFromPath_ReplacesIllegalChars()
    {
        // [Source: shareDefaults.test.ts L38] b<c>d: → b_c_d_（< 与 : 转下划线）
        Assert.Equal("b_c_d_", ShareNameUtils.ShareNameFromPath(@"D:\a\b<c>d:"));
    }

    [Fact]
    public void ShareNameFromPath_LimitsTo60Chars()
    {
        var name = new string('x', 80);
        Assert.Equal(60, ShareNameUtils.ShareNameFromPath(@"E:\" + name).Length);
    }

    [Fact]
    public void ShareNameFromPath_EmptyOrNull()
    {
        Assert.Equal("", ShareNameUtils.ShareNameFromPath(""));
        Assert.Equal("", ShareNameUtils.ShareNameFromPath(null));
    }

    // ===== driveOf / isUncPath [Source: shareDefaults.ts#driveOf/isUncPath] =====

    [Theory]
    [InlineData(@"E:\a\b", "E:")]
    [InlineData(@"d:root", "D:")]
    public void DriveOf_UppercasesLocalDrive(string path, string expected)
    {
        Assert.Equal(expected, ShareNameUtils.DriveOf(path));
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData("/mnt/share")]
    [InlineData("")]
    public void DriveOf_NonLocalReturnsNull(string path)
    {
        Assert.Null(ShareNameUtils.DriveOf(path));
    }

    [Fact]
    public void IsUncPath_MatchesServerShareOnly()
    {
        Assert.True(ShareNameUtils.IsUncPath(@"\\server\share"));
        Assert.False(ShareNameUtils.IsUncPath(@"E:\x"));
        Assert.False(ShareNameUtils.IsUncPath(@"\single"));
    }

    // ===== indexDiskUsages + waterLevelOf [Source: shareDefaults.ts#indexDiskUsages/waterLevelOf] =====

    [Fact]
    public void IndexDiskUsages_UppercaseKey()
    {
        var index = ShareNameUtils.IndexDiskUsages(
            new List<DiskUsage> { new() { drive = "e:", freeGB = 5, totalGB = 100, freePct = 5 } });
        Assert.True(index.ContainsKey("E:"));
    }

    [Fact]
    public void WaterLevelOf_UncPathReturnsNull()
    {
        var index = ShareNameUtils.IndexDiskUsages(new[]
        {
            new DiskUsage { drive = "E:", freeGB = 1, totalGB = 100, freePct = 1 },
        });
        Assert.Null(ShareNameUtils.WaterLevelOf(@"\\srv\share", index, 20));
        Assert.Null(ShareNameUtils.WaterLevelOf(@"", index, 20));
        // 查不到的盘（无本地卷就没有"剩余空间"语义）
        Assert.Null(ShareNameUtils.WaterLevelOf(@"Z:\x", index, 20));
    }

    [Fact]
    public void WaterLevelOf_OnlyWarnOrDanger()
    {
        var index = ShareNameUtils.IndexDiskUsages(new[]
        {
            new DiskUsage { drive = "E:", freeGB = 1, totalGB = 100, freePct = 1 },   // danger
            new DiskUsage { drive = "D:", freeGB = 10, totalGB = 100, freePct = 10 }, // warn
            new DiskUsage { drive = "C:", freeGB = 100, totalGB = 200, freePct = 50 }, // ok
        });
        var danger = ShareNameUtils.WaterLevelOf(@"E:\x", index, 20);
        var warn = ShareNameUtils.WaterLevelOf(@"D:\x", index, 20);
        var ok = ShareNameUtils.WaterLevelOf(@"C:\x", index, 20);
        Assert.NotNull(danger);
        Assert.Equal("danger", danger!.Value.level);
        Assert.NotNull(warn);
        Assert.Equal("warn", warn!.Value.level);
        Assert.Null(ok);
    }

    // ===== DEFAULT_READ_ACCESS [Source: shareDefaults.ts#DEFAULT_READ_ACCESS] =====

    [Fact]
    public void DefaultReadAccess_IsUsers()
    {
        Assert.Equal(new[] { "Users" }, ShareNameUtils.DefaultReadAccess);
    }
}

/// <summary>
/// 删除影响/撤销文案测试 [Source: src/utils/shareDefaults.ts#deleteImpactText/undoHintText]。
/// </summary>
public class ShareDeleteImpactTests
{
    static Share S(string name = "a", int conns = 0, Protocol proto = Protocol.Smb) => new()
    {
        name = name, path = @"E:\" + name, description = "", protocol = proto,
        type = ShareType.Disk, hidden = false, encrypted = false,
        concurrentUsers = conns, status = ShareStatus.Enabled, cached = false,
    };

    [Fact]
    public void DeleteImpact_SingleNoConnection()
    {
        var (text, danger) = ShareDeleteImpact.DeleteImpactText(new[] { S("a", 0) });
        Assert.Equal("当前无人连接，可安全删除。", text);
        Assert.False(danger);
    }

    [Fact]
    public void DeleteImpact_MultipleNoConnection()
    {
        var (text, danger) = ShareDeleteImpact.DeleteImpactText(new[] { S("a", 0), S("b", 0) });
        Assert.Equal("所选共享当前均无人连接。", text);
        Assert.False(danger);
    }

    [Fact]
    public void DeleteImpact_WithConnections_SumsAndCapsNamesAtThree()
    {
        var (text, danger) = ShareDeleteImpact.DeleteImpactText(
            new[] { S("a", 2), S("b", 3), S("c", 1), S("d", 5) });
        Assert.True(danger);
        Assert.Contains("当前 11 个连接正在使用", text);
        Assert.Contains("a(2)、b(3)、c(1)", text);
        Assert.Contains(" 等", text);
    }

    [Fact]
    public void UndoHint_SingleSmb()
    {
        Assert.Equal("删除后可在「操作回收站」一键撤销。",
            ShareDeleteImpact.UndoHintText(new[] { S("a", 0) }));
    }

    [Fact]
    public void UndoHint_SingleNonSmb()
    {
        Assert.Equal("该协议的删除仅留档，不支持一键撤销。",
            ShareDeleteImpact.UndoHintText(new[] { S("a", 0, Protocol.Ftp) }));
    }

    [Fact]
    public void UndoHint_AllSmb()
    {
        Assert.Equal("2 个 SMB 共享删除后均可在「操作回收站」撤销。",
            ShareDeleteImpact.UndoHintText(new[] { S("a"), S("b") }));
    }

    [Fact]
    public void UndoHint_Mixed()
    {
        Assert.Equal("其中 1 个 SMB 共享可撤销；1 个非 SMB 共享仅留档，不支持一键撤销。",
            ShareDeleteImpact.UndoHintText(new[] { S("a"), S("b", 0, Protocol.Ftp) }));
    }
}
