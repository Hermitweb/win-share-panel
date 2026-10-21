// 假 IPsInvoker 断言 IIS 命令串（写域全干跑：New-WebFtpSite / Remove-Item IIS:\Sites\* 绝不真执行）
// + 假 FtpService 断言「服务配置类方法」纯委托 + 配置节解锁次数。
// 每个测试方法 XML doc 标注 [Source: ftpAdapter.ts#<方法>]。
using WinShare.Core.Contracts;
using WinShare.Core.Services.Protocols.Adapters;
using WinShare.Core.Tests.Services.ProtocolServices;

namespace WinShare.Core.Tests.Services.Protocols.Adapters;

[Collection(ProtocolAdaptersCollection.Name)]
public class FtpAdapterTests
{
    private const string SiteJson =
        @"[{""Name"":""site1"",""State"":""Started"",""PhysicalPath"":""C:/ftp"",""Port"":2121,""SslPolicy"":""SslRequire"",""AnonymousEnabled"":false,""BasicEnabled"":true}]";

    private static FtpAdapter Make(PsRecorder ps, FakeFtpService? ftp = null, FakeStateStore? store = null) =>
        new(ftp ?? new FakeFtpService(), ps, store ?? new FakeStateStore());

    private static CreateShareInput NewSite(string path = "C:/ftp") => new()
    {
        protocol = Protocol.Ftp,
        name = "site1",
        path = path,
        port = 2121,
        sslPolicy = SslPolicy.SslRequire,
        authMode = FtpAuthMode.Basic,
    };

    // [Source: ftpAdapter.ts#capabilities]
    [Fact]
    public void Capabilities_MatchTs()
    {
        FtpAdapter a = Make(new PsRecorder());
        Assert.Equal(Protocol.Ftp, a.Protocol);
        Assert.True(a.Capabilities.supportsCreate);
        Assert.True(a.Capabilities.supportsUpdate);
        Assert.True(a.Capabilities.supportsDelete);
        Assert.True(a.Capabilities.supportsToggle);
        Assert.True(a.Capabilities.supportsPermissions);
        Assert.False(a.Capabilities.supportsSessions);
        Assert.False(a.Capabilities.supportsOpenFiles);
        Assert.True(a.Capabilities.supportsServerConfig);
        Assert.True(a.Capabilities.supportsRestart);
        Assert.Equal(PermissionModel.IisAuth, a.Capabilities.permissionModel);
    }

    // [Source: ftpAdapter.ts#listShares —— LIST_SCRIPT 逐字（Get-Website + FTP 绑定过滤）+ retries:0 + 映射]
    [Fact]
    public async Task ListShares_RunsListScriptAndMaps()
    {
        var ps = new PsRecorder().WhenJson(SiteJson);
        IReadOnlyList<Share> shares = await Make(ps).ListSharesAsync();

        Assert.StartsWith("Import-Module WebAdministration; Get-Website | Where-Object { $hasFtp=$false;",
            ps.JsonCalls[0].Command);
        Assert.Contains("Get-Item \"IIS:\\Sites\\$name\"", ps.JsonCalls[0].Command);
        Assert.True(ps.JsonCalls[0].Command.Length > 1000);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Equal(15000, ps.JsonCalls[0].TimeoutMs);
        Share s = Assert.Single(shares);
        Assert.Equal("site1", s.name);
        Assert.Equal(Protocol.Ftp, s.protocol);
        Assert.Equal("C:/ftp", s.path);
        Assert.Equal(ShareStatus.Enabled, s.status);
        Assert.Equal(2121, s.port);
        Assert.Equal(SslPolicy.SslRequire, s.sslPolicy);
        Assert.Equal(FtpAuthMode.Basic, s.authMode);
        Assert.Equal("site1", s.siteName);
    }

    // [Source: ftpAdapter.ts#listShares -> mapFtpSite —— AnonymousEnabled → anonymous；SslRequireCredentials 归一]
    [Fact]
    public async Task ListShares_MapsAnonymousAndSslPolicy()
    {
        var ps = new PsRecorder().WhenJson(
            @"[{""Name"":""site2"",""State"":""Stopped"",""Port"":0,""SslPolicy"":""SslRequireCredentials"",""AnonymousEnabled"":true,""BasicEnabled"":false}]");
        Share s = Assert.Single(await Make(ps).ListSharesAsync());
        Assert.Equal(ShareStatus.Disabled, s.status);
        Assert.Equal(21, s.port);
        Assert.Equal(SslPolicy.SslRequireCredentials, s.sslPolicy);
        Assert.Equal(FtpAuthMode.Anonymous, s.authMode);
        Assert.Equal("", s.path);
    }

    // [Source: ftpAdapter.ts#createShare —— 步骤 1/3 New-WebFtpSite + 步骤 2/3 SSL/认证 + 步骤 3/3 回读 + addJournal]
    [Fact]
    public async Task CreateShare_CreatesSiteAppliesConfigAndJournals()
    {
        var ps = new PsRecorder().WhenJson(SiteJson);
        FakeFtpService ftp = new();
        FakeStateStore store = new();
        Share site = await Make(ps, ftp, store).CreateShareAsync(NewSite());

        Assert.Equal("New-WebFtpSite -Name 'site1' -PhysicalPath 'C:/ftp' -Port 2121 -Force", ps.VoidCalls[0].Command);
        Assert.Equal(0, ps.VoidCalls[0].Retries);
        // applyFtpConfig：解锁一次 + 一条合并的 Set 串（控制/数据通道 + 匿名/基本认证）
        Assert.Equal(1, ftp.UnlockCalls);
        Assert.Contains(
            @"try { Set-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath 'IIS:\Sites\site1' -Name controlChannelPolicy -Value 'SslRequire' -ErrorAction Stop } catch {}",
            ps.VoidCalls[1].Command);
        Assert.Contains(
            @"try { Set-WebConfigurationProperty -Filter 'ftpServer/security/authentication/anonymousAuthentication' -PSPath 'IIS:\Sites\site1' -Name enabled -Value $false -ErrorAction Stop } catch {}",
            ps.VoidCalls[1].Command);
        Assert.StartsWith(@"Import-Module WebAdministration; Get-Website | Where-Object { $_.Name -eq 'site1' }",
            ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Equal(JournalAction.Create, store.Journal[0].action);
        Assert.Equal(Protocol.Ftp, store.Journal[0].protocol);
        Assert.Equal("site1", store.Journal[0].name);
        Assert.Equal("路径 C:/ftp", store.Journal[0].detail);
        Assert.False(store.Journal[0].undoable);
        Assert.Equal("site1", site.name);
    }

    // [Source: ftpAdapter.ts#createShare —— 非法站点名/路径前置校验，不触发任何 PS 调用]
    [Fact]
    public async Task CreateShare_InvalidInput_ThrowsWithoutPsCall()
    {
        var ps = new PsRecorder();
        AppException ex = await Assert.ThrowsAsync<AppException>(() =>
            Make(ps).CreateShareAsync(NewSite("relative/path")));
        Assert.Equal("INVALID_PARAM", ex.Code);
        Assert.Empty(ps.VoidCalls);
        Assert.Empty(ps.JsonCalls);
    }

    // [Source: ftpAdapter.ts#createShare —— 回读不到站点 → 清理孤儿站点 + 验证 + commandFailed]
    [Fact]
    public async Task CreateShare_ReadbackMissing_CleansOrphanAndThrows()
    {
        var ps = new PsRecorder().WhenJson("[]").WhenJson("[]");
        FakeStateStore store = new();
        CreateShareInput input = NewSite();
        input = input with { sslPolicy = null, authMode = null };

        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps, store: store).CreateShareAsync(input));

        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("已自动清理孤儿站点", ex.Message);
        Assert.Equal(
            @"Import-Module WebAdministration; try { Remove-Item 'IIS:\Sites\site1' -Recurse -Force -ErrorAction Stop } catch {}",
            ps.VoidCalls[1].Command);
        Assert.Equal(0, ps.VoidCalls[1].Retries);
        Assert.StartsWith(@"Get-Website | Where-Object { $_.Name -eq 'site1' } | Select-Object -First 1",
            ps.JsonCalls[1].Command);
        Assert.Empty(store.Journal);
    }

    // [Source: ftpAdapter.ts#deleteShare —— Remove-Item IIS:\Sites\{name} + addJournal(delete)]
    [Fact]
    public async Task DeleteShare_RemovesSiteAndJournals()
    {
        var ps = new PsRecorder();
        FakeStateStore store = new();
        await Make(ps, store: store).DeleteShareAsync("site1");

        Assert.Equal(
            @"Import-Module WebAdministration; try { Remove-Item 'IIS:\Sites\site1' -Recurse -Force -ErrorAction Stop } catch {}",
            ps.LastVoid.Command);
        Assert.Equal(2, ps.LastVoid.Retries);
        Assert.Equal(JournalAction.Delete, store.Journal[0].action);
        Assert.Contains("不支持一键撤销", store.Journal[0].detail);
    }

    // [Source: ftpAdapter.ts#updateShare —— physicalPath 经 Set-ItemProperty 写入后重新回读]
    [Fact]
    public async Task UpdateShare_WritesPhysicalPathAndReadsBack()
    {
        var ps = new PsRecorder().WhenJson(SiteJson);
        Share site = await Make(ps).UpdateShareAsync("site1", new UpdateShareInput
        {
            protocol = Protocol.Ftp,
            physicalPath = "C:/ftp2",
        });

        Assert.Equal(
            @"Import-Module WebAdministration; Set-ItemProperty 'IIS:\Sites\site1' -Name physicalPath -Value 'C:/ftp2'",
            ps.VoidCalls[0].Command);
        Assert.Equal(0, ps.VoidCalls[0].Retries);
        Assert.Equal("site1", site.name);
    }

    // [Source: ftpAdapter.ts#updateShare —— 回读不到站点 → shareNotFound]
    [Fact]
    public async Task UpdateShare_SiteMissing_ThrowsShareNotFound()
    {
        var ps = new PsRecorder().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() =>
            Make(ps).UpdateShareAsync("site1", new UpdateShareInput { protocol = Protocol.Ftp }));
        Assert.Equal("SHARE_NOT_FOUND", ex.Code);
    }

    // [Source: ftpAdapter.ts#toggleShare —— 存在性检查 → Start-Website/Stop-Website（非 Get-Item .Start()）]
    [Fact]
    public async Task ToggleShare_StartsAndStopsSite()
    {
        var ps = new PsRecorder()
            .WhenJson(@"[""site1""]")
            .WhenJson(@"[""site1""]");
        FtpAdapter a = Make(ps);

        await a.ToggleShareAsync("site1", true);
        Assert.Equal(@"try { Start-Website -Name 'site1' -ErrorAction Stop } catch {}", ps.VoidCalls[0].Command);

        await a.ToggleShareAsync("site1", false);
        Assert.Equal(@"try { Stop-Website -Name 'site1' -ErrorAction Stop } catch {}", ps.VoidCalls[1].Command);
        // 存在性检查命令（firstOrNull 归一化）
        Assert.StartsWith(@"Get-Website | Where-Object { $_.Name -eq 'site1' }", ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
    }

    // [Source: ftpAdapter.ts#toggleShare —— 站点不存在 → shareNotFound 且不下发启停命令]
    [Fact]
    public async Task ToggleShare_SiteMissing_Throws()
    {
        var ps = new PsRecorder().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).ToggleShareAsync("site1", true));
        Assert.Equal("SHARE_NOT_FOUND", ex.Code);
        Assert.Empty(ps.VoidCalls);
    }

    // [Source: ftpAdapter.ts#getPermissions —— ftpServer/security/authorization/* 回读 + access 位映射]
    [Fact]
    public async Task GetPermissions_MapsRules()
    {
        var ps = new PsRecorder().WhenJson(
            @"[{""Users"":""u1"",""Roles"":"""",""Permissions"":""Read, Write"",""AccessType"":""Allow""},{""Users"":"""",""Roles"":""g1"",""Permissions"":""Read"",""AccessType"":""Deny""}]");
        IReadOnlyList<SharePermission> perms = await Make(ps).GetPermissionsAsync("site1");

        Assert.Equal(
            @"Get-WebConfiguration -Filter 'ftpServer/security/authorization/*' -PSPath 'IIS:\Sites\site1' -ErrorAction SilentlyContinue",
            ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Equal(2, perms.Count);
        Assert.Equal("u1", perms[0].account);
        Assert.Equal(AccountType.User, perms[0].accountType);
        Assert.Equal(ShareAccess.Change, perms[0].access);
        Assert.False(perms[0].deny);
        Assert.Equal("g1", perms[1].account);
        Assert.Equal(AccountType.Group, perms[1].accountType);
        Assert.Equal(ShareAccess.Read, perms[1].access);
        Assert.True(perms[1].deny);
    }

    // [Source: ftpAdapter.ts#setPermissions —— 解锁配置节 → Clear-WebConfiguration → Add-WebConfiguration（users=/roles=）]
    [Fact]
    public async Task SetPermissions_UnlocksClearsThenAdds()
    {
        var ps = new PsRecorder().WhenJson("[]");
        FakeFtpService ftp = new();
        await Make(ps, ftp).SetPermissionsAsync("site1", new[]
        {
            new SharePermission { shareName = "site1", account = "u1", accountType = AccountType.User, access = ShareAccess.Change },
            new SharePermission { shareName = "site1", account = "g1", accountType = AccountType.Group, access = ShareAccess.Read, deny = true },
        });

        Assert.Equal(1, ftp.UnlockCalls);
        Assert.Equal(
            @"Clear-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath 'IIS:\Sites\site1' -ErrorAction SilentlyContinue",
            ps.VoidCalls[0].Command);
        Assert.Equal(
            @"Add-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath 'IIS:\Sites\site1' -Value @{accessType='Allow';users='u1';permissions='Read,Write'} -ErrorAction Stop",
            ps.VoidCalls[1].Command);
        Assert.Equal(
            @"Add-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath 'IIS:\Sites\site1' -Value @{accessType='Deny';roles='g1';permissions='Read'} -ErrorAction Stop",
            ps.VoidCalls[2].Command);
        Assert.Equal(0, ps.VoidCalls[1].Retries);
    }

    // [Source: ftpAdapter.ts#setPermissions —— 授予失败 → 回滚到备份 + commandFailed]
    [Fact]
    public async Task SetPermissions_GrantFailure_RollsBackAndThrows()
    {
        var ps = new PsRecorder()
            .WhenJson(@"[{""Users"":""old"",""Permissions"":""Read"",""AccessType"":""Allow""}]")
            .WhenJson("[]")
            .WhenJson("[]");
        ps.WhenVoidOk(); // Clear
        ps.WhenVoidFails(new Exception("拒绝访问")); // Add 失败
        ps.WhenVoidOk(); // 回滚 Clear
        ps.WhenVoidOk(); // 回滚 Add

        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).SetPermissionsAsync("site1", new[]
        {
            new SharePermission { account = "u1", accountType = AccountType.User, access = ShareAccess.Full },
        }));

        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("已回滚到原始状态", ex.Message);
        Assert.Equal(
            @"Add-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath 'IIS:\Sites\site1' -Value @{accessType='Allow';users='old';permissions='Read'} -ErrorAction SilentlyContinue",
            ps.VoidCalls[3].Command);
    }

    // [Source: ftpAdapter.ts#listSessions/closeSession/listOpenFiles/closeFile —— TS 未实现（能力位 false）]
    [Fact]
    public async Task UnsupportedMembers_ThrowNotSupported()
    {
        FtpAdapter a = Make(new PsRecorder());
        await Assert.ThrowsAsync<NotSupportedException>(() => a.ListSessionsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => a.CloseSessionAsync("s1"));
        await Assert.ThrowsAsync<NotSupportedException>(() => a.ListOpenFilesAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => a.CloseFileAsync("f1"));
    }

    // [Source: ftpAdapter.ts#getServiceStatus/restartService/getConfig/setConfig/defaultConfig/restoreDefault —— 纯委托 ftp.*]
    [Fact]
    public async Task ServiceConfigMethods_DelegateToFtpService()
    {
        var ps = new PsRecorder();
        FakeFtpService ftp = new();
        FtpAdapter a = Make(ps, ftp);

        Assert.Equal("ftpsvc", (await a.GetServiceStatusAsync()).name);
        await a.RestartServiceAsync();
        Assert.Equal(1, ftp.RestartCalls);
        Assert.Same(ftp.Config, await a.GetConfigAsync());
        FtpServerConfig cfg = new() { greetingMessage = "hi" };
        await a.SetConfigAsync(cfg);
        Assert.Same(cfg, ftp.AppliedConfig);
        Assert.Same(ftp.Default, a.DefaultConfig());
        Assert.Same(ftp.Restored, await a.RestoreDefaultAsync());

        Assert.Empty(ps.JsonCalls);
        Assert.Empty(ps.VoidCalls);
        // 服务配置面不触发配置节解锁（解锁只属于 setPermissions/applyFtpConfig）
        Assert.Equal(0, ftp.UnlockCalls);
    }
}
