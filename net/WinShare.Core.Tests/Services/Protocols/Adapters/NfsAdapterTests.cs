// 假 IPsInvoker 断言命令串（写域全干跑：New-/Remove-NfsShare 绝不真执行）+ 假服务断言委托面。
// 每个测试方法 XML doc 标注 [Source: nfsAdapter.ts#<方法>]。
using WinShare.Core.Contracts;
using WinShare.Core.Services.Protocols.Adapters;
using WinShare.Core.Tests.Services.ProtocolServices;

namespace WinShare.Core.Tests.Services.Protocols.Adapters;

[Collection(ProtocolAdaptersCollection.Name)]
public class NfsAdapterTests
{
    private const string ShareReadback =
        @"{""Name"":""nfs1"",""Path"":""C:/data"",""NetworkName"":""fs1"",""Authentication"":[""krb5"",""sys""],""AnonymousUid"":65534,""AnonymousGid"":-2,""EnableUnmappedAccess"":true,""AllowRootAccess"":false,""Permission"":""rw"",""Online"":true}";

    private static NfsAdapter Make(PsRecorder ps, FakeNfsService? nfs = null, FakeStateStore? store = null) =>
        new(nfs ?? new FakeNfsService(), ps, store ?? new FakeStateStore());

    // [Source: nfsAdapter.ts#capabilities]
    [Fact]
    public void Capabilities_MatchTs()
    {
        NfsAdapter a = Make(new PsRecorder());
        Assert.Equal(Protocol.Nfs, a.Protocol);
        Assert.True(a.Capabilities.supportsCreate);
        Assert.True(a.Capabilities.supportsUpdate);
        Assert.True(a.Capabilities.supportsDelete);
        Assert.False(a.Capabilities.supportsToggle);
        Assert.True(a.Capabilities.supportsPermissions);
        Assert.True(a.Capabilities.supportsSessions);
        Assert.False(a.Capabilities.supportsOpenFiles);
        Assert.True(a.Capabilities.supportsServerConfig);
        Assert.True(a.Capabilities.supportsRestart);
        Assert.Equal(PermissionModel.NfsKrb, a.Capabilities.permissionModel);
    }

    // [Source: nfsAdapter.ts#listShares —— 命令串 Get-NfsShare + retries:0 + 字段映射]
    [Fact]
    public async Task ListShares_QueriesGetNfsShareAndMaps()
    {
        var ps = new PsRecorder().WhenJson("[" + ShareReadback + "]");
        IReadOnlyList<Share> shares = await Make(ps).ListSharesAsync();

        Assert.Equal("Get-NfsShare", ps.JsonCalls[0].Command);
        Assert.Equal(15000, ps.JsonCalls[0].TimeoutMs);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Single(shares);
        Share s = shares[0];
        Assert.Equal("nfs1", s.name);
        Assert.Equal(Protocol.Nfs, s.protocol);
        Assert.Equal(ShareStatus.Enabled, s.status);
        Assert.Equal(NfsPermission.Rw, s.nfsPermission);
        Assert.Equal(65534, s.anonymousUid);
        Assert.Equal(-2, s.anonymousGid);
        Assert.True(s.enableUnmappedAccess);
        Assert.False(s.allowRootAccess);
        Assert.Equal(new[] { NfsAuthMethod.Krb5, NfsAuthMethod.Sys }, s.authentication);
    }

    // [Source: nfsAdapter.ts#listShares —— 单对象输出归一为一条（Array.isArray ? raw : [raw]）]
    [Fact]
    public async Task ListShares_SingleObjectIsNormalized()
    {
        var ps = new PsRecorder().WhenJson(ShareReadback);
        IReadOnlyList<Share> shares = await Make(ps).ListSharesAsync();
        Assert.Single(shares);
        Assert.Equal(NfsPermission.Rw, shares[0].nfsPermission);
    }

    // [Source: nfsAdapter.ts#createShare —— 参数拼接顺序 + addJournal(create) + 回读映射]
    [Fact]
    public async Task CreateShare_BuildsNewNfsShareCommandAndJournals()
    {
        var ps = new PsRecorder().WhenJson(ShareReadback);
        FakeStateStore store = new();
        Share share = await Make(ps, store: store).CreateShareAsync(new CreateShareInput
        {
            protocol = Protocol.Nfs,
            name = "nfs1",
            path = @"C:\data",
            authentication = new[] { NfsAuthMethod.Krb5, NfsAuthMethod.Sys },
            nfsPermission = NfsPermission.Rw,
            enableUnmappedAccess = true,
            allowRootAccess = false,
            anonymousUid = 65534,
            anonymousGid = -2,
        });

        Assert.Equal(
            @"New-NfsShare -Name 'nfs1' -Path 'C:\data' -Authentication krb5,sys -Permission rw -EnableUnmappedAccess $true -AllowRootAccess $false -AnonymousUid 65534 -AnonymousGid -2",
            ps.VoidCalls[0].Command);
        Assert.Equal(2, ps.VoidCalls[0].Retries);
        Assert.Equal("Get-NfsShare -Name 'nfs1'", ps.JsonCalls[0].Command);
        Assert.Equal(JournalAction.Create, store.Journal[0].action);
        Assert.Equal(Protocol.Nfs, store.Journal[0].protocol);
        Assert.Equal("nfs1", store.Journal[0].name);
        Assert.Equal(@"路径 C:\data", store.Journal[0].detail);
        Assert.False(store.Journal[0].undoable);
        Assert.Equal("nfs1", share.name);
    }

    // [Source: nfsAdapter.ts#createShare —— 认证未指定时回落 -Authentication sys]
    [Fact]
    public async Task CreateShare_DefaultsAuthenticationToSys()
    {
        var ps = new PsRecorder().WhenJson(ShareReadback);
        await Make(ps).CreateShareAsync(new CreateShareInput { name = "nfs1", path = @"C:\data" });
        Assert.Equal(
            @"New-NfsShare -Name 'nfs1' -Path 'C:\data' -Authentication sys -Permission rw",
            ps.VoidCalls[0].Command);
    }

    // [Source: nfsAdapter.ts#createShare —— validateName/validatePath 前置校验（不触发任何 PS 调用）]
    [Fact]
    public async Task CreateShare_InvalidName_ThrowsWithoutPsCall()
    {
        var ps = new PsRecorder();
        AppException ex = await Assert.ThrowsAsync<AppException>(() =>
            Make(ps).CreateShareAsync(new CreateShareInput { name = "bad$name", path = @"C:\data" }));
        Assert.Equal("INVALID_PARAM", ex.Code);
        Assert.Empty(ps.VoidCalls);
        Assert.Empty(ps.JsonCalls);
    }

    // [Source: nfsAdapter.ts#createShare —— 回读判空 → 孤儿清理 + 重抛 commandFailed]
    [Fact]
    public async Task CreateShare_ReadbackEmpty_ThrowsAndCleansOrphan()
    {
        var ps = new PsRecorder().WhenVoidOk().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() =>
            Make(ps).CreateShareAsync(new CreateShareInput { name = "nfs1", path = @"C:\data" }));
        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("未能回读到该共享", ex.Message);
        Assert.Equal(
            "try { Remove-NfsShare -Name 'nfs1' -Force -ErrorAction Stop } catch {}",
            ps.VoidCalls[1].Command);
        Assert.Equal(0, ps.VoidCalls[1].Retries);
        Assert.StartsWith("try { Get-NfsShare -Name 'nfs1' -ErrorAction Stop", ps.JsonCalls[1].Command);
    }

    // [Source: nfsAdapter.ts#deleteShare —— Remove-NfsShare -Force + addJournal(delete, 不可撤销)]
    [Fact]
    public async Task DeleteShare_SendsRemoveAndJournals()
    {
        var ps = new PsRecorder();
        FakeStateStore store = new();
        await Make(ps, store: store).DeleteShareAsync("nfs1");

        Assert.Equal("Remove-NfsShare -Name 'nfs1' -Force", ps.LastVoid.Command);
        Assert.Equal(2, ps.LastVoid.Retries);
        Assert.Equal(JournalAction.Delete, store.Journal[0].action);
        Assert.Contains("不支持一键撤销", store.Journal[0].detail);
        Assert.False(store.Journal[0].undoable);
    }

    // [Source: nfsAdapter.ts#updateShare —— Set-NfsShare 参数只拼已提供项 + -Confirm:$false]
    [Fact]
    public async Task UpdateShare_BuildsSetCommandAndReadsBack()
    {
        var ps = new PsRecorder().WhenJson(ShareReadback);
        Share share = await Make(ps).UpdateShareAsync("nfs1", new UpdateShareInput
        {
            protocol = Protocol.Nfs,
            nfsPermission = NfsPermission.Ro,
        });

        Assert.Equal("Set-NfsShare -Name 'nfs1' -Permission ro -Confirm:$false", ps.VoidCalls[0].Command);
        Assert.Equal("Get-NfsShare -Name 'nfs1'", ps.JsonCalls[0].Command);
        Assert.Equal("nfs1", share.name);
    }

    // [Source: nfsAdapter.ts#updateShare —— allowRootAccess/enableUnmappedAccess 经 psBool 拼入]
    [Fact]
    public async Task UpdateShare_IncludesBoolFlags()
    {
        var ps = new PsRecorder().WhenJson(ShareReadback);
        await Make(ps).UpdateShareAsync("nfs1", new UpdateShareInput
        {
            allowRootAccess = true,
            enableUnmappedAccess = false,
        });
        Assert.Equal(
            "Set-NfsShare -Name 'nfs1' -AllowRootAccess $true -EnableUnmappedAccess $false -Confirm:$false",
            ps.VoidCalls[0].Command);
    }

    // [Source: nfsAdapter.ts#getPermissions —— Get-NfsSharePermission + rw→Change / Type=Deny]
    [Fact]
    public async Task GetPermissions_MapsPermissionAndType()
    {
        var ps = new PsRecorder().WhenJson(
            @"[{""ClientName"":""10.0.0.1"",""Permission"":""rw"",""Type"":""Allow""},{""ClientName"":""grp1"",""Permission"":""ro"",""Type"":""Deny""}]");
        IReadOnlyList<SharePermission> perms = await Make(ps).GetPermissionsAsync("nfs1");

        Assert.Equal("Get-NfsSharePermission -Name 'nfs1'", ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Equal(2, perms.Count);
        Assert.Equal("10.0.0.1", perms[0].account);
        Assert.Equal(AccountType.Group, perms[0].accountType);
        Assert.Equal(ShareAccess.Change, perms[0].access);
        Assert.False(perms[0].deny);
        Assert.Equal(ShareAccess.Read, perms[1].access);
        Assert.True(perms[1].deny);
        Assert.Equal("nfs1", perms[1].shareName);
    }

    // [Source: nfsAdapter.ts#setPermissions —— 先备份、清空已有、再逐条 Grant]
    [Fact]
    public async Task SetPermissions_ClearsThenGrants()
    {
        var ps = new PsRecorder()
            .WhenJson(@"[{""ClientName"":""old"",""Permission"":""rw"",""Type"":""Allow""}]")
            .WhenJson(@"[{""ClientName"":""old"",""Permission"":""rw"",""Type"":""Allow""}]");
        FakeStateStore store = new();
        await Make(ps, store: store).SetPermissionsAsync("nfs1", new[]
        {
            new SharePermission { shareName = "nfs1", account = "new", accountType = AccountType.User, access = ShareAccess.Read },
        });

        Assert.Equal(2, ps.JsonCalls.Count);
        Assert.Equal(2, ps.VoidCalls.Count);
        Assert.Equal("Revoke-NfsSharePermission -Name 'nfs1' -ClientName 'old' -Confirm:$false", ps.VoidCalls[0].Command);
        Assert.Equal(
            "Grant-NfsSharePermission -Name 'nfs1' -ClientName 'new' -Permission ro -Type Allow -Confirm:$false",
            ps.VoidCalls[1].Command);
        // 权限设置不属于 create/delete 留档动作
        Assert.Empty(store.Journal);
    }

    // [Source: nfsAdapter.ts#setPermissions —— NoAccess 跳过授予；Full/Change → rw, deny → Type Deny]
    [Fact]
    public async Task SetPermissions_SkipsNoAccessAndMapsDeny()
    {
        var ps = new PsRecorder().WhenJson("[]").WhenJson("[]");
        await Make(ps).SetPermissionsAsync("nfs1", new[]
        {
            new SharePermission { account = "skip", access = ShareAccess.NoAccess },
            new SharePermission { account = "adm", accountType = AccountType.Group, access = ShareAccess.Full, deny = true },
        });

        Assert.Single(ps.VoidCalls);
        Assert.Equal(
            "Grant-NfsSharePermission -Name 'nfs1' -ClientName 'adm' -Permission rw -Type Deny -Confirm:$false",
            ps.VoidCalls[0].Command);
    }

    // [Source: nfsAdapter.ts#setPermissions —— 授予失败 → 回滚到备份 + commandFailed]
    [Fact]
    public async Task SetPermissions_GrantFailure_RollsBackAndThrows()
    {
        var ps = new PsRecorder()
            .WhenJson(@"[{""ClientName"":""old"",""Permission"":""rw"",""Type"":""Allow""}]") // 备份
            .WhenJson("[]") // 清空已有
            .WhenJson("[]") // 回滚前状态
            .WhenJson("[]") // 回滚清空
            .WhenJson("[]"); // 回滚后状态
        ps.WhenVoidFails(new Exception("拒绝访问"));
        ps.WhenVoidOk();

        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).SetPermissionsAsync("nfs1", new[]
        {
            new SharePermission { account = "new", access = ShareAccess.Change },
        }));

        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("已回滚到原始状态", ex.Message);
        Assert.Contains("new", ex.Message);
        Assert.Equal(
            "Grant-NfsSharePermission -Name 'nfs1' -ClientName 'old' -Permission rw -Type Allow -Confirm:$false",
            ps.VoidCalls[1].Command);
    }

    // [Source: nfsAdapter.ts#listSessions —— Get-NfsSession + Client {ClientId} 映射]
    [Fact]
    public async Task ListSessions_MapsClientId()
    {
        var ps = new PsRecorder().WhenJson(
            @"[{""SessionId"":""s1"",""NetworkName"":""10.0.0.9"",""State"":1,""ClientId"":7}]");
        IReadOnlyList<ProtocolSession> sessions = await Make(ps).ListSessionsAsync();

        Assert.Equal("Get-NfsSession", ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Single(sessions);
        Assert.Equal(Protocol.Nfs, sessions[0].protocol);
        Assert.Equal("s1", sessions[0].sessionId);
        Assert.Equal("Client 7", sessions[0].clientUserName);
        Assert.Equal("10.0.0.9", sessions[0].clientComputerName);
        Assert.Equal("", sessions[0].sessionStartTime);
        Assert.Equal(0, sessions[0].clientOpenFiles);
        Assert.Equal(0L, sessions[0].bytesSent);
    }

    // [Source: nfsAdapter.ts#closeSession —— Disconnect-NfsSession -SessionId]
    [Fact]
    public async Task CloseSession_SendsDisconnect()
    {
        var ps = new PsRecorder();
        await Make(ps).CloseSessionAsync("s1");
        Assert.Equal("Disconnect-NfsSession -SessionId 's1' -Confirm:$false", ps.LastVoid.Command);
        Assert.Equal(2, ps.LastVoid.Retries);
    }

    // [Source: nfsAdapter.ts#toggleShare / listOpenFiles / closeFile —— TS 未实现（能力位 false）]
    [Fact]
    public async Task UnsupportedMembers_ThrowNotSupported()
    {
        NfsAdapter a = Make(new PsRecorder());
        await Assert.ThrowsAsync<NotSupportedException>(() => a.ToggleShareAsync("nfs1", true));
        await Assert.ThrowsAsync<NotSupportedException>(() => a.ListOpenFilesAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => a.CloseFileAsync("f1"));
    }

    // [Source: nfsAdapter.ts#getServiceStatus/restartService/getConfig/setConfig/defaultConfig/restoreDefault —— 纯委托 nfs.*]
    [Fact]
    public async Task ServiceConfigMethods_DelegateToNfsService()
    {
        var ps = new PsRecorder();
        FakeNfsService nfs = new();
        NfsAdapter a = Make(ps, nfs);

        ServiceStatus status = await a.GetServiceStatusAsync();
        Assert.Equal("NfsService", status.name);
        Assert.Equal(ServiceState.Running, status.status);

        await a.RestartServiceAsync();
        Assert.Equal(1, nfs.RestartCalls);

        Assert.Equal("3.0", ((NfsServerConfig?)await a.GetConfigAsync())?.protocolVersion);

        NfsServerConfig cfg = new() { logActivity = true };
        await a.SetConfigAsync(cfg);
        Assert.Same(cfg, nfs.AppliedConfig);

        Assert.Equal("4.1", ((NfsServerConfig?)a.DefaultConfig())?.protocolVersion);
        Assert.True(((NfsServerConfig?)await a.RestoreDefaultAsync())?.logActivity);

        // 服务配置面不得自带 PS 命令串
        Assert.Empty(ps.JsonCalls);
        Assert.Empty(ps.VoidCalls);
    }

    // [Source: nfsAdapter.ts#setConfig —— 配置对象类型不匹配 → invalidParam]
    [Fact]
    public async Task SetConfig_WrongType_Throws()
    {
        AppException ex = await Assert.ThrowsAsync<AppException>(() =>
            Make(new PsRecorder()).SetConfigAsync(new FtpServerConfig()));
        Assert.Equal("INVALID_PARAM", ex.Code);
    }
}
