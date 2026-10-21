// 假 IPsInvoker 断言 IIS 命令串（写域全干跑：New-/Remove-Website 绝不真执行）
// + 假 WebdavService 断言「服务配置类方法」纯委托 + 配置节解锁次数。
// 每个测试方法 XML doc 标注 [Source: webdavAdapter.ts#<方法>]。
using WinShare.Core.Contracts;
using WinShare.Core.Services.Protocols.Adapters;
using WinShare.Core.Tests.Services.ProtocolServices;

namespace WinShare.Core.Tests.Services.Protocols.Adapters;

[Collection(ProtocolAdaptersCollection.Name)]
public class WebdavAdapterTests
{
    private const string SiteJson =
        @"{""Name"":""site1"",""State"":""Started"",""PhysicalPath"":""C:/dav"",""Port"":8080,""AuthoringEnabled"":true,""AnonymousEnabled"":false}";

    private static WebdavAdapter Make(PsRecorder ps, FakeWebdavService? webdav = null, FakeStateStore? store = null) =>
        new(webdav ?? new FakeWebdavService(), ps, store ?? new FakeStateStore());

    private static CreateShareInput NewSite() => new()
    {
        protocol = Protocol.Webdav,
        name = "site1",
        path = "C:/dav",
        port = 8080,
        anonymousEnabled = true,
    };

    // [Source: webdavAdapter.ts#capabilities]
    [Fact]
    public void Capabilities_MatchTs()
    {
        WebdavAdapter a = Make(new PsRecorder());
        Assert.Equal(Protocol.Webdav, a.Protocol);
        Assert.True(a.Capabilities.supportsCreate);
        Assert.True(a.Capabilities.supportsUpdate);
        Assert.True(a.Capabilities.supportsDelete);
        Assert.True(a.Capabilities.supportsToggle);
        Assert.True(a.Capabilities.supportsPermissions);
        Assert.False(a.Capabilities.supportsSessions);
        Assert.False(a.Capabilities.supportsOpenFiles);
        Assert.True(a.Capabilities.supportsServerConfig);
        Assert.True(a.Capabilities.supportsRestart);
        Assert.Equal(PermissionModel.WebdavRules, a.Capabilities.permissionModel);
    }

    // [Source: webdavAdapter.ts#listShares —— LIST_SCRIPT 用完整 webdav 配置节路径 + retries:0 + 映射]
    [Fact]
    public async Task ListShares_UsesFullAuthoringFilterAndMaps()
    {
        var ps = new PsRecorder().WhenJson("[" + SiteJson + "]");
        IReadOnlyList<Share> shares = await Make(ps).ListSharesAsync();

        Assert.StartsWith("Import-Module WebAdministration; Get-Website | ForEach-Object {", ps.JsonCalls[0].Command);
        Assert.Contains("Get-WebConfiguration -Filter 'system.webServer/webdav/authoring'", ps.JsonCalls[0].Command);
        Assert.Contains("system.webServer/security/authentication/anonymousAuthentication", ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Equal(15000, ps.JsonCalls[0].TimeoutMs);
        Share s = Assert.Single(shares);
        Assert.Equal("site1", s.name);
        Assert.Equal(Protocol.Webdav, s.protocol);
        Assert.Equal("C:/dav", s.path);
        Assert.Equal(ShareStatus.Enabled, s.status);
        Assert.Equal(8080, s.port);
        Assert.Equal("site1", s.siteName);
        Assert.True(s.authoringEnabled);
        Assert.False(s.anonymousEnabled);
    }

    // [Source: webdavAdapter.ts#listShares -> mapWebdavSite —— Port 缺省回落 80、State 非 Started → Disabled]
    [Fact]
    public async Task ListShares_DefaultsPortTo80()
    {
        var ps = new PsRecorder().WhenJson(
            @"[{""Name"":""site2"",""State"":""Stopped"",""Port"":0,""AuthoringEnabled"":false,""AnonymousEnabled"":true}]");
        Share s = Assert.Single(await Make(ps).ListSharesAsync());
        Assert.Equal(80, s.port);
        Assert.Equal(ShareStatus.Disabled, s.status);
        Assert.True(s.anonymousEnabled);
    }

    // [Source: webdavAdapter.ts#createShare —— New-Website + enableAuthoring 验证 + 匿名访问 + 回读 + addJournal]
    [Fact]
    public async Task CreateShare_CreatesSiteEnablesAuthoringAndJournals()
    {
        var ps = new PsRecorder()
            .WhenJson("true")   // enableAuthoring 回读验证
            .WhenJson(SiteJson); // fetchSite
        FakeWebdavService webdav = new();
        FakeStateStore store = new();
        Share site = await Make(ps, webdav, store).CreateShareAsync(NewSite());

        Assert.Equal("New-Website -Name 'site1' -PhysicalPath 'C:/dav' -Port 8080 -Force", ps.VoidCalls[0].Command);
        Assert.Equal(0, ps.VoidCalls[0].Retries);
        // enableAuthoring：先解锁配置节，再 Set + 回读验证（默认重试 2 次）
        Assert.Contains(
            @"try { Set-WebConfigurationProperty -Filter 'system.webServer/webdav/authoring' -PSPath 'IIS:\Sites\site1' -Name enabled -Value $true -ErrorAction Stop } catch {}; $v = $false;",
            ps.JsonCalls[0].Command);
        Assert.Equal(2, ps.JsonCalls[0].Retries);
        // 匿名访问写入（$true 来自 psBool）
        Assert.Equal(
            @"try { Set-WebConfigurationProperty -Filter 'system.webServer/security/authentication/anonymousAuthentication' -PSPath 'IIS:\Sites\site1' -Name enabled -Value $true -ErrorAction Stop } catch {}",
            ps.VoidCalls[1].Command);
        Assert.Equal(2, webdav.UnlockCalls);
        Assert.StartsWith(@"Import-Module WebAdministration; Get-Website -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'site1' }",
            ps.JsonCalls[1].Command);
        Assert.Equal(0, ps.JsonCalls[1].Retries);
        Assert.Equal(JournalAction.Create, store.Journal[0].action);
        Assert.Equal(Protocol.Webdav, store.Journal[0].protocol);
        Assert.Equal("路径 C:/dav", store.Journal[0].detail);
        Assert.False(store.Journal[0].undoable);
        Assert.Equal("site1", site.name);
    }

    // [Source: webdavAdapter.ts#createShare —— authoring 启用失败 → Remove-Website 清理 + commandFailed]
    [Fact]
    public async Task CreateShare_AuthoringFails_CleansOrphanAndThrows()
    {
        var ps = new PsRecorder().WhenJson("false");
        FakeStateStore store = new();
        AppException ex = await Assert.ThrowsAsync<AppException>(() =>
            Make(ps, store: store).CreateShareAsync(NewSite()));

        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("WebDAV authoring 启用失败", ex.Message);
        Assert.Equal(@"try { Remove-Website -Name 'site1' -ErrorAction Stop } catch {}", ps.VoidCalls[1].Command);
        Assert.Equal(0, ps.VoidCalls[1].Retries);
        Assert.Empty(store.Journal);
    }

    // [Source: webdavAdapter.ts#createShare —— 回读不到站点 → 清理孤儿站点 + 验证 + commandFailed]
    [Fact]
    public async Task CreateShare_ReadbackMissing_CleansOrphanAndThrows()
    {
        var ps = new PsRecorder()
            .WhenJson("true")  // enableAuthoring
            .WhenJson("[]")    // fetchSite 空
            .WhenJson("[]");   // 清理后存在性验证
        CreateShareInput input = NewSite();
        input = input with { anonymousEnabled = null };
        FakeStateStore store = new();

        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps, store: store).CreateShareAsync(input));

        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("已自动清理孤儿站点", ex.Message);
        Assert.Equal(@"try { Remove-Website -Name 'site1' -ErrorAction Stop } catch {}", ps.VoidCalls[1].Command);
        Assert.StartsWith(@"Get-Website | Where-Object { $_.Name -eq 'site1' } | Select-Object -First 1",
            ps.JsonCalls[2].Command);
        Assert.Empty(store.Journal);
    }

    // [Source: webdavAdapter.ts#deleteShare —— Remove-Website（非 Remove-Item IIS:）+ addJournal(delete)]
    [Fact]
    public async Task DeleteShare_RemovesSiteAndJournals()
    {
        var ps = new PsRecorder();
        FakeStateStore store = new();
        await Make(ps, store: store).DeleteShareAsync("site1");

        Assert.Equal(@"try { Remove-Website -Name 'site1' -ErrorAction Stop } catch {}", ps.LastVoid.Command);
        Assert.Equal(2, ps.LastVoid.Retries);
        Assert.Equal(JournalAction.Delete, store.Journal[0].action);
        Assert.Contains("不支持一键撤销", store.Journal[0].detail);
    }

    // [Source: webdavAdapter.ts#updateShare —— 匿名访问 + authoring 开关均经解锁后写入，再回读]
    [Fact]
    public async Task UpdateShare_WritesAnonymousAndAuthoringThenReadsBack()
    {
        var ps = new PsRecorder().WhenJson(SiteJson);
        FakeWebdavService webdav = new();
        Share site = await Make(ps, webdav).UpdateShareAsync("site1", new UpdateShareInput
        {
            protocol = Protocol.Webdav,
            anonymousEnabled = false,
            authoringEnabled = true,
        });

        Assert.Equal(2, webdav.UnlockCalls);
        Assert.Equal(
            @"try { Set-WebConfigurationProperty -Filter 'system.webServer/security/authentication/anonymousAuthentication' -PSPath 'IIS:\Sites\site1' -Name enabled -Value $false -ErrorAction Stop } catch {}",
            ps.VoidCalls[0].Command);
        Assert.Equal(
            @"try { Set-WebConfigurationProperty -Filter 'system.webServer/webdav/authoring' -PSPath 'IIS:\Sites\site1' -Name enabled -Value $true -ErrorAction Stop } catch {}",
            ps.VoidCalls[1].Command);
        Assert.Equal(0, ps.VoidCalls[1].Retries);
        Assert.Equal("site1", site.name);
    }

    // [Source: webdavAdapter.ts#updateShare —— 回读不到站点 → shareNotFound]
    [Fact]
    public async Task UpdateShare_SiteMissing_ThrowsShareNotFound()
    {
        var ps = new PsRecorder().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() =>
            Make(ps).UpdateShareAsync("site1", new UpdateShareInput { protocol = Protocol.Webdav }));
        Assert.Equal("SHARE_NOT_FOUND", ex.Code);
    }

    // [Source: webdavAdapter.ts#toggleShare —— 存在性检查 → Start-Website/Stop-Website]
    [Fact]
    public async Task ToggleShare_StartsAndStopsSite()
    {
        var ps = new PsRecorder()
            .WhenJson(@"[""site1""]")
            .WhenJson(@"[""site1""]");
        WebdavAdapter a = Make(ps);

        await a.ToggleShareAsync("site1", true);
        Assert.Equal(@"try { Start-Website -Name 'site1' -ErrorAction Stop } catch {}", ps.VoidCalls[0].Command);

        await a.ToggleShareAsync("site1", false);
        Assert.Equal(@"try { Stop-Website -Name 'site1' -ErrorAction Stop } catch {}", ps.VoidCalls[1].Command);
        Assert.Equal(2, ps.VoidCalls[0].Retries);
        Assert.StartsWith(@"Get-Website | Where-Object { $_.Name -eq 'site1' }", ps.JsonCalls[0].Command);
    }

    // [Source: webdavAdapter.ts#toggleShare —— 站点不存在 → shareNotFound 且不下发启停命令]
    [Fact]
    public async Task ToggleShare_SiteMissing_Throws()
    {
        var ps = new PsRecorder().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).ToggleShareAsync("site1", true));
        Assert.Equal("SHARE_NOT_FOUND", ex.Code);
        Assert.Empty(ps.VoidCalls);
    }

    // [Source: webdavAdapter.ts#getPermissions —— authoringRules/* 回读 + Source/Write/Read 映射]
    [Fact]
    public async Task GetPermissions_MapsAccessBits()
    {
        var ps = new PsRecorder().WhenJson(
            @"[{""Users"":""u1"",""Roles"":"""",""Path"":""*"",""Access"":""Read, Write, Source""},{""Users"":"""",""Roles"":""g1"",""Path"":""*"",""Access"":""Read""}]");
        IReadOnlyList<SharePermission> perms = await Make(ps).GetPermissionsAsync("site1");

        Assert.Equal(
            @"Get-WebConfiguration -Filter 'system.webServer/webdav/authoring/authoringRules/*' -PSPath 'IIS:\Sites\site1' -ErrorAction SilentlyContinue",
            ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Equal(2, perms.Count);
        Assert.Equal("u1", perms[0].account);
        Assert.Equal(AccountType.User, perms[0].accountType);
        Assert.Equal(ShareAccess.Full, perms[0].access);
        Assert.False(perms[0].deny);
        Assert.Equal("g1", perms[1].account);
        Assert.Equal(AccountType.Group, perms[1].accountType);
        Assert.Equal(ShareAccess.Read, perms[1].access);
    }

    // [Source: webdavAdapter.ts#setPermissions —— 解锁 → Clear-WebConfiguration → Add-WebConfiguration（path='*'）]
    [Fact]
    public async Task SetPermissions_UnlocksClearsThenAdds()
    {
        var ps = new PsRecorder().WhenJson("[]");
        FakeWebdavService webdav = new();
        await Make(ps, webdav).SetPermissionsAsync("site1", new[]
        {
            new SharePermission { shareName = "site1", account = "u1", accountType = AccountType.User, access = ShareAccess.Change },
            new SharePermission { shareName = "site1", account = "g1", accountType = AccountType.Group, access = ShareAccess.Full },
        });

        Assert.Equal(1, webdav.UnlockCalls);
        Assert.Equal(
            @"Clear-WebConfiguration -Filter 'system.webServer/webdav/authoring/authoringRules' -PSPath 'IIS:\Sites\site1' -ErrorAction SilentlyContinue",
            ps.VoidCalls[0].Command);
        Assert.Equal(
            @"Add-WebConfiguration -Filter 'system.webServer/webdav/authoring/authoringRules' -PSPath 'IIS:\Sites\site1' -Value @{users='u1';path='*';access='Read,Write'} -ErrorAction Stop",
            ps.VoidCalls[1].Command);
        Assert.Equal(
            @"Add-WebConfiguration -Filter 'system.webServer/webdav/authoring/authoringRules' -PSPath 'IIS:\Sites\site1' -Value @{roles='g1';path='*';access='Read,Write,Source'} -ErrorAction Stop",
            ps.VoidCalls[2].Command);
        Assert.Equal(0, ps.VoidCalls[1].Retries);
    }

    // [Source: webdavAdapter.ts#setPermissions —— 授予失败 → 回滚到备份（SilentlyContinue）+ commandFailed]
    [Fact]
    public async Task SetPermissions_GrantFailure_RollsBackAndThrows()
    {
        var ps = new PsRecorder()
            .WhenJson(@"[{""Users"":""old"",""Roles"":"""",""Access"":""Read""}]")
            .WhenJson("[]")
            .WhenJson("[]");
        ps.WhenVoidOk(); // Clear
        ps.WhenVoidFails(new Exception("拒绝访问")); // Add 失败
        ps.WhenVoidOk(); // 回滚 Clear
        ps.WhenVoidOk(); // 回滚 Add

        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).SetPermissionsAsync("site1", new[]
        {
            new SharePermission { account = "u1", accountType = AccountType.User, access = ShareAccess.Change },
        }));

        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("已回滚到原始状态", ex.Message);
        Assert.Equal(
            @"Add-WebConfiguration -Filter 'system.webServer/webdav/authoring/authoringRules' -PSPath 'IIS:\Sites\site1' -Value @{users='old';path='*';access='Read'} -ErrorAction SilentlyContinue",
            ps.VoidCalls[3].Command);
    }

    // [Source: webdavAdapter.ts#listSessions/closeSession/listOpenFiles/closeFile —— TS 未实现（能力位 false）]
    [Fact]
    public async Task UnsupportedMembers_ThrowNotSupported()
    {
        WebdavAdapter a = Make(new PsRecorder());
        await Assert.ThrowsAsync<NotSupportedException>(() => a.ListSessionsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => a.CloseSessionAsync("s1"));
        await Assert.ThrowsAsync<NotSupportedException>(() => a.ListOpenFilesAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => a.CloseFileAsync("f1"));
    }

    // [Source: webdavAdapter.ts#getServiceStatus/restartService/getConfig/setConfig/defaultConfig/restoreDefault —— 纯委托 webdav.*]
    [Fact]
    public async Task ServiceConfigMethods_DelegateToWebdavService()
    {
        var ps = new PsRecorder();
        FakeWebdavService webdav = new();
        WebdavAdapter a = Make(ps, webdav);

        Assert.Equal("W3SVC", (await a.GetServiceStatusAsync()).name);
        await a.RestartServiceAsync();
        Assert.Equal(1, webdav.RestartCalls);
        Assert.Same(webdav.Config, await a.GetConfigAsync());
        WebdavServerConfig cfg = new();
        await a.SetConfigAsync(cfg);
        Assert.Same(cfg, webdav.AppliedConfig);
        Assert.Same(webdav.Default, a.DefaultConfig());
        Assert.Same(webdav.Restored, await a.RestoreDefaultAsync());

        Assert.Empty(ps.JsonCalls);
        Assert.Empty(ps.VoidCalls);
        Assert.Equal(0, webdav.UnlockCalls);
    }
}
