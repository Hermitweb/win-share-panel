// electron/services/ftp.ts 无对应 TS 单测，故以假 IPsInvoker 断言命令串（IIS 写域全部干跑）
// + 字段表驱动的回读/写入语义（布尔 !!、数值 Number(v)||默认、字符串/枚举 String(v)）。
using WinShare.Core.Contracts;
using WinShare.Core.Services.ProtocolServices;

namespace WinShare.Core.Tests.Services.ProtocolServices;

public class FtpServiceTests
{
    private const string ServiceName = "ftpsvc";
    private const string AppCmd = @"$env:windir\system32\inetsrv\appcmd.exe";

    // TS: restartService 的整段脚本（含依赖服务兜底），逐字
    private const string RestartCommand =
        "try { Restart-Service -Name ftpsvc -Force -ErrorAction Stop } catch { if ($_.Exception.Message -match " +
        "'Cannot stop|无法停止|dependent|依赖') { Start-Service -Name ftpsvc -ErrorAction SilentlyContinue; " +
        "if ((Get-Service ftpsvc).Status -ne 'Running') { throw } } else { throw } }";

    private static FtpService Make(PsRecorder ps) => new(ps);

    // ===== defaultConfig =====

    // [Source: ftp.ts#defaultConfig（IIS FTP 7.5+ 默认配置）]
    [Fact]
    public void DefaultConfig_MatchesTsDefaults()
    {
        FtpServerConfig c = Make(new PsRecorder()).DefaultConfig();
        Assert.Equal(SslPolicy.SslAllow, c.sslControlChannelPolicy);
        Assert.Equal(SslPolicy.SslAllow, c.sslDataChannelPolicy);
        Assert.Equal("", c.sslServerCertHash);
        Assert.False(c.sslClientCertRequired);
        Assert.False(c.ssl128);
        Assert.False(c.anonymousEnabled);
        Assert.Equal("IUSR", c.anonymousUserName);
        Assert.False(c.basicEnabled);
        Assert.Equal(0, c.firewallLowDataChannelPort);
        Assert.Equal(0, c.firewallHighDataChannelPort);
        Assert.Equal("", c.greetingMessage);
        Assert.Equal("", c.bannerMessage);
        Assert.Equal("", c.exitMessage);
        Assert.Equal("", c.maxClientsMessage);
        Assert.False(c.suppressDefaultMessages);
        Assert.False(c.showVirtualDirs);
        Assert.Equal(FtpUserIsolationMode.None, c.userIsolationMode);
        Assert.Equal(30, c.unauthenticatedTimeout);
        Assert.Equal(300, c.controlConnectionTimeout);
        Assert.Equal(30, c.dataChannelConnectionTimeout);
        Assert.False(c.keepPartialUploads);
        Assert.False(c.allowReplaceOnRename);
        Assert.Equal(@"%SystemDrive%\inetpub\logs\LogFiles", c.logFileDirectory);
        Assert.Equal(FtpLogPeriod.Daily, c.logFilePeriod);
    }

    // ===== getServiceStatus =====

    // [Source: ftp.ts#getServiceStatus —— 命令串逐字 + 默认重试 2 次]
    [Fact]
    public async Task GetServiceStatus_QueriesFtpsvc()
    {
        var ps = new PsRecorder().WhenJson("{\"Name\":\"ftpsvc\",\"Status\":4,\"StartType\":2}");
        ServiceStatus st = await Make(ps).GetServiceStatusAsync();

        Assert.Equal("ftpsvc", st.name);
        Assert.Equal(ServiceState.Running, st.status);
        Assert.Equal("Automatic", st.startType);
        Assert.Equal(
            "Get-Service ftpsvc -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType",
            ps.JsonCalls[0].Command);
        Assert.Equal(2, ps.JsonCalls[0].Retries);
        Assert.Equal(15000, ps.JsonCalls[0].TimeoutMs);
    }

    // [Source: ftp.ts#getServiceStatus —— 未装/查询失败均回落 ftpsvc + Unknown]
    [Fact]
    public async Task GetServiceStatus_NotInstalled_ReturnsUnknown()
    {
        var ps = new PsRecorder().WhenJson("[]");
        ServiceStatus st = await Make(ps).GetServiceStatusAsync();
        Assert.Equal("ftpsvc", st.name);
        Assert.Equal(ServiceState.Unknown, st.status);
        Assert.Equal("Unknown", st.startType);
    }

    // [Source: ftp.ts#getServiceStatus —— 命令抛错同样回落（catch → Unknown）]
    [Fact]
    public async Task GetServiceStatus_QueryFails_ReturnsUnknown()
    {
        var ps = new PsRecorder().WhenJsonFails(new Exception("命令执行失败：拒绝访问"));
        ServiceStatus st = await Make(ps).GetServiceStatusAsync();
        Assert.Equal("ftpsvc", st.name);
        Assert.Equal(ServiceState.Unknown, st.status);
    }

    // ===== 服务控制命令 =====

    // [Source: ftp.ts#restartService —— 先探测存在性（retries:0），再执行依赖兜底脚本]
    [Fact]
    public async Task RestartService_ProbesExistenceThenRunsGuardedRestart()
    {
        var ps = new PsRecorder().WhenJson("{\"Name\":\"ftpsvc\"}");
        await Make(ps).RestartServiceAsync();

        Assert.Equal(
            "Get-Service ftpsvc -ErrorAction SilentlyContinue | Select-Object Name",
            ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Equal(RestartCommand, ps.LastVoid.Command);
        Assert.Equal(0, ps.LastVoid.Retries);
    }

    // [Source: ftp.ts#restartService —— 未装 → 引导安装 FTP 角色服务]
    [Fact]
    public async Task RestartService_NotInstalled_ThrowsGuidance()
    {
        var ps = new PsRecorder().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).RestartServiceAsync());
        Assert.Contains("ftpsvc 服务未安装", ex.Message, StringComparison.Ordinal);
        Assert.Empty(ps.VoidCalls);
    }

    // [Source: ftp.ts#restartService —— 权限分支]
    [Fact]
    public async Task RestartService_AccessDenied_MapsToAdminHint()
    {
        var ps = new PsRecorder().WhenJson("{\"Name\":\"ftpsvc\"}").WhenVoidFails(new Exception("Access is denied"));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).RestartServiceAsync());
        Assert.Equal("重启 ftpsvc 服务需要管理员权限，请以管理员身份运行 WinShare Panel", ex.Message);
    }

    // [Source: ftp.ts#restartService —— 依赖服务分支]
    [Fact]
    public async Task RestartService_DependentService_MapsToDependencyHint()
    {
        var ps = new PsRecorder().WhenJson("{\"Name\":\"ftpsvc\"}")
            .WhenVoidFails(new Exception("命令执行失败：无法停止服务"));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).RestartServiceAsync());
        Assert.Equal("ftpsvc 服务存在依赖服务无法重启，请手动停止依赖服务后重试，或重启计算机", ex.Message);
    }

    // [Source: ftp.ts#startService / stopService —— 命令串逐字]
    [Fact]
    public async Task StartAndStopService_BuildCommands()
    {
        var ps = new PsRecorder()
            .WhenJson("{\"Name\":\"ftpsvc\"}")
            .WhenJson("{\"Name\":\"ftpsvc\"}");
        var svc = Make(ps);
        await svc.StartServiceAsync();
        await svc.StopServiceAsync();

        Assert.Equal("Start-Service -Name ftpsvc -ErrorAction Stop", ps.VoidCalls[0].Command);
        Assert.Equal("Stop-Service -Name ftpsvc -Force -ErrorAction Stop", ps.VoidCalls[1].Command);
        Assert.Equal(0, ps.VoidCalls[0].Retries);
    }

    // [Source: ftp.ts#startService —— 未装 → '请先安装 FTP 角色服务']
    [Fact]
    public async Task StartService_NotInstalled_Throws()
    {
        var ps = new PsRecorder().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).StartServiceAsync());
        Assert.Equal("ftpsvc 服务未安装，请先安装 FTP 角色服务", ex.Message);
    }

    // ===== ensureFtpSectionsUnlocked =====

    // [Source: ftp.ts#ensureFtpSectionsUnlocked —— 12 个 ftpServer/* 节逐个 appcmd unlock（timeout 30000）]
    [Fact]
    public async Task EnsureSectionsUnlocked_BuildsAppcmdCommand()
    {
        var ps = new PsRecorder();
        await Make(ps).EnsureFtpSectionsUnlockedAsync();

        PsCall call = ps.LastVoid;
        Assert.Equal(12, PsCount.Of(call.Command, "unlock config /section:"));
        Assert.Equal(12, PsCount.Of(call.Command, "2>&1 | Out-Null } catch {}"));
        Assert.StartsWith(
            "try { & " + AppCmd +
            " unlock config /section:'ftpServer/security/authentication/anonymousAuthentication' 2>&1 | Out-Null } catch {}",
            call.Command, StringComparison.Ordinal);
        Assert.Contains("unlock config /section:'ftpServer/security/ssl'", call.Command, StringComparison.Ordinal);
        Assert.Contains("unlock config /section:'ftpServer/logFile'", call.Command, StringComparison.Ordinal);
        Assert.Equal(30000, call.TimeoutMs);
        Assert.Equal(0, call.Retries);
    }

    // [Source: ftp.ts#ensureFtpSectionsUnlocked —— best-effort：解锁失败不阻断]
    [Fact]
    public async Task EnsureSectionsUnlocked_FailureIsSwallowed()
    {
        var ps = new PsRecorder().WhenVoidFails(new Exception("命令执行失败：拒绝访问"));
        await Make(ps).EnsureFtpSectionsUnlockedAsync(); // 不抛
        Assert.Single(ps.VoidCalls);
    }

    // ===== getConfig =====

    // [Source: ftp.ts#getConfig —— 单次批量读取：Import-Module + $r 累积 24 个配置属性 + 末尾 $r]
    [Fact]
    public async Task GetConfig_BuildsBatchReadCommand()
    {
        var ps = new PsRecorder().WhenJson("{}");
        await Make(ps).GetConfigAsync();

        PsCall call = ps.JsonCalls[0];
        Assert.StartsWith("Import-Module WebAdministration; $r = [PSCustomObject]@{}; ", call.Command, StringComparison.Ordinal);
        Assert.EndsWith("; $r", call.Command, StringComparison.Ordinal);
        Assert.Equal(24, PsCount.Of(call.Command, "Add-Member -MemberType NoteProperty -Name"));
        Assert.Contains(
            "try { $r | Add-Member -MemberType NoteProperty -Name sslControlChannelPolicy -Value " +
            "(Get-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath 'MACHINE/WEBROOT/APPHOST' " +
            "-Name controlChannelPolicy -ErrorAction Stop) } catch {}",
            call.Command, StringComparison.Ordinal);
        Assert.Contains(
            "-Name logFilePeriod -Value (Get-WebConfigurationProperty -Filter 'ftpServer/logFile' ",
            call.Command, StringComparison.Ordinal);
        Assert.Equal(15000, call.TimeoutMs);
        Assert.Equal(0, call.Retries);
    }

    // [Source: ftp.ts#getConfig —— 布尔 !!v / 数值 Number(v)||默认 / 字符串 String(v) / 枚举透传]
    [Fact]
    public async Task GetConfig_ParsesFieldTypes()
    {
        var ps = new PsRecorder().WhenJson("""
        {"sslControlChannelPolicy":"SslRequire","sslServerCertHash":"ABC123",
         "sslClientCertRequired":true,"anonymousUserName":"IUSR2","firewallLowDataChannelPort":5000,
         "suppressDefaultMessages":1,"userIsolationMode":"IsolateUsers","logFilePeriod":"Weekly",
         "controlConnectionTimeout":0}
        """);
        FtpServerConfig c = await Make(ps).GetConfigAsync();

        Assert.Equal(SslPolicy.SslRequire, c.sslControlChannelPolicy);
        Assert.Equal(SslPolicy.SslAllow, c.sslDataChannelPolicy);         // 缺失 → 默认
        Assert.Equal("ABC123", c.sslServerCertHash);
        Assert.True(c.sslClientCertRequired);
        Assert.Equal("IUSR2", c.anonymousUserName);
        Assert.Equal(5000, c.firewallLowDataChannelPort);
        Assert.True(c.suppressDefaultMessages);                          // !!1 → true
        Assert.Equal(FtpUserIsolationMode.IsolateUsers, c.userIsolationMode);
        Assert.Equal(FtpLogPeriod.Weekly, c.logFilePeriod);
        Assert.Equal(300, c.controlConnectionTimeout);                   // Number(0)||默认 → 300
        Assert.Equal(30, c.unauthenticatedTimeout);
        Assert.False(c.basicEnabled);
    }

    // [Source: ftp.ts#getConfig —— 整体失败（ftpsvc 未装 / IIS 未配置）→ 默认配置]
    [Fact]
    public async Task GetConfig_CommandFails_ReturnsDefaults()
    {
        var ps = new PsRecorder().WhenJsonFails(new Exception("命令执行失败：Import-Module 失败"));
        FtpServerConfig c = await Make(ps).GetConfigAsync();
        Assert.Equal(SslPolicy.SslAllow, c.sslControlChannelPolicy);
        Assert.Equal(300, c.controlConnectionTimeout);
        Assert.Equal(@"%SystemDrive%\inetpub\logs\LogFiles", c.logFileDirectory);
    }

    // [Source: ftp.ts#getConfig —— 空结果（$r 无属性）全量回落默认]
    [Fact]
    public async Task GetConfig_EmptyResult_ReturnsDefaults()
    {
        var ps = new PsRecorder().WhenJson("[]");
        FtpServerConfig c = await Make(ps).GetConfigAsync();
        Assert.Equal("IUSR", c.anonymousUserName);
        Assert.Equal(FtpLogPeriod.Daily, c.logFilePeriod);
    }

    // ===== setConfig =====

    // [Source: ftp.ts#setConfig —— 先解锁配置节，再按字段表拼 Set-WebConfigurationProperty（per-field try/catch）]
    [Fact]
    public async Task SetConfig_Partial_WritesOnlyProvidedFields()
    {
        var ps = new PsRecorder();
        await Make(ps).SetConfigAsync(new FtpConfigPatch
        {
            ssl128 = true,
            anonymousUserName = "IUSR",
            firewallHighDataChannelPort = 6000,
        });

        Assert.Equal(2, ps.VoidCalls.Count);           // [0]=解锁，[1]=写入
        Assert.Equal(30000, ps.LastVoid.TimeoutMs);
        Assert.Equal(0, ps.LastVoid.Retries);
        Assert.Equal(3, PsCount.Of(ps.LastVoid.Command, "Set-WebConfigurationProperty"));
        Assert.Equal(
            "try { Set-WebConfigurationProperty -Filter 'ftpServer/security/ssl' " +
            "-PSPath 'MACHINE/WEBROOT/APPHOST' -Name ssl128 -Value $true -ErrorAction Stop } catch {}; " +
            "try { Set-WebConfigurationProperty -Filter " +
            "'ftpServer/security/authentication/anonymousAuthentication' " +
            "-PSPath 'MACHINE/WEBROOT/APPHOST' -Name userName -Value 'IUSR' -ErrorAction Stop } catch {}; " +
            "try { Set-WebConfigurationProperty -Filter 'ftpServer/firewallSupport' " +
            "-PSPath 'MACHINE/WEBROOT/APPHOST' -Name highDataChannelPort -Value 6000 -ErrorAction Stop } catch {}",
            ps.LastVoid.Command);
    }

    // [Source: ftp.ts#setConfig —— 字符串清换行 + 单引号转义；枚举按名写入]
    [Fact]
    public async Task SetConfig_TextCleansNewlinesAndEscapesQuotes()
    {
        var ps = new PsRecorder();
        await Make(ps).SetConfigAsync(new FtpConfigPatch
        {
            greetingMessage = "hello\r\nworld's ftp",
            logFilePeriod = FtpLogPeriod.MaxSize,
        });
        string cmd = ps.LastVoid.Command;
        Assert.Contains("-Name greetingMessage -Value 'hello world''s ftp'", cmd, StringComparison.Ordinal);
        Assert.Contains("-Name period -Value 'MaxSize'", cmd, StringComparison.Ordinal);
    }

    // [Source: ftp.ts#setConfig —— 未提供任何配置项 → Errors.invalidParam]
    [Fact]
    public async Task SetConfig_NoFields_ThrowsInvalidParam()
    {
        var ps = new PsRecorder();
        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => Make(ps).SetConfigAsync(new FtpConfigPatch()));
        Assert.Equal("INVALID_PARAM", ex.Code);
        Assert.Equal("未提供任何配置项", ex.Message);
        Assert.Single(ps.VoidCalls); // 解锁已执行，写入未执行
        Assert.Contains("unlock config", ps.VoidCalls[0].Command, StringComparison.Ordinal);
    }

    // [Source: ftp.ts#restoreDefault —— 写默认配置（全 24 字段）并回传默认值]
    [Fact]
    public async Task RestoreDefault_WritesAllFieldsAndReturnsDefaults()
    {
        var ps = new PsRecorder();
        FtpServerConfig c = await Make(ps).RestoreDefaultAsync();

        Assert.Equal(24, PsCount.Of(ps.LastVoid.Command, "Set-WebConfigurationProperty"));
        Assert.Contains("-Name controlChannelPolicy -Value 'SslAllow'", ps.LastVoid.Command, StringComparison.Ordinal);
        Assert.Contains("-Name mode -Value 'None'", ps.LastVoid.Command, StringComparison.Ordinal);
        Assert.Equal(FtpUserIsolationMode.None, c.userIsolationMode);
        Assert.Equal(FtpLogPeriod.Daily, c.logFilePeriod);
    }
}
