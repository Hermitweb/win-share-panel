using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Settings;

/// <summary>
/// G5 判定/徽章推断测试（逐条对齐 ProtocolCapabilityBanner.tsx renderStatusBadges）。
/// [Source: src/components/ProtocolCapabilityBanner.tsx + src/hooks/useEnsureProtocolCaps.ts]
/// </summary>
public class ProtocolDetectionViewModelTests
{
    static ProtocolFeatureState Cap(Protocol p, bool installed, ProtocolInstallType type, ServiceState svc, string hint)
        => new()
        {
            protocol = p,
            installed = installed,
            installType = type,
            serviceName = "S",
            serviceStatus = svc,
            installCommand = installed ? "" : "dsh install",
            installHint = hint,
        };

    [Fact]
    public void 徽章_iis已装_角色未装()
    {
        var cap = Cap(Protocol.Ftp, false, ProtocolInstallType.IisRole, ServiceState.Unknown, "IIS 基础已安装，但 FTP 服务角色未安装");
        var badges = ProtocolDetectionViewModel.InferBadges(cap);
        Assert.Contains(badges, b => b.Label == "IIS 已装" && b.Color == "green");
        Assert.Contains(badges, b => b.Label == "FTP 角色未装" && b.Color == "orange");
    }

    [Fact]
    public void 徽章_iis未装()
    {
        var cap = Cap(Protocol.Webdav, false, ProtocolInstallType.IisRole, ServiceState.Unknown, "IIS 均未安装（W3SVC 服务不存在）");
        var badges = ProtocolDetectionViewModel.InferBadges(cap);
        Assert.Contains(badges, b => b.Label == "IIS 未装" && b.Color == "red");
        // 「W3SVC.*未运行」正则不命中「不存在」→ 不应多打服务已停
        Assert.DoesNotContain(badges, b => b.Label == "服务已停");
    }

    [Fact]
    public void 徽章_服务未运行命中提示()
    {
        var cap = Cap(Protocol.Ftp, false, ProtocolInstallType.IisRole, ServiceState.Unknown, "ftpsvc 服务未运行");
        var badges = ProtocolDetectionViewModel.InferBadges(cap);
        Assert.Contains(badges, b => b.Label == "服务已停" && b.Color == "orange");
    }

    [Fact]
    public void 徽章_已装且服务停()
    {
        var cap = Cap(Protocol.Smb, true, ProtocolInstallType.Builtin, ServiceState.Stopped, "");
        var badges = ProtocolDetectionViewModel.InferBadges(cap);
        Assert.Contains(badges, b => b.Label == "服务已停");
    }

    [Fact]
    public void 徽章_已装且服务运行_无徽章()
    {
        var cap = Cap(Protocol.Smb, true, ProtocolInstallType.Builtin, ServiceState.Running, "");
        Assert.Empty(ProtocolDetectionViewModel.InferBadges(cap));
    }

    [Fact]
    public void 服务缺席_整段降级不臆造()
    {
        var vm = new ProtocolDetectionViewModel(null);
        Assert.True(vm.ServiceMissing);
        Assert.NotNull(vm.DegradedNote);
        Assert.Null(vm.InstalledOf(Protocol.Nfs));
        Assert.Empty(vm.UninstalledProtocols());
    }

    [Fact]
    public async Task 探测后_已装未装判定与未装清单()
    {
        var caps = new Dictionary<WinShare.Core.Contracts.Protocol, WinShare.Core.Contracts.ProtocolFeatureState>();
        caps[WinShare.Core.Contracts.Protocol.Smb] = Cap(Protocol.Smb, true, ProtocolInstallType.Builtin, ServiceState.Running, "");
        caps[WinShare.Core.Contracts.Protocol.Nfs] = Cap(Protocol.Nfs, false, ProtocolInstallType.ServerFeature, ServiceState.Unknown, "需安装 NFS 服务器角色");
        caps[WinShare.Core.Contracts.Protocol.Ftp] = Cap(Protocol.Ftp, false, ProtocolInstallType.IisRole, ServiceState.Unknown, "IIS 均未安装");
        caps[WinShare.Core.Contracts.Protocol.Webdav] = Cap(Protocol.Webdav, false, ProtocolInstallType.IisRole, ServiceState.Unknown, "IIS 均未安装");
        var svc = new StubProtocolService { DetectedCaps = caps };
        var vm = new ProtocolDetectionViewModel(svc);
        await vm.DetectAsync();
        Assert.True(vm.InstalledOf(Protocol.Smb));
        Assert.False(vm.InstalledOf(Protocol.Nfs));
        Assert.Equal(3, vm.UninstalledProtocols().Count);
        Assert.True(vm.HasInstallCommand(Protocol.Nfs));
        Assert.DoesNotContain(vm.Badges, b => b.Label == "服务已停");
        Assert.Equal(2, vm.Badges.Count(b => b.Label == "IIS 未装"));
    }

    [Fact]
    public async Task 安装_成功文案_并重探刷新()
    {
        var svc = new StubProtocolService();
        var vm = new ProtocolDetectionViewModel(svc);
        var err = await vm.InstallAsync(Protocol.Nfs);
        Assert.Null(err);
        Assert.Equal("安装成功，可能需要重启系统", vm.Success);
        Assert.Contains(Protocol.Nfs, svc.InstalledCalls);
        Assert.Equal(1, svc.DetectCalls); // 安装后重探（对齐 TS handleInstall）
    }
}
