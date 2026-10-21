using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Settings;

/// <summary>
/// 协议配置卡 VM 测试（纯逻辑；stub adapter 驱动）。
/// [Source: src/hooks/useProtocolSettings.ts 能力门控/保存语义 + src/pages/Settings.tsx 新手/专家模式高级参数收敛]
/// </summary>
public class ProtocolConfigCardViewModelTests
{
    [Fact]
    public void 新手模式补丁不含高级字段()
    {
        var (svc, _) = StubProtocolService.BuildAllInstalled();
        var vm = new SmbConfigCardViewModel(svc, advancedMode: false);
        var patch = vm.BuildPatchForTesting();
        Assert.DoesNotContain("sessionTimeoutSeconds", patch.Keys);
        Assert.DoesNotContain("requestCompression", patch.Keys);
        Assert.Contains("enableSMB1Protocol", patch.Keys);
    }

    [Fact]
    public void 专家模式补丁含高级字段()
    {
        var (svc, _) = StubProtocolService.BuildAllInstalled();
        var vm = new SmbConfigCardViewModel(svc, advancedMode: true);
        var patch = vm.BuildPatchForTesting();
        Assert.Contains("maxWorkItems", patch.Keys);
        Assert.Contains("requestCompression", patch.Keys);
    }

    [Fact]
    public void 服务缺席时整卡降级_命令报降级说明()
    {
        var vm = new NfsConfigCardViewModel(null, advancedMode: true);
        Assert.NotNull(vm.DegradedNote);
        Assert.Null(vm.Capabilities);
        vm.LoadAsync().Wait();
        Assert.Equal(vm.DegradedNote, vm.Error);
        vm.SaveAsync().Wait();
        Assert.Equal(vm.DegradedNote, vm.Error);
    }

    [Fact]
    public void 保存合并语义_未改字段保留服务器现值()
    {
        var (svc, adapters) = StubProtocolService.BuildAllInstalled();
        var current = new NfsServerConfig
        {
            gracefulUnmount = true,
            logActivity = true,
            tcpConnectionTimeout = 240,
            udpConnectionTimeout = 240,
            restartConnectionTimeout = 60,
            maxConcurrentConnectionsPerUser = 0,
            directoryCacheExpiry = 60,
            anonymousUid = -2,
            anonymousGid = -2,
            gatewayCharacterSet = "UTF-8",
            protocolVersion = "4.1",
        };
        adapters[Protocol.Nfs].ConfigSource = current;
        var vm = new NfsConfigCardViewModel(svc, advancedMode: true);
        vm.LoadAsync().Wait();
        Assert.Equal(240, vm.TcpTimeout); // 服务器值回填镜像
        vm.DirectoryCacheExpiry = 120;    // 只改一项
        vm.SaveAsync().Wait();
        Assert.Null(vm.Error);
        Assert.Equal("已保存", vm.Success);
        var written = Assert.IsType<NfsServerConfig>(adapters[Protocol.Nfs].LastSetConfig);
        Assert.Equal(120, written.directoryCacheExpiry);
        Assert.Equal(current.gracefulUnmount, written.gracefulUnmount);   // 未改字段原值
        Assert.Equal(240, written.tcpConnectionTimeout);
        Assert.Equal(current.gatewayCharacterSet, written.gatewayCharacterSet); // 只读字段不参与补丁但克隆保留
        Assert.Equal("已保存", vm.Success);
    }

    [Fact]
    public void 预设应用_枚举字面量收敛到枚举_失败值保留现值()
    {
        var (svc, adapters) = StubProtocolService.BuildAllInstalled();
        adapters[Protocol.Smb].ConfigSource = new SmbServerConfig();
        var vm = new SmbConfigCardViewModel(svc, advancedMode: true);
        vm.LoadAsync().Wait();
        var preset = ProtocolPresets.Smb.First(p => p.Id == "smb-perf");
        vm.ApplyPresetAsync(preset).Wait();
        var written = Assert.IsType<SmbServerConfig>(adapters[Protocol.Smb].LastSetConfig);
        // 契约枚举域内字面量正常收敛
        Assert.Equal(2048, written.maxWorkItems);
        Assert.Equal(300, written.sessionTimeoutSeconds);
    }

    [Fact]
    public void 预设应用_枚举域外字面量跳过该字段不写脏值()
    {
        var (svc, adapters) = StubProtocolService.BuildAllInstalled();
        var current = new SmbServerConfig { requestCompression = RequestCompression.Require };
        adapters[Protocol.Smb].ConfigSource = current;
        var vm = new SmbConfigCardViewModel(svc, advancedMode: true);
        vm.LoadAsync().Wait();
        // 「性能优先」预设含 requestCompression=OptimizeForSpeed（不在契约枚举域内）→ 跳过，保留现值
        var preset = ProtocolPresets.Smb.First(p => p.Id == "smb-perf");
        vm.ApplyPresetAsync(preset).Wait();
        var written = Assert.IsType<SmbServerConfig>(adapters[Protocol.Smb].LastSetConfig);
        Assert.Equal(RequestCompression.Require, written.requestCompression);
    }

    [Fact]
    public void 服务控制_重启计数且成功文案对齐()
    {
        var (svc, adapters) = StubProtocolService.BuildAllInstalled();
        adapters[Protocol.Ftp].ConfigSource = new FtpServerConfig();
        var vm = new FtpConfigCardViewModel(svc, advancedMode: true);
        vm.LoadAsync().Wait();
        vm.RestartAsync().Wait();
        Assert.Equal(1, adapters[Protocol.Ftp].RestartCalls);
        Assert.Equal("FTP 服务已重启", vm.Success);
    }

    [Fact]
    public void 保存失败_错误面文案且重试可恢复()
    {
        var (svc, adapters) = StubProtocolService.BuildAllInstalled();
        adapters[Protocol.Webdav].ConfigSource = new WebdavServerConfig();
        adapters[Protocol.Webdav].ThrowOnSet = new InvalidOperationException("IIS 锁定写入失败");
        var vm = new WebdavConfigCardViewModel(svc, advancedMode: true);
        vm.LoadAsync().Wait();
        vm.SaveAsync().Wait();
        Assert.Equal("IIS 锁定写入失败", vm.Error);
        adapters[Protocol.Webdav].ThrowOnSet = null;
        vm.SaveAsync().Wait();
        Assert.Equal("已保存", vm.Success);
    }

    [Fact]
    public void 安装三态注入_驱动门控()
    {
        var (svc, _) = StubProtocolService.BuildAllInstalled();
        var vm = new NfsConfigCardViewModel(svc, advancedMode: true);
        Assert.Null(vm.Installed);
        vm.SetInstalled(false);
        Assert.False(vm.Installed);
        vm.SetInstalled(true);
        Assert.True(vm.Installed);
    }

    [Fact]
    public void 停止态判定_驱动启停按钮显隐()
    {
        var (svc, adapters) = StubProtocolService.BuildAllInstalled();
        adapters[Protocol.Smb].ServiceSource = new ServiceStatus { name = "LanmanServer", status = ServiceState.Stopped, startType = "Automatic" };
        adapters[Protocol.Smb].ConfigSource = new SmbServerConfig();
        var vm = new SmbConfigCardViewModel(svc, advancedMode: true);
        vm.LoadAsync().Wait();
        Assert.True(vm.IsServiceStopped);
        vm.StartAsync().Wait(); // stopped 态经 restart 达成启动
        Assert.Equal(1, adapters[Protocol.Smb].RestartCalls);
    }
}

