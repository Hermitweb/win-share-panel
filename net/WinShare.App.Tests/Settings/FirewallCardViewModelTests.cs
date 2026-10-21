using WinShare.App.Settings;

namespace WinShare.App.Tests.Settings;

/// <summary>
/// 防火墙卡测试（组内规则 列表/预设添加/删除 + 被动端口校验）。
/// [Source: src/components/AppSettingsCards.tsx FirewallCard（rangeOk / 添加文案三分支）+ IFirewallService 契约]
/// </summary>
public class FirewallCardViewModelTests
{
    [Fact]
    public void 被动端口范围_合法性矩阵()
    {
        var vm = new FirewallCardViewModel(new StubFirewallService());
        vm.SelectPreset("ftpPassive");
        Assert.True(vm.NeedRange);
        vm.SetPassiveRange(50000, 51000);
        Assert.True(vm.RangeOk);
        vm.SetPassiveRange(51000, 50000);
        Assert.False(vm.RangeOk); // 起始>结束
        vm.SetPassiveRange(0, 50000);
        Assert.False(vm.RangeOk); // <1
        vm.SetPassiveRange(50000, 70000);
        Assert.False(vm.RangeOk); // >65535
        vm.SetPassiveRange(null, 50000);
        Assert.False(vm.RangeOk); // 缺失
    }

    [Fact]
    public async Task 添加预设_新建条数文案对齐TS()
    {
        var svc = new StubFirewallService();
        var vm = new FirewallCardViewModel(svc);
        var err = await vm.AddAsync();
        Assert.Null(err);
        Assert.Equal("已添加 1 条组内规则：WinShare SMB (445/TCP)", vm.Success);
        Assert.Contains("WinShare SMB (445/TCP)", svc.EnsuredNames);
    }

    [Fact]
    public async Task 被动预设_携带自定义端口段()
    {
        var svc = new StubFirewallService();
        var vm = new FirewallCardViewModel(svc);
        vm.SelectPreset("ftpPassive");
        vm.SetPassiveRange(60000, 60500);
        await vm.AddAsync();
        Assert.Contains("WinShare FTP Passive", svc.EnsuredNames);
    }

    [Fact]
    public async Task 添加失败_错误面文案且重载列表()
    {
        var svc = new StubFirewallService { ThrowOnEnsure = new InvalidOperationException("规则名白名单校验失败") };
        var vm = new FirewallCardViewModel(svc);
        var err = await vm.AddAsync();
        Assert.Equal("规则名白名单校验失败", err);
        Assert.Equal("添加防火墙规则失败：规则名白名单校验失败", vm.Error);
        Assert.True(svc.ListCalls >= 1); // 失败后重载（可能部分写入，以读回为准）
    }

    [Fact]
    public async Task 删除组内规则_成功文案()
    {
        var svc = new StubFirewallService
        {
            ManagedRules = [new WinShare.Core.Contracts.FirewallRule { name = "r1", enabled = true, ports = "445" }],
        };
        var vm = new FirewallCardViewModel(svc);
        await vm.ReloadAsync();
        var err = await vm.RemoveAsync("r1");
        Assert.Null(err);
        Assert.Equal("已删除组内规则「r1」", vm.Success);
        Assert.Contains("r1", svc.RemovedNames);
    }

    [Fact]
    public void 服务缺席_降级文案()
    {
        var vm = new FirewallCardViewModel(null);
        vm.ReloadAsync().Wait();
        Assert.Equal("防火墙服务未注册（等待 Core 服务层实现）：组内规则不可读", vm.Error);
    }
}
