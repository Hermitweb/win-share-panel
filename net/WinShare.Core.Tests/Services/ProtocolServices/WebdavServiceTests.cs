// electron/services/webdav.ts 无对应 TS 单测，故以假 IPsInvoker 断言命令串（IIS 写域全部干跑）
// + 配置节完整路径（短路径解析缺陷的项目记忆约束）+ 只读字段回读语义。
using WinShare.Core.Contracts;
using WinShare.Core.Services.ProtocolServices;

namespace WinShare.Core.Tests.Services.ProtocolServices;

public class WebdavServiceTests
{
    private const string AppCmd = @"$env:windir\system32\inetsrv\appcmd.exe";

    // TS: restartService 的整段脚本（含依赖服务兜底），逐字
    private const string RestartCommand =
        "try { Restart-Service -Name W3SVC -Force -ErrorAction Stop } catch { if ($_.Exception.Message -match " +
        "'Cannot stop|无法停止|dependent|依赖') { Start-Service -Name W3SVC -ErrorAction SilentlyContinue; " +
        "if ((Get-Service W3SVC).Status -ne 'Running') { throw } } else { throw } }";

    private static WebdavService Make(PsRecorder ps) => new(ps);

    // ===== defaultConfig =====

    // [Source: webdav.ts#defaultConfig（IIS WebDAV 默认配置）]
    [Fact]
    public void DefaultConfig_MatchesTsDefaults()
    {
        WebdavServerConfig c = Make(new PsRecorder()).DefaultConfig();
        Assert.False(c.authoringEnabled);
        Assert.Equal(0L, c.authoringMaxRequestBodySize);
        Assert.Equal(30000000L, c.maxAllowedContentLength);
        Assert.False(c.allowDoubleEscaping);
        Assert.True(c.verifyIntegration);
        Assert.False(c.anonymousEnabled);
        Assert.False(c.basicEnabled);
        Assert.True(c.windowsEnabled);
        Assert.Equal(260, c.maxUrlLength);
        Assert.Equal(2048, c.maxQueryStringLength);
        Assert.Equal(0, c.globalAuthoringRulesCount);
        Assert.True(c.enableStaticCompression);
        Assert.False(c.enableDynamicCompression);
        Assert.False(c.requireSSL);
    }

    // ===== getServiceStatus =====

    // [Source: webdav.ts#getServiceStatus —— 服务名 W3SVC，默认重试 2 次]
    [Fact]
    public async Task GetServiceStatus_QueriesW3svc()
    {
        var ps = new PsRecorder().WhenJson("{\"Name\":\"W3SVC\",\"Status\":4,\"StartType\":2}");
        ServiceStatus st = await Make(ps).GetServiceStatusAsync();

        Assert.Equal("W3SVC", st.name);
        Assert.Equal(ServiceState.Running, st.status);
        Assert.Equal("Automatic", st.startType);
        Assert.Equal(
            "Get-Service W3SVC -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType",
            ps.JsonCalls[0].Command);
        Assert.Equal(2, ps.JsonCalls[0].Retries);
    }

    // [Source: webdav.ts#getServiceStatus —— 未装 → W3SVC + Unknown]
    [Fact]
    public async Task GetServiceStatus_NotInstalled_ReturnsUnknown()
    {
        var ps = new PsRecorder().WhenJson("[]");
        ServiceStatus st = await Make(ps).GetServiceStatusAsync();
        Assert.Equal("W3SVC", st.name);
        Assert.Equal(ServiceState.Unknown, st.status);
        Assert.Equal("Unknown", st.startType);
    }

    // ===== 服务控制命令 =====

    // [Source: webdav.ts#restartService —— 先探测存在性（retries:0），再执行依赖兜底脚本]
    [Fact]
    public async Task RestartService_ProbesExistenceThenRunsGuardedRestart()
    {
        var ps = new PsRecorder().WhenJson("{\"Name\":\"W3SVC\"}");
        await Make(ps).RestartServiceAsync();

        Assert.Equal("Get-Service W3SVC -ErrorAction SilentlyContinue | Select-Object Name", ps.JsonCalls[0].Command);
        Assert.Equal(0, ps.JsonCalls[0].Retries);
        Assert.Equal(RestartCommand, ps.LastVoid.Command);
        Assert.Equal(0, ps.LastVoid.Retries);
    }

    // [Source: webdav.ts#restartService —— 未装 → 引导安装 IIS 角色]
    [Fact]
    public async Task RestartService_NotInstalled_ThrowsGuidance()
    {
        var ps = new PsRecorder().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).RestartServiceAsync());
        Assert.Contains("W3SVC 服务未安装", ex.Message, StringComparison.Ordinal);
        Assert.Empty(ps.VoidCalls);
    }

    // [Source: webdav.ts#restartService —— 权限 / 依赖两个分支]
    [Fact]
    public async Task RestartService_AccessDenied_MapsToAdminHint()
    {
        var ps = new PsRecorder().WhenJson("{\"Name\":\"W3SVC\"}").WhenVoidFails(new Exception("拒绝访问"));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).RestartServiceAsync());
        Assert.Equal("重启 W3SVC 服务需要管理员权限，请以管理员身份运行 WinShare Panel", ex.Message);
    }

    // [Source: webdav.ts#restartService —— 依赖服务分支]
    [Fact]
    public async Task RestartService_DependentService_MapsToDependencyHint()
    {
        var ps = new PsRecorder().WhenJson("{\"Name\":\"W3SVC\"}")
            .WhenVoidFails(new Exception("命令执行失败：Cannot stop service"));
        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).RestartServiceAsync());
        Assert.Equal("W3SVC 服务存在依赖服务无法重启，请手动停止依赖服务后重试，或重启计算机", ex.Message);
    }

    // [Source: webdav.ts#startService / stopService —— 命令串逐字]
    [Fact]
    public async Task StartAndStopService_BuildCommands()
    {
        var ps = new PsRecorder()
            .WhenJson("{\"Name\":\"W3SVC\"}")
            .WhenJson("{\"Name\":\"W3SVC\"}");
        var svc = Make(ps);
        await svc.StartServiceAsync();
        await svc.StopServiceAsync();

        Assert.Equal("Start-Service -Name W3SVC -ErrorAction Stop", ps.VoidCalls[0].Command);
        Assert.Equal("Stop-Service -Name W3SVC -Force -ErrorAction Stop", ps.VoidCalls[1].Command);
    }

    // [Source: webdav.ts#stopService —— 未装 → '请先安装 IIS 角色']
    [Fact]
    public async Task StopService_NotInstalled_Throws()
    {
        var ps = new PsRecorder().WhenJson("[]");
        AppException ex = await Assert.ThrowsAsync<AppException>(() => Make(ps).StopServiceAsync());
        Assert.Equal("W3SVC 服务未安装，请先安装 IIS 角色", ex.Message);
    }

    // ===== ensureWebdavSectionsUnlocked =====

    // [Source: webdav.ts#ensureWebdavSectionsUnlocked —— 8 个 system.webServer/* 节 appcmd unlock（timeout 30000）]
    [Fact]
    public async Task EnsureSectionsUnlocked_BuildsAppcmdCommand()
    {
        var ps = new PsRecorder();
        await Make(ps).EnsureWebdavSectionsUnlockedAsync();

        PsCall call = ps.LastVoid;
        Assert.Equal(8, PsCount.Of(call.Command, "unlock config /section:"));
        Assert.StartsWith(
            "try { & " + AppCmd +
            " unlock config /section:'system.webServer/webdav/authoring' 2>&1 | Out-Null } catch {}",
            call.Command, StringComparison.Ordinal);
        Assert.Contains("unlock config /section:'system.webServer/webdav/authoring/authoringRules'",
            call.Command, StringComparison.Ordinal);
        Assert.Contains("unlock config /section:'system.webServer/security/access'", call.Command, StringComparison.Ordinal);
        Assert.Equal(30000, call.TimeoutMs);
        Assert.Equal(0, call.Retries);
    }

    // [Source: webdav.ts#ensureWebdavSectionsUnlocked —— best-effort：失败不阻断]
    [Fact]
    public async Task EnsureSectionsUnlocked_FailureIsSwallowed()
    {
        var ps = new PsRecorder().WhenVoidFails(new Exception("命令执行失败：拒绝访问"));
        await Make(ps).EnsureWebdavSectionsUnlockedAsync();
        Assert.Single(ps.VoidCalls);
    }

    // ===== getConfig =====

    // [Source: webdav.ts#getConfig —— 10 个可写字段 + 4 个只读字段的批量读取命令]
    [Fact]
    public async Task GetConfig_BuildsBatchReadCommand()
    {
        var ps = new PsRecorder().WhenJson("{}");
        await Make(ps).GetConfigAsync();

        PsCall call = ps.JsonCalls[0];
        Assert.StartsWith("Import-Module WebAdministration; $r = [PSCustomObject]@{}; ", call.Command, StringComparison.Ordinal);
        Assert.EndsWith("; $r", call.Command, StringComparison.Ordinal);
        // 10 个可写字段 + 4 个只读段；authoringRules 计数段 try/catch 各含一处 Add-Member → 15
        Assert.Equal(15, PsCount.Of(call.Command, "Add-Member -MemberType NoteProperty -Name"));
        // 配置节必须用完整路径（短路径解析缺陷）
        Assert.Contains(
            "try { $r | Add-Member -MemberType NoteProperty -Name authoringEnabled -Value " +
            "(Get-WebConfigurationProperty -Filter 'system.webServer/webdav/authoring' " +
            "-PSPath 'MACHINE/WEBROOT/APPHOST' -Name enabled -ErrorAction Stop) } catch {}",
            call.Command, StringComparison.Ordinal);
        Assert.Contains(
            "-Filter 'system.webServer/security/requestFiltering/requestLimits' -PSPath 'MACHINE/WEBROOT/APPHOST' " +
            "-Name maxUrl -ErrorAction Stop", call.Command, StringComparison.Ordinal);
        // 只读字段：authoringRules 计数 / 压缩 / SSL
        Assert.Contains(
            "@(Get-WebConfiguration -Filter 'system.webServer/webdav/authoring/authoringRules/*' " +
            "-PSPath 'MACHINE/WEBROOT/APPHOST' -ErrorAction Stop).Count", call.Command, StringComparison.Ordinal);
        Assert.Contains("-Name doStaticCompression", call.Command, StringComparison.Ordinal);
        Assert.Contains("-Name doDynamicCompression", call.Command, StringComparison.Ordinal);
        Assert.Contains(
            "try { $f = (Get-WebConfigurationProperty -Filter 'system.webServer/security/access' " +
            "-PSPath 'MACHINE/WEBROOT/APPHOST' -Name sslFlags -ErrorAction Stop); " +
            "$r | Add-Member -MemberType NoteProperty -Name requireSSL -Value " +
            "($f -and $f -ne 'None' -and $f -ne '') } catch {}",
            call.Command, StringComparison.Ordinal);
        Assert.Equal(15000, call.TimeoutMs);
        Assert.Equal(0, call.Retries);
    }

    // [Source: webdav.ts#getConfig —— 布尔 !!v / 数值 Number(v)||默认 / 只读字段 !== undefined 分支]
    [Fact]
    public async Task GetConfig_ParsesWritableAndReadOnlyFields()
    {
        var ps = new PsRecorder().WhenJson("""
        {"authoringEnabled":true,"maxAllowedContentLength":10485760,"requireSSL":true,
         "enableStaticCompression":false,"maxUrlLength":0,"verifyIntegration":false,
         "globalAuthoringRulesCount":3}
        """);
        WebdavServerConfig c = await Make(ps).GetConfigAsync();

        Assert.True(c.authoringEnabled);
        Assert.Equal(10485760L, c.maxAllowedContentLength);
        Assert.True(c.requireSSL);
        Assert.False(c.enableStaticCompression);
        Assert.False(c.verifyIntegration);
        Assert.Equal(3, c.globalAuthoringRulesCount);
        Assert.Equal(260, c.maxUrlLength);            // Number(0) || 默认 → 260
        Assert.Equal(0L, c.authoringMaxRequestBodySize); // 缺失字段用默认值
        Assert.Equal(2048, c.maxQueryStringLength);
        Assert.False(c.enableDynamicCompression);
        Assert.False(c.anonymousEnabled);
        Assert.True(c.windowsEnabled);
    }

    // [Source: webdav.ts#getConfig —— 只读字段缺失时回落到默认值（不是 false）]
    [Fact]
    public async Task GetConfig_ReadOnlyFieldsMissing_FallBackToDefaults()
    {
        var ps = new PsRecorder().WhenJson("{\"authoringEnabled\":false}");
        WebdavServerConfig c = await Make(ps).GetConfigAsync();
        Assert.True(c.enableStaticCompression);
        Assert.False(c.enableDynamicCompression);
        Assert.False(c.requireSSL);
        Assert.Equal(0, c.globalAuthoringRulesCount);
    }

    // [Source: webdav.ts#getConfig —— IIS 未安装/未配置 → 默认配置]
    [Fact]
    public async Task GetConfig_CommandFails_ReturnsDefaults()
    {
        var ps = new PsRecorder().WhenJsonFails(new Exception("命令执行失败：Import-Module 失败"));
        WebdavServerConfig c = await Make(ps).GetConfigAsync();
        Assert.False(c.authoringEnabled);
        Assert.True(c.verifyIntegration);
        Assert.Equal(30000000L, c.maxAllowedContentLength);
    }

    // ===== setConfig =====

    // [Source: webdav.ts#setConfig —— 先解锁，再按字段表写；只读字段不可写]
    [Fact]
    public async Task SetConfig_Partial_WritesOnlyProvidedWritableFields()
    {
        var ps = new PsRecorder();
        await Make(ps).SetConfigAsync(new WebdavConfigPatch
        {
            authoringEnabled = true,
            authoringMaxRequestBodySize = 5000,
        });

        Assert.Equal(2, ps.VoidCalls.Count);           // [0]=解锁，[1]=写入
        Assert.Equal(30000, ps.LastVoid.TimeoutMs);
        Assert.Equal(0, ps.LastVoid.Retries);
        Assert.Equal(
            "try { Set-WebConfigurationProperty -Filter 'system.webServer/webdav/authoring' " +
            "-PSPath 'MACHINE/WEBROOT/APPHOST' -Name enabled -Value $true -ErrorAction Stop } catch {}; " +
            "try { Set-WebConfigurationProperty -Filter 'system.webServer/webdav/authoring' " +
            "-PSPath 'MACHINE/WEBROOT/APPHOST' -Name maxRequestBodySize -Value 5000 -ErrorAction Stop } catch {}",
            ps.LastVoid.Command);
    }

    // [Source: webdav.ts#setConfig —— 只读字段（globalAuthoringRulesCount/压缩/requireSSL）不写入]
    [Fact]
    public async Task SetConfig_FullConfig_SkipsReadOnlyFields()
    {
        var ps = new PsRecorder();
        WebdavService svc = Make(ps);
        await svc.SetConfigAsync(svc.DefaultConfig());

        string cmd = ps.LastVoid.Command;
        Assert.Equal(10, PsCount.Of(cmd, "Set-WebConfigurationProperty"));
        Assert.DoesNotContain("globalAuthoringRulesCount", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("requireSSL", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("doStaticCompression", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("doDynamicCompression", cmd, StringComparison.Ordinal);
        Assert.Contains("-Name maxAllowedContentLength -Value 30000000", cmd, StringComparison.Ordinal);
        Assert.Contains("-Name enabled -Value $false", cmd, StringComparison.Ordinal);
    }

    // [Source: webdav.ts#setConfig —— 未提供任何配置项 → Errors.invalidParam]
    [Fact]
    public async Task SetConfig_NoFields_ThrowsInvalidParam()
    {
        var ps = new PsRecorder();
        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => Make(ps).SetConfigAsync(new WebdavConfigPatch()));
        Assert.Equal("INVALID_PARAM", ex.Code);
        Assert.Equal("未提供任何配置项", ex.Message);
        Assert.Single(ps.VoidCalls);
        Assert.Contains("unlock config", ps.VoidCalls[0].Command, StringComparison.Ordinal);
    }

    // [Source: webdav.ts#restoreDefault —— 写默认配置（全 10 可写字段）并回传默认值]
    [Fact]
    public async Task RestoreDefault_WritesAllWritableFieldsAndReturnsDefaults()
    {
        var ps = new PsRecorder();
        WebdavServerConfig c = await Make(ps).RestoreDefaultAsync();

        Assert.Equal(10, PsCount.Of(ps.LastVoid.Command, "Set-WebConfigurationProperty"));
        Assert.Contains("-Name maxUrl -Value 260", ps.LastVoid.Command, StringComparison.Ordinal);
        Assert.Equal(260, c.maxUrlLength);
        Assert.Equal(30000000L, c.maxAllowedContentLength);
        Assert.True(c.windowsEnabled);
    }
}
