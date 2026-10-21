using WinShare.App.Settings;
using WinShare.App.State;

namespace WinShare.App.Tests.Settings;

/// <summary>
/// 告警规则卡测试（单字段提交 + 磁盘草稿语义）。
/// [Source: src/components/AppSettingsCards.tsx AlertRulesCard（toInt / 草稿跟随 / 失焦保存 / 失败退回）]
/// </summary>
public class AlertRulesCardViewModelTests
{
    static AppSettingsStore TempStore(string dir) => new AppSettingsStore(dir);

    static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "wsp-alert-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 输入收敛_toInt_对齐()
    {
        Assert.Null(AlertRulesCardViewModel.ToInt(null));
        Assert.Null(AlertRulesCardViewModel.ToInt(""));
        Assert.Null(AlertRulesCardViewModel.ToInt("   "));
        Assert.Null(AlertRulesCardViewModel.ToInt("abc"));
        Assert.Equal(42, AlertRulesCardViewModel.ToInt("42"));
        Assert.Equal(0, AlertRulesCardViewModel.ToInt("0"));
    }

    [Fact]
    public void 磁盘草稿_越界收敛区间_1_到_100000()
    {
        var store = TempStore(TempDir());
        var vm = new AlertRulesCardViewModel(store);
        vm.SetDiskDraft(0);
        Assert.Equal(AlertRulesCardViewModel.DiskMin, vm.DiskDraft);
        vm.SetDiskDraft(999_999_999);
        Assert.Equal(AlertRulesCardViewModel.DiskMax, vm.DiskDraft);
        vm.SetDiskDraft(33);
        Assert.Equal(33, vm.DiskDraft);
        Assert.True(vm.DiskDirty);
    }

    [Fact]
    public async Task 磁盘保存_失焦即存_成功后草稿收敛到已生效值()
    {
        var store = TempStore(TempDir());
        var vm = new AlertRulesCardViewModel(store);
        Assert.Equal(20, vm.DiskLowGb); // store 默认
        vm.SetDiskDraft(55);
        await vm.SaveDiskAsync();
        Assert.Equal(55, vm.DiskLowGb);
        Assert.Equal(55, vm.DiskDraft);
        Assert.False(vm.DiskDirty);
        Assert.Equal("磁盘阈值已保存", vm.Success);
        // 持久化落盘（重启保持）
        Assert.Equal(55, store.Load().alertRules.diskLowGb);
    }

    [Fact]
    public async Task 未dirty时失焦不提交()
    {
        var store = TempStore(TempDir());
        var vm = new AlertRulesCardViewModel(store);
        await vm.SaveDiskAsync(); // 草稿=当前值 → 不动作
        Assert.False(vm.DiskDirty);
        Assert.Null(vm.Success);
    }

    [Fact]
    public async Task 外部值变化_草稿跟随_对齐TS渲染期同步()
    {
        var store = TempStore(TempDir());
        var vm = new AlertRulesCardViewModel(store);
        vm.SetDiskDraft(88); // 编辑中
        vm.DiscardDraftToExternal(40); // 别处改了
        Assert.Equal(40, vm.DiskLowGb);
        Assert.Equal(40, vm.DiskDraft);
        Assert.False(vm.DiskDirty);
        await vm.SaveDiskAsync(); // 跟随后无 dirty → 不应写盘（store 值保持）
        Assert.Equal(20, store.Load().alertRules.diskLowGb);
        Assert.Null(vm.Success);
    }

    [Fact]
    public async Task 单字段提交_其余规则保持原值()
    {
        var dir = TempDir();
        var store = TempStore(dir);
        store.SaveState(new WinShare.Core.Contracts.AppStatePatch
        {
            alertRules = new WinShare.Core.Contracts.AlertRules
            {
                idleAlertMinutes = 30, smb1Alert = false, weakPasswordAlert = true, diskLowGb = 12,
            },
        });
        var vm = new AlertRulesCardViewModel(store);
        Assert.Equal(30, vm.SelectedIdle);
        await vm.SaveSmb1Async(true);
        var saved = store.Load().alertRules;
        Assert.True(saved.smb1Alert);
        Assert.Equal(30, saved.idleAlertMinutes);  // 未改项保持
        Assert.Equal(12, saved.diskLowGb);
        Assert.Equal("SMB1 告警已保存", vm.Success);
    }

    [Fact]
    public async Task 空闲提醒_0为关闭_null化()
    {
        var store = TempStore(TempDir());
        var vm = new AlertRulesCardViewModel(store);
        await vm.SaveIdleAsync(0);
        Assert.Null(store.Load().alertRules.idleAlertMinutes);
        Assert.Equal(0, vm.SelectedIdle);
    }
}
