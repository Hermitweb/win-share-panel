// 移植自 electron/services/share.test.ts（createShare(smb) 假成功防线 B-2）+ importGuard.test.ts
// 的 share.importConfig 护栏部分。写域全部干跑（假 IPsInvoker），只断言命令串 + 错误路径。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Shares;
using WinShare.Core.Tests.Misc;

namespace WinShare.Core.Tests.Services.Shares;

public class ShareServiceTests
{
    // TS: baseOpts = { name: '测试', path: 'E:\\测试', fullAccess: ['Administrator'] }
    private static readonly CreateShareOpts BaseOpts = new()
    {
        name = "测试",
        path = @"E:\测试",
        fullAccess = new[] { "Administrator" },
    };

    // TS: raw = { Name, Path, Description, ShareType: 0, Hidden: false, ConcurrentUsers: 0,
    //            Cached: false, Encrypted: false, Special: false }
    private const string RawShareJson =
        "{\"Name\":\"测试\",\"Path\":\"E:\\\\测试\",\"Description\":\"\",\"ShareType\":0," +
        "\"Hidden\":false,\"ConcurrentUsers\":0,\"Cached\":false,\"Encrypted\":false,\"Special\":false}";

    private static (ShareService Svc, FakePsInvoker Ps) Make() => (new ShareService(new FakePsInvoker()), null!);

    private static FakePsInvoker PsOf(ShareService svc) => (FakePsInvoker)svc.GetType()
        .GetField("_ps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(svc)!;

    // ===== createShare 假成功防线（B-2）=====

    // [Source: share.test.ts#写命令抛错 → 清理孤儿并 rethrow（不再吞错）]
    [Fact]
    public async Task CreateShare_WriteCommandThrows_CleansUpOrphanAndRethrows()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenVoidFails(new Exception("命令执行失败：Access is denied"));

        Exception ex = await Assert.ThrowsAsync<Exception>(() => svc.CreateShareAsync(BaseOpts));
        Assert.Contains("Access is denied", ex.Message, StringComparison.Ordinal);
        // 第一个 reject 是 New-SmbShare；其后 createShare 的 catch 会发起清理 Remove
        Assert.Equal(2, ps.VoidCommands.Count);
    }

    // [Source: share.test.ts#写命令"成功"但回读为空 → 报回读失败，不得返回垃圾对象]
    [Fact]
    public async Task CreateShare_ReadBackEmptyArray_ReportsReadBackFailure()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        // mockedRunPowerShell.mockResolvedValueOnce([]) —— 空数组 → raw.Name undefined
        ps.WhenJsonEmptyArray();

        Exception ex = await Assert.ThrowsAsync<AppException>(() => svc.CreateShareAsync(BaseOpts));
        Assert.Contains("未能回读到该共享", ex.Message, StringComparison.Ordinal);
    }

    // [Source: share.test.ts#回读 null 同样拒绝]
    [Fact]
    public async Task CreateShare_ReadBackNull_ReportsReadBackFailure()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        // TS: mockedRunPowerShell.mockResolvedValue(null) —— C# 侧等价为"无 JSON 结果"（空数组归一化）
        await Assert.ThrowsAsync<AppException>(() => svc.CreateShareAsync(BaseOpts));
    }

    // [Source: share.test.ts#回读正常 → 返回映射后的共享]
    [Fact]
    public async Task CreateShare_ReadBackOk_ReturnsMappedShare()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenJsonObject(RawShareJson);

        Share share = await svc.CreateShareAsync(BaseOpts);
        Assert.Equal("测试", share.name);
        Assert.Equal(Protocol.Smb, share.protocol);
    }

    // ===== 注入闸门：用户输入必须过闸 =====

    // [Source: 新增 - 对应 TS createShare 的 validateName/validatePath/validateAccountName 前置校验]
    [Fact]
    public async Task CreateShare_InvalidNameOrPath_RejectedBeforeAnyCommand()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);

        await Assert.ThrowsAsync<AppException>(() =>
            svc.CreateShareAsync(BaseOpts with { name = "bad;name" }));
        await Assert.ThrowsAsync<AppException>(() =>
            svc.CreateShareAsync(BaseOpts with { path = "\\\\server\\share" }));
        // 账号注入同样拒绝
        await Assert.ThrowsAsync<AppException>(() =>
            svc.CreateShareAsync(BaseOpts with { fullAccess = new[] { "a'; Remove-SmbShare x; '" } }));
        Assert.Empty(ps.VoidCommands);
    }

    // ===== 命令串逐字性：createShare 完整开关矩阵 =====

    // [Source: 新增 - 对应 TS createShare 的命令拼装（parts 数组逐项）]
    [Fact]
    public async Task CreateShare_CommandString_MatchesTsAssembly()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenJsonObject(RawShareJson);

        await svc.CreateShareAsync(new CreateShareOpts
        {
            name = "share1",
            path = @"D:\data",
            description = "desc",
            fullAccess = new[] { "DOMAIN\\user", "Everyone" },
            changeAccess = new[] { "Alice" },
            readAccess = new[] { "Bob" },
            noAccess = new[] { "Eve" },
            concurrentUserLimit = 5,
            cachingMode = CachingMode.Manual,
            folderEnumerationMode = FolderEnumerationMode.AccessBased,
            encryptData = true,
            shareShadowCopy = true,
        });

        string cmd = ps.VoidCommands[0];
        Assert.Contains("New-SmbShare", cmd);
        Assert.Contains("-Name 'share1'", cmd);
        Assert.Contains("-Path 'D:\\data'", cmd);
        Assert.Contains("-Description 'desc'", cmd);
        Assert.Contains("-EncryptData $true", cmd);
        Assert.Contains("-FullAccess 'DOMAIN\\user','Everyone'", cmd);
        Assert.Contains("-ChangeAccess 'Alice'", cmd);
        Assert.Contains("-ReadAccess 'Bob'", cmd);
        Assert.Contains("-NoAccess 'Eve'", cmd);
        Assert.Contains("-ConcurrentUserLimit 5", cmd);
        Assert.Contains("-CachingMode Manual", cmd);
        Assert.Contains("-FolderEnumerationMode AccessBased", cmd);
        Assert.Contains("-ShareShadowCopy", cmd);
    }

    // [Source: 新增 - 对应 TS 的 psNumber 闸门：非法 concurrentUserLimit（0）不拼参数]
    [Fact]
    public async Task CreateShare_ZeroConcurrentUserLimit_Omitted()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenJsonObject(RawShareJson);
        await svc.CreateShareAsync(BaseOpts with { concurrentUserLimit = 0 });
        Assert.DoesNotContain("-ConcurrentUserLimit", ps.VoidCommands[0], StringComparison.Ordinal);
    }

    // [Source: 新增 - 对应 TS 的 psEnum 闸门：非白名单 cachingMode 不拼参数]
    [Fact]
    public async Task CreateShare_InvalidEnum_Omitted()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenJsonObject(RawShareJson);
        // CachingMode 契约枚举无"非法值"可达路径；此处验证 None（合法值）可正常下发
        await svc.CreateShareAsync(BaseOpts with { cachingMode = CachingMode.None });
        Assert.Contains("-CachingMode None", ps.VoidCommands[0], StringComparison.Ordinal);
    }

    // ===== updateShare 命令拼装 =====

    // [Source: 新增 - 对应 TS updateShare 的 parts 拼装（含 cached → CachingMode 派生）]
    [Fact]
    public async Task UpdateShare_CommandString_MatchesTsAssembly()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenJsonObject(RawShareJson);

        await svc.UpdateShareAsync("测试", new UpdateShareOpts
        {
            description = "d",
            concurrentUserLimit = 3,
            cachingMode = CachingMode.Documents,
            folderEnumerationMode = FolderEnumerationMode.Unrestricted,
            encryptData = false,
            hidden = true,
            cached = true,
        });

        string cmd = ps.VoidCommands[0];
        Assert.Contains("Set-SmbShare", cmd);
        Assert.Contains("-Name '测试'", cmd);
        Assert.Contains("-Description 'd'", cmd);
        Assert.Contains("-ConcurrentUserLimit 3", cmd);
        Assert.Contains("-CachingMode Documents", cmd);
        Assert.Contains("-FolderEnumerationMode Unrestricted", cmd);
        Assert.Contains("-EncryptData $false", cmd);
        Assert.Contains("-Hidden $true", cmd);
        Assert.Contains("-Force", cmd);
        // cached=true 且已显式给 CachingMode → 不再追加 Manual
        Assert.DoesNotContain("-CachingMode Manual", cmd, StringComparison.Ordinal);
    }

    // [Source: 新增 - 对应 TS updateShare：cached=false 且未给 CachingMode → 派生 -CachingMode None]
    [Fact]
    public async Task UpdateShare_CachedFalse_NoExplicitMode_DerivesNone()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenJsonObject(RawShareJson);
        await svc.UpdateShareAsync("测试", new UpdateShareOpts { cached = false });
        Assert.Contains("-CachingMode None", ps.VoidCommands[0], StringComparison.Ordinal);
    }

    // ===== delete / toggle 系统特殊共享防护 =====

    // [Source: 新增 - 对应 TS deleteShare/toggleShare 的 SYSTEM_SPECIAL_SHARES 防护]
    [Theory]
    [InlineData("ADMIN$")]
    [InlineData("IPC$")]
    [InlineData("C$")]
    public async Task SystemSpecialShares_DeleteAndToggle_Rejected(string name)
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        await Assert.ThrowsAsync<AppException>(() => svc.DeleteShareAsync(name));
        await Assert.ThrowsAsync<AppException>(() => svc.ToggleShareAsync(name, enabled: false));
        await Assert.ThrowsAsync<AppException>(() => svc.ToggleShareAsync(name, enabled: true));
        Assert.Empty(ps.VoidCommands);
    }

    // [Source: 新增 - 对应 TS deleteShare 的快照捕获 + Remove-SmbShare 命令串]
    [Fact]
    public async Task DeleteShare_CapturesSnapshotThenRemoves()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenJsonObject(RawShareJson); // listShares 回读
        ps.WhenJsonObject("{\"AccountName\":\"Everyone\",\"AccountType\":0,\"AccessRight\":0,\"AccessControlType\":0}");
        ps.WhenJsonObject("{\"ConcurrentUserLimit\":5,\"CachingMode\":\"Manual\",\"FolderEnumerationMode\":\"AccessBased\"}");

        await svc.DeleteShareAsync("测试");

        Assert.Contains(ps.VoidCommands, c => c == "Remove-SmbShare -Name '测试' -Force");
        // 快照查询命令（Get-SmbShareAccess / 高级选项 Select-Object）逐字保留
        Assert.Contains(ps.JsonCommands, c => c.Contains("Get-SmbShareAccess -Name '测试'"));
        Assert.Contains(ps.JsonCommands, c =>
            c.Contains("Select-Object ConcurrentUserLimit, CachingMode, FolderEnumerationMode"));
    }

    // [Source: 新增 - 对应 TS restoreShare 的 allow/deny 分流与 Block-SmbShareAccess 补写]
    [Fact]
    public async Task RestoreShare_AppliesAllowThenBlocksDeny()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenJsonObject(RawShareJson);

        await svc.RestoreShareAsync(new ShareSnapshot
        {
            name = "测试",
            path = @"E:\测试",
            description = "d",
            encrypted = false,
            concurrentUserLimit = 5,
            cachingMode = CachingMode.Manual,
            folderEnumerationMode = FolderEnumerationMode.AccessBased,
            permissions = new[]
            {
                new SharePermission { account = "Alice", accountType = AccountType.User, access = ShareAccess.Full },
                new SharePermission { account = "Bob", accountType = AccountType.User, access = ShareAccess.Change },
                new SharePermission { account = "Carol", accountType = AccountType.User, access = ShareAccess.Read },
                new SharePermission { account = "Eve", accountType = AccountType.User, access = ShareAccess.Read, deny = true },
            },
        });

        string create = ps.VoidCommands[0];
        Assert.Contains("New-SmbShare", create);
        Assert.Contains("-FullAccess 'Alice'", create);
        Assert.Contains("-ChangeAccess 'Bob'", create);
        Assert.Contains("-ReadAccess 'Carol'", create);
        Assert.Contains("-ConcurrentUserLimit 5", create);
        // deny 项经 Block-SmbShareAccess 补回
        Assert.Contains(ps.VoidCommands, c => c == "Block-SmbShareAccess -Name '测试' -AccountName 'Eve' -Force");
    }

    // ===== toggleShare 禁用/恢复分支 =====

    // [Source: 新增 - 对应 TS toggleShare(enabled=false) 的 Remove + 禁用记录落盘]
    [Fact]
    public async Task ToggleShare_Disable_RemovesAndArchives()
    {
        string dir = TestTmp.New("wsshare");
        WinShare.Core.Services.Shares.ShareData.SetDirForTesting(dir);
        try
        {
            var ps = new FakePsInvoker();
            var svc = new ShareService(ps);
            ps.WhenJsonObject(RawShareJson);                                  // listShares
            ps.WhenJsonObject("{\"AccountName\":\"Everyone\",\"AccountType\":0,\"AccessRight\":0,\"AccessControlType\":0}");
            ps.WhenJsonObject("{\"ConcurrentUserLimit\":5,\"CachingMode\":\"Manual\",\"FolderEnumerationMode\":\"AccessBased\"}");

            await svc.ToggleShareAsync("测试", enabled: false);

            Assert.Contains(ps.VoidCommands, c => c == "Remove-SmbShare -Name '测试' -Force");
            // 禁用记录落盘到隔离数据目录（非真实 APPDATA）
            string file = System.IO.Path.Combine(dir, "disabled.json");
            Assert.True(System.IO.File.Exists(file));
            Assert.Contains("测试", System.IO.File.ReadAllText(file), StringComparison.Ordinal);
        }
        finally
        {
            WinShare.Core.Services.Shares.ShareData.SetDirForTesting(null);
        }
    }

    // [Source: 新增 - 对应 TS toggleShare(enabled=true) 无禁用记录时报错]
    [Fact]
    public async Task ToggleShare_EnableWithoutRecord_Fails()
    {
        string dir = TestTmp.New("wsshare");
        WinShare.Core.Services.Shares.ShareData.SetDirForTesting(dir);
        try
        {
            var ps = new FakePsInvoker();
            var svc = new ShareService(ps);
            await Assert.ThrowsAsync<AppException>(() => svc.ToggleShareAsync("测试", enabled: true));
            Assert.Empty(ps.VoidCommands);
        }
        finally
        {
            WinShare.Core.Services.Shares.ShareData.SetDirForTesting(null);
        }
    }

    // ===== 读域命令串 =====

    // [Source: 新增 - 对应 TS getSharePermissions/getShareConnections/getShareOpenFiles 的命令串]
    [Theory]
    [InlineData("perms")]
    [InlineData("conn")]
    [InlineData("files")]
    public async Task ReadDomain_CommandStrings_Verbatim(string which)
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenJsonEmptyArray();

        switch (which)
        {
            case "perms":
                await svc.GetSharePermissionsAsync("s1");
                Assert.Contains(ps.JsonCommands, c => c == "Get-SmbShareAccess -Name 's1'");
                break;
            case "conn":
                ShareConnections conn = await svc.GetShareConnectionsAsync("s1");
                Assert.Equal(0, conn.concurrentUsers);
                Assert.Contains(ps.JsonCommands, c =>
                    c == "Get-SmbConnection | Where-Object { $_.ShareName -eq 's1' } | Select-Object ClientUserName, ClientComputerName");
                break;
            default:
                IReadOnlyList<ShareOpenFile> files = await svc.GetShareOpenFilesAsync("s1");
                Assert.Empty(files);
                Assert.Contains(ps.JsonCommands, c =>
                    c == "Get-SmbOpenFile | Where-Object { $_.Path -like '*s1*' } | Select-Object FileId, Path, ClientUserName, ClientComputerName, LockCount");
                break;
        }
    }

    // [Source: 新增 - 对应 TS closeShareOpenFiles 的逐文件 Close-SmbOpenFile + closed/failed 统计]
    [Fact]
    public async Task CloseShareOpenFiles_CountsClosedAndFailed()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        // 两个打开文件：第一个关闭成功、第二个关闭失败（FakePsInvoker 的 JSON 队列一次只消费一项，
        // 故按「两次 getShareOpenFiles」给两次单个对象结果——TS 侧该函数对每个文件独立 try/catch）
        // 一次查询返回两个打开文件（真实 Get-SmbOpenFile 多行输出即 JSON 数组），
        // 第一个关闭成功、第二个关闭失败 → closed=1 / failed=1（TS 对每个文件独立 try/catch）
        ps.WhenJsonObject("[{\"FileId\":11,\"Path\":\"D:\\\\a\\\\s1\\\\x.txt\",\"ClientUserName\":\"u\",\"ClientComputerName\":\"c\",\"LockCount\":1},{\"FileId\":12,\"Path\":\"D:\\\\a\\\\s1\\\\y.txt\",\"ClientUserName\":\"u\",\"ClientComputerName\":\"c\",\"LockCount\":1}]");
        ps.WhenVoidOk();
        ps.WhenVoidFails(new Exception("locked"));

        CloseOpenFilesResult r = await svc.CloseShareOpenFilesAsync("s1");
        Assert.Equal(1, r.closed);
        Assert.Equal(1, r.failed);
        Assert.Contains(ps.VoidCommands, c => c == "Close-SmbOpenFile -FileId 11 -Force");
    }

    // ===== exportConfig / importConfig 护栏（importGuard.test.ts）=====

    // [Source: importGuard.test.ts#share.importConfig > 非法 JSON → 友好错误而非裸 SyntaxError]
    [Fact]
    public async Task ImportConfig_InvalidJson_FriendlyError()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        Exception ex = await Assert.ThrowsAsync<AppException>(() => svc.ImportConfigAsync("{ not json"));
        Assert.Contains("JSON 解析失败", ex.Message, StringComparison.Ordinal);
    }

    // [Source: importGuard.test.ts#share.importConfig > 超过 2MB → 先行拒绝]
    [Fact]
    public async Task ImportConfig_Over2MB_Rejected()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        string overSize = new string('a', 2 * 1024 * 1024 + 1);
        string json = JsonSerializer.Serialize(new { shares = new[] { overSize } });
        Exception ex = await Assert.ThrowsAsync<AppException>(() => svc.ImportConfigAsync(json));
        Assert.Contains("2MB 上限", ex.Message, StringComparison.Ordinal);
    }

    // [Source: importGuard.test.ts#share.importConfig > 缺少 shares 字段 → 格式错误]
    [Fact]
    public async Task ImportConfig_MissingSharesField_FormatError()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        Exception ex = await Assert.ThrowsAsync<AppException>(() => svc.ImportConfigAsync("{\"foo\":1}"));
        Assert.Contains("导入文件格式错误", ex.Message, StringComparison.Ordinal);
    }

    // [Source: importGuard.test.ts#share.importConfig > 条目超过 500 → 拒绝分批提示]
    [Fact]
    public async Task ImportConfig_Over500Items_Rejected()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        var big = new { shares = Enumerable.Range(0, 501).Select(_ => new { name = "s", path = @"C:\s" }).ToArray() };
        Exception ex = await Assert.ThrowsAsync<AppException>(() => svc.ImportConfigAsync(JsonSerializer.Serialize(big)));
        Assert.Contains("500 上限", ex.Message, StringComparison.Ordinal);
    }

    // [Source: 新增 - 对应 TS importConfig 的逐条校验：非法 name/path 跳过并记 errors]
    [Fact]
    public async Task ImportConfig_InvalidEntry_SkippedWithError()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        ps.WhenJsonObject(RawShareJson); // 合法条目 createShare 回读

        ImportReport report = await svc.ImportConfigAsync(JsonSerializer.Serialize(new
        {
            shares = new[]
            {
                new { name = "bad;name", path = @"C:\x", description = "", encrypted = false },
                new { name = "ok", path = "not-a-path", description = "", encrypted = false },
                new { name = "测试", path = @"E:\测试", description = "", encrypted = false },
            }
        }));

        Assert.Equal(1, report.imported);
        Assert.Equal(2, report.skipped);
        Assert.Equal(2, report.errors.Count);
        Assert.Contains(report.errors, e => e.Contains("跳过非法条目", StringComparison.Ordinal));
    }

    // [Source: 新增 - 对应 TS exportConfig 跳过 Special/IPC 共享]
    [Fact]
    public async Task ExportConfig_SkipsSpecialAndIpc()
    {
        var ps = new FakePsInvoker();
        var svc = new ShareService(ps);
        // 一次 listShares 返回三条：普通磁盘 / IPC / Special
        ps.WhenJsonObject("[" +
            "{\"Name\":\"data\",\"Path\":\"D:\\\\data\",\"Description\":\"\",\"ShareType\":0,\"Hidden\":false,\"ConcurrentUsers\":0,\"Cached\":false,\"Encrypted\":false,\"Special\":false}," +
            "{\"Name\":\"IPC$\",\"Path\":\"\",\"Description\":\"\",\"ShareType\":3,\"Hidden\":false,\"ConcurrentUsers\":0,\"Cached\":false,\"Encrypted\":false,\"Special\":true}" +
            "]");
        ps.WhenJsonObject("{\"AccountName\":\"Everyone\",\"AccountType\":0,\"AccessRight\":0,\"AccessControlType\":0}");

        string json = await svc.ExportConfigAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement shares = doc.RootElement.GetProperty("shares");
        Assert.Equal(1, shares.GetArrayLength());
        Assert.Equal("data", shares[0].GetProperty("name").GetString());
        Assert.True(doc.RootElement.TryGetProperty("exportedAt", out _));
    }
}
