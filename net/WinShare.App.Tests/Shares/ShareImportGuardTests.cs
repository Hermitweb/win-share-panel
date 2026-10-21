using WinShare.App.Shares;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Shares;

/// <summary>
/// 配置导入导出护栏测试（对齐 electron/services/share.ts importConfig 的 R-1 护栏 +
/// electron/lib/powershell.ts 校验白名单；导入完成文案对齐 Shares.tsx handleImport）。
/// </summary>
public class ShareImportGuardTests
{
    // ===== 校验白名单 [Source: powershell.ts#validateName/validatePath] =====

    [Fact]
    public void ValidateName_AcceptsCjkAndSafeChars()
    {
        Assert.True(ShareImportGuard.ValidateName("共享-1"));
        Assert.True(ShareImportGuard.ValidateName("Shared_Docs.v2"));
    }

    [Fact]
    public void ValidateName_RejectsEmptyTooLongOrIllegal()
    {
        Assert.False(ShareImportGuard.ValidateName(""));
        Assert.False(ShareImportGuard.ValidateName(null));
        Assert.False(ShareImportGuard.ValidateName(new string('a', 81)));
        Assert.False(ShareImportGuard.ValidateName("bad<name"));
        Assert.False(ShareImportGuard.ValidateName(@"C:\path"));
    }

    [Fact]
    public void ValidateShareName_AllowsTrailingDollar()
    {
        Assert.True(ShareImportGuard.ValidateShareName("IPC$"));
        Assert.True(ShareImportGuard.ValidateShareName("admin$"));
        Assert.False(ShareImportGuard.ValidateShareName("$$"));
        Assert.False(ShareImportGuard.ValidateShareName(""));
    }

    [Fact]
    public void ValidateAccountName_AllowsDomainAndMachineForms()
    {
        Assert.True(ShareImportGuard.ValidateAccountName(@"DOMAIN\user"));
        Assert.True(ShareImportGuard.ValidateAccountName("Everyone"));
        Assert.True(ShareImportGuard.ValidateAccountName("MACHINE$"));
        Assert.False(ShareImportGuard.ValidateAccountName("bad|name"));
    }

    [Fact]
    public void ValidatePath_LocalDriveRules()
    {
        Assert.True(ShareImportGuard.ValidatePath(@"E:\share"));
        Assert.True(ShareImportGuard.ValidatePath(@"e:/share/子目录"));
        Assert.False(ShareImportGuard.ValidatePath(@"\\server\share")); // UNC 非本地盘
        Assert.False(ShareImportGuard.ValidatePath(@"E:"));             // 需至少一个分隔段
        Assert.False(ShareImportGuard.ValidatePath(@"E:<bad>"));
        Assert.False(ShareImportGuard.ValidatePath(""));
        Assert.False(ShareImportGuard.ValidatePath(null));
    }

    // ===== 导入护栏（2MB / 结构 / 500 条目）[Source: share.ts#importConfig R-1] =====

    [Fact]
    public void Guard_RejectsOversizedContent()
    {
        var json = "{\"shares\":[" + new string('x', ShareImportGuard.MaxJsonBytes + 1) + "]}";
        var ex = Assert.Throws<ArgumentException>(() => ShareImportGuard.GuardImportJson(json));
        Assert.Equal("导入内容不合法或超过 2MB 上限", ex.Message);
    }

    [Fact]
    public void Guard_RejectsMalformedJson()
    {
        var ex = Assert.Throws<ArgumentException>(() => ShareImportGuard.GuardImportJson("{oops"));
        Assert.Equal("导入文件格式错误：JSON 解析失败", ex.Message);
    }

    [Fact]
    public void Guard_RejectsMissingSharesArray()
    {
        Assert.Equal("导入文件格式错误",
            Assert.Throws<ArgumentException>(() => ShareImportGuard.GuardImportJson("{"other":"1"")).Message);
        Assert.Equal("导入文件格式错误",
            Assert.Throws<ArgumentException>(() => ShareImportGuard.GuardImportJson("{"shares":"nope"")).Message);
        Assert.Equal("导入内容不合法或超过 2MB 上限",
            Assert.Throws<ArgumentException>(() => ShareImportGuard.GuardImportJson(null)).Message);
    }
    [Fact]
    public void Guard_RejectsOver500Entries()
    {
        var entries = string.Join(",", Enumerable.Range(0, 501).Select(_ =>
            "{\"name\":\"a\",\"path\":\"E:\\\\a\"}"));
        var ex = Assert.Throws<ArgumentException>(() =>
            ShareImportGuard.GuardImportJson("{\"shares\":[" + entries + "]}"));
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public void Guard_AcceptsValidPayload()
    {
        var arr = ShareImportGuard.GuardImportJson("{\"shares\":[{\"name\":\"a\",\"path\":\"E:\\\\a\"}]}");
        Assert.Equal(1, arr.GetArrayLength());
    }

    // ===== 条目合法性 [Source: share.ts#importConfig 逐条 validate] =====

    [Fact]
    public void IsValidEntry_BothLegit()
    {
        Assert.True(ShareImportGuard.IsValidEntry("Docs", @"E:\docs"));
        Assert.False(ShareImportGuard.IsValidEntry("", @"E:\docs"));
        Assert.False(ShareImportGuard.IsValidEntry("Docs", @"\\server\x"));
    }

    // ===== 导入完成文案 [Source: Shares.tsx#importProps.beforeUpload] =====

    [Fact]
    public void ImportReportText_NoSkips()
    {
        var r = new ImportReport { imported = 3, skipped = 0, errors = Array.Empty<string>() };
        Assert.Equal("导入完成：成功 3 个共享（仅 SMB）", ShareImportGuard.BuildImportReportText(r));
    }

    [Fact]
    public void ImportReportText_WithSkips_ShowsFirstTwoErrors()
    {
        var r = new ImportReport
        {
            imported = 1, skipped = 2,
            errors = new[] { "a: bad", "b: bad" },
        };
        Assert.Equal("导入完成：成功 1 个，跳过 2 个。a: bad；b: bad",
            ShareImportGuard.BuildImportReportText(r));
    }

    [Fact]
    public void ImportReportText_MoreThanTwoErrorsAppendsEtC()
    {
        var r = new ImportReport
        {
            imported = 0, skipped = 3,
            errors = new[] { "e1", "e2", "e3" },
        };
        Assert.Equal("导入完成：成功 0 个，跳过 3 个。e1；e2 等",
            ShareImportGuard.BuildImportReportText(r));
    }
}

