// electron/services/nfs.ts 无对应 TS 单测（TS 侧只有协议适配器测试），故按任务书要求
// 以假 IPsInvoker 断言命令串 + 默认值/回读/恒真回落语义；写域（Set-NfsServerConfiguration）全部干跑。
using WinShare.Core.Contracts;
using WinShare.Core.Services.ProtocolServices;

namespace WinShare.Core.Tests.Services.ProtocolServices;

public class NfsServiceTests
{
    // TS: getNfsServiceName 的探测命令（逐字）
    private const string NameProbe =
        "@('NfsService','NfsClnt') | ForEach-Object { Get-Service -Name $_ -ErrorAction SilentlyContinue } | " +
        "Where-Object { $_ } | Select-Object -First 1 -ExpandProperty Name";

    // ===== defaultConfig =====

    // [Source: nfs.ts#defaultConfig（协议未装或查询失败时让 Settings 表单可渲染）]
    [Fact]
    public void DefaultConfig_MatchesTsDefaults()
    {
        NfsServerConfig c = new NfsService(new PsRecorder()).DefaultConfig();
        Assert.False(c.gracefulUnmount);
        Assert.False(c.logActivity);
        Assert.False(c.enableUnmappedAccess);
        Assert.False(c.enableAuthenticationRenegotiation);
        Assert.Equal("ANSI", c.gatewayCharacterSet);
        Assert.Equal("4.1", c.protocolVersion);
        Assert.Equal(240, c.tcpConnectionTimeout);
        Assert.Equal(240, c.udpConnectionTimeout);
        Assert.Equal(60, c.restartConnectionTimeout);
        Assert.Equal(0, c.maxConcurrentConnectionsPerUser);
        Assert.Equal(60, c.directoryCacheExpiry);
        Assert.Equal(-2, c.anonymousUid);
        Assert.Equal(-2, c.anonymousGid);
    }

    // ===== getConfig =====

    // [Source: nfs.ts#getConfig —— retries:0（未装 NFS 时避免无谓重试造成切 Tab 延迟）]
    [Fact]
    public async Task GetConfig_UsesSingleProbeWithNoRetries()
    {
        var ps = new PsRecorder().WhenJson("{}");
        await new NfsService(ps).GetConfigAsync();
        Assert.Single(ps.JsonCalls);
        Assert.Equal("Get-NfsServerConfiguration", ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Equal(15000, ps.JsonCalls[0].TimeoutMs);
    }

    // [Source: nfs.ts#getConfig —— 字段映射（布尔 !!、字符串 ||、数值 !== undefined）]
    [Fact]
    public async Task GetConfig_ParsesRawFields()
    {
        var ps = new PsRecorder().WhenJson("""
        {"GracefulUnmount":true,"LogActivity":true,"EnableUnmappedAccess":true,
         "EnableAuthenticationRenegotiation":true,"GatewayCharacterSet":"UTF-8","ProtocolVersion":"3",
         "TcpConnectionTimeout":120,"UdpConnectionTimeout":121,"RestartConnectionTimeout":30,
         "MaxConcurrentConnectionsPerUser":5,"DirectoryCacheExpiry":15,"AnonymousUid":-1,"AnonymousGid":-3}
        """);
        NfsServerConfig c = await new NfsService(ps).GetConfigAsync();
        Assert.True(c.gracefulUnmount);
        Assert.True(c.logActivity);
        Assert.True(c.enableUnmappedAccess);
        Assert.True(c.enableAuthenticationRenegotiation);
        Assert.Equal("UTF-8", c.gatewayCharacterSet);
        Assert.Equal("3", c.protocolVersion);
        Assert.Equal(120, c.tcpConnectionTimeout);
        Assert.Equal(121, c.udpConnectionTimeout);
        Assert.Equal(30, c.restartConnectionTimeout);
        Assert.Equal(5, c.maxConcurrentConnectionsPerUser);
        Assert.Equal(15, c.directoryCacheExpiry);
        Assert.Equal(-1, c.anonymousUid);
        Assert.Equal(-3, c.anonymousGid);
    }

    // [Source: nfs.ts#getConfig —— 旧版 Windows 无字段时降级默认值；0 是有效值不被 || 吞掉]
    [Fact]
    public async Task GetConfig_MissingFieldsFallBackButZeroIsKept()
    {
        var ps = new PsRecorder().WhenJson(
            "{\"GracefulUnmount\":false,\"TcpConnectionTimeout\":0,\"AnonymousUid\":0}");
        NfsServerConfig c = await new NfsService(ps).GetConfigAsync();
        Assert.False(c.gracefulUnmount);
        Assert.Equal(0, c.tcpConnectionTimeout);          // !== undefined → 保留 0
        Assert.Equal(0, c.anonymousUid);
        Assert.Equal(240, c.udpConnectionTimeout);         // 缺失 → 默认
        Assert.Equal(60, c.restartConnectionTimeout);
        Assert.Equal(-2, c.anonymousGid);
        Assert.Equal("ANSI", c.gatewayCharacterSet);
        Assert.Equal("4.1", c.protocolVersion);
    }

    // [Source: nfs.ts#getConfig —— raw.GatewayCharacterSet || 'ANSI'（空串回落）]
    [Fact]
    public async Task GetConfig_EmptyCharsetAndVersionFallBackToDefaults()
    {
        var ps = new PsRecorder().WhenJson("{\"GatewayCharacterSet\":\"\",\"ProtocolVersion\":\"\"}");
        NfsServerConfig c = await new NfsService(ps).GetConfigAsync();
        Assert.Equal("ANSI", c.gatewayCharacterSet);
        Assert.Equal("4.1", c.protocolVersion);
    }

    // [Source: nfs.ts#getConfig —— NFS 未装：返回默认配置，避免 Settings Tab 弹错]
    [Fact]
    public async Task GetConfig_CommandFails_ReturnsDefaults()
    {
        var ps = new PsRecorder().WhenJsonFails(new Exception("命令执行失败：不存在"));
        NfsServerConfig c = await new NfsService(ps).GetConfigAsync();
        Assert.Equal("ANSI", c.gatewayCharacterSet);
        Assert.Equal(240, c.tcpConnectionTimeout);
    }

    // ===== setConfig =====

    // [Source: nfs.ts#setConfig —— 命令串逐字（全字段提供 → 5 布尔 + 5 数值 + -Confirm:$false）]
    [Fact]
    public async Task SetConfig_Defaults_BuildsExactCommand()
    {
        var ps = new PsRecorder();
        var svc = new NfsService(ps);
        await svc.SetConfigAsync(svc.DefaultConfig());

        Assert.Single(ps.VoidCalls);
        Assert.Equal(
            "Set-NfsServerConfiguration -GracefulUnmount $false -LogActivity $false " +
            "-EnableUnmappedAccess $false -EnableAuthenticationRenegotiation $false " +
            "-TcpConnectionTimeout 240 -UdpConnectionTimeout 240 -RestartConnectionTimeout 60 " +
            "-MaxConcurrentConnectionsPerUser 0 -DirectoryCacheExpiry 60 -Confirm:$false",
            ps.LastVoid.Command);
        Assert.Equal(2, ps.LastVoid.Retries);
        Assert.Equal(15000, ps.LastVoid.TimeoutMs);
    }

    // [Source: nfs.ts#setConfig —— 刻意不写回只读字段（Set-NfsServerConfiguration 亦不接受）]
    [Fact]
    public async Task SetConfig_OmitsReadOnlyFields()
    {
        var ps = new PsRecorder();
        var svc = new NfsService(ps);
        await svc.SetConfigAsync(svc.DefaultConfig());

        string cmd = ps.LastVoid.Command;
        Assert.DoesNotContain("GatewayCharacterSet", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtocolVersion", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("AnonymousUid", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("AnonymousGid", cmd, StringComparison.Ordinal);
    }

    // [Source: nfs.ts#setConfig —— 0 是有效配置值（TS 侧 '0' 为 truthy，照写）]
    [Fact]
    public async Task SetConfig_ZeroAndTrueAreWrittenThrough()
    {
        var ps = new PsRecorder();
        await new NfsService(ps).SetConfigAsync(new NfsConfigPatch
        {
            gracefulUnmount = true,
            maxConcurrentConnectionsPerUser = 0,
            directoryCacheExpiry = 15,
        });
        Assert.Equal(
            "Set-NfsServerConfiguration -GracefulUnmount $true -MaxConcurrentConnectionsPerUser 0 " +
            "-DirectoryCacheExpiry 15 -Confirm:$false",
            ps.LastVoid.Command);
    }

    // [Source: nfs.ts#setConfig —— 未提供任何配置项 → Errors.invalidParam]
    [Fact]
    public async Task SetConfig_NoFields_ThrowsInvalidParam()
    {
        var ps = new PsRecorder();
        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => new NfsService(ps).SetConfigAsync(new NfsConfigPatch()));
        Assert.Equal("INVALID_PARAM", ex.Code);
        Assert.Equal("未提供任何配置项", ex.Message);
        Assert.Empty(ps.VoidCalls);
    }

    // [Source: nfs.ts#restoreDefault —— 写默认配置并回传默认值]
    [Fact]
    public async Task RestoreDefault_WritesDefaultsAndReturnsThem()
    {
        var ps = new PsRecorder();
        var svc = new NfsService(ps);
        NfsServerConfig c = await svc.RestoreDefaultAsync();

        Assert.Equal("4.1", c.protocolVersion);
        Assert.Equal(-2, c.anonymousUid);
        Assert.Single(ps.VoidCalls);
        Assert.Contains("-GracefulUnmount $false", ps.LastVoid.Command, StringComparison.Ordinal);
    }

    // ===== getServiceStatus =====

    // [Source: nfs.ts#getServiceStatus —— 服务名探测器 + 状态查询（默认重试 2 次）]
    [Fact]
    public async Task GetServiceStatus_ReadsDetectedServiceAndNumericEnums()
    {
        var ps = new PsRecorder()
            .WhenJson("\"NfsService\"")
            .WhenJson("{\"Name\":\"NfsService\",\"Status\":4,\"StartType\":2}");
        ServiceStatus st = await new NfsService(ps).GetServiceStatusAsync();

        Assert.Equal("NfsService", st.name);
        Assert.Equal(ServiceState.Running, st.status);
        Assert.Equal("Automatic", st.startType);

        Assert.Equal(NameProbe, ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Equal(
            "Get-Service NfsService -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType",
            ps.JsonCalls[1].Command);
        Assert.Equal(2, ps.JsonCalls[1].Retries);
    }

    // [Source: nfs.ts#getServiceStatus —— Win10/11 客户端服务名 NfsClnt 同样可识别]
    [Fact]
    public async Task GetServiceStatus_UsesClientServiceNameWhenServerRoleAbsent()
    {
        var ps = new PsRecorder()
            .WhenJson("\"NfsClnt\"")
            .WhenJson("{\"Name\":\"NfsClnt\",\"Status\":1,\"StartType\":3}");
        ServiceStatus st = await new NfsService(ps).GetServiceStatusAsync();

        Assert.Equal("NfsClnt", st.name);
        Assert.Equal(ServiceState.Stopped, st.status);
        Assert.Equal("Manual", st.startType);
        Assert.StartsWith("Get-Service NfsClnt ", ps.JsonCalls[1].Command, StringComparison.Ordinal);
    }

    // [Source: nfs.ts#getServiceStatus —— 未装：name 回落 'NFS'，状态 Unknown]
    [Fact]
    public async Task GetServiceStatus_NotInstalled_ReturnsUnknown()
    {
        var ps = new PsRecorder().WhenJson("[]");
        ServiceStatus st = await new NfsService(ps).GetServiceStatusAsync();

        Assert.Equal("NFS", st.name);
        Assert.Equal(ServiceState.Unknown, st.status);
        Assert.Equal("Unknown", st.startType);
        Assert.Single(ps.JsonCalls); // 探测已失败，不再发状态查询
    }

    // [Source: nfs.ts#getServiceStatus —— TS: statusStr==='Running' 分支优先于数值]
    [Fact]
    public async Task GetServiceStatus_StringStatusAndUnknownStartType()
    {
        var ps = new PsRecorder()
            .WhenJson("\"NfsService\"")
            .WhenJson("{\"Name\":\"NfsService\",\"Status\":\"Running\",\"StartType\":9}");
        ServiceStatus st = await new NfsService(ps).GetServiceStatusAsync();
        Assert.Equal(ServiceState.Running, st.status);
        Assert.Equal("Unknown", st.startType); // 4 档都不匹配 → 'Unknown'
    }

    // [Source: nfs.ts#getServiceStatus —— 状态查询失败 → Unknown（不抛出）]
    [Fact]
    public async Task GetServiceStatus_QueryFails_ReturnsUnknown()
    {
        var ps = new PsRecorder()
            .WhenJson("\"NfsService\"")
            .WhenJsonFails(new Exception("命令执行超时，请检查系统响应或稍后重试"));
        ServiceStatus st = await new NfsService(ps).GetServiceStatusAsync();
        Assert.Equal("NfsService", st.name);
        Assert.Equal(ServiceState.Unknown, st.status);
    }

    // ===== 服务控制 =====

    // [Source: nfs.ts#restartService —— 服务名探测 + Restart-Service（retries:0）]
    [Fact]
    public async Task RestartService_BuildsCommand()
    {
        var ps = new PsRecorder().WhenJson("\"NfsService\"");
        await new NfsService(ps).RestartServiceAsync();
        PsCall call = ps.LastVoid;
        Assert.Equal("Restart-Service -Name NfsService -Force -ErrorAction Stop", call.Command);
        Assert.Equal(0, call.Retries);
    }

    // [Source: nfs.ts#restartService —— 未装 NFS → 引导安装文案]
    [Fact]
    public async Task RestartService_NotInstalled_ThrowsGuidance()
    {
        var ps = new PsRecorder().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() => new NfsService(ps).RestartServiceAsync());
        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("NFS 服务未安装", ex.Message, StringComparison.Ordinal);
        Assert.Empty(ps.VoidCalls);
    }

    // [Source: nfs.ts#restartService —— /access is denied|拒绝访问|administrator|权限/i → 管理员提示]
    [Fact]
    public async Task RestartService_AccessDenied_MapsToAdminHint()
    {
        var ps = new PsRecorder()
            .WhenJson("\"NfsService\"")
            .WhenVoidFails(new Exception("命令执行失败：Access is denied"));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => new NfsService(ps).RestartServiceAsync());
        Assert.Equal("重启 NFS 服务需要管理员权限，请以管理员身份运行 WinShare Panel", ex.Message);
    }

    // [Source: nfs.ts#restartService —— 其它失败：'重启 NFS 服务失败：' + msg.slice(0,200)]
    [Fact]
    public async Task RestartService_OtherFailure_TruncatesMessageTo200()
    {
        string raw = new string('x', 500);
        var ps = new PsRecorder().WhenJson("\"NfsService\"").WhenVoidFails(new Exception(raw));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => new NfsService(ps).RestartServiceAsync());
        Assert.Equal("重启 NFS 服务失败：" + new string('x', 200), ex.Message);
    }

    // [Source: nfs.ts#startService / stopService —— 命令串逐字 + 未装引导]
    [Fact]
    public async Task StartAndStopService_BuildCommands()
    {
        var ps = new PsRecorder().WhenJson("\"NfsService\"").WhenJson("\"NfsService\"");
        var svc = new NfsService(ps);
        await svc.StartServiceAsync();
        await svc.StopServiceAsync();

        Assert.Equal("Start-Service -Name NfsService -ErrorAction Stop", ps.VoidCalls[0].Command);
        Assert.Equal(0, ps.VoidCalls[0].Retries);
        Assert.Equal("Stop-Service -Name NfsService -Force -ErrorAction Stop", ps.VoidCalls[1].Command);
    }

    // [Source: nfs.ts#startService —— 失败文案前缀]
    [Fact]
    public async Task StartService_Failure_WrapsMessage()
    {
        var ps = new PsRecorder().WhenJson("\"NfsService\"").WhenVoidFails(new Exception("拒绝访问"));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => new NfsService(ps).StartServiceAsync());
        Assert.Equal("启动 NFS 服务失败：拒绝访问", ex.Message);
    }

    // [Source: nfs.ts#stopService —— 未装 → 'NFS 服务未安装，请先安装 NFS 角色']
    [Fact]
    public async Task StopService_NotInstalled_Throws()
    {
        var ps = new PsRecorder().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() => new NfsService(ps).StopServiceAsync());
        Assert.Equal("NFS 服务未安装，请先安装 NFS 角色", ex.Message);
    }
}
