using WinShare.App.Settings;
using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Settings;

/// <summary>
/// 开机自启 / 使用模式卡测试。
/// [Source: AppSettingsCards.tsx AutoStartCard（写后读回/未接受/失败三分支）
///  + UsageModeCard（模式/主题写入与文案）]
/// </summary>
public class AutoStartUsageModeVmTests
{
    static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "wsp-as-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ===== 开机自启 =====

    [Fact]
    public void 自启_服务缺席_整卡降级()
    {
        var vm = new AutoStartCardViewModel(null, new AppSettingsStore(TempDir()));
        Assert.True(vm.Unavailable);
        Assert.NotNull(vm.UnavailableNote);
    }

    [Fact]
    public async Task 自启_写成功且读回一致_成功文案与意愿持久化()
    {
        var dir = TempDir();
        var store = new AppSettingsStore(dir);
        var svc = new StubPathRegistrationService { LoginShortcut = false, SetResult = true };
        var vm = new AutoStartCardViewModel(svc, store);
        await vm.WriteAsync(true);
        Assert.Null(vm.Pending);
        Assert.True(vm.Actual);
        Assert.Equal("已开启开机自启（下次登录 Windows 时自动启动本面板）", vm.Success);
        Assert.True(store.Load().autoStart); // 意愿写入
    }

    [Fact]
    public async Task 自启_系统未接受_读回不一致_警告文案()
    {
        var svc = new StubPathRegistrationService { LoginShortcut = false, SetResult = false };
        var vm = new AutoStartCardViewModel(svc, new AppSettingsStore(TempDir()));
        await vm.WriteAsync(true); // 想开，系统没接受
        Assert.Equal("系统未接受该设置，当前为已关闭", vm.Error);
        Assert.False(vm.Actual);
    }

    [Fact]
    public async Task 自启_写入异常_目标态作废_错误面()
    {
        var svc = new StubPathRegistrationService { ThrowOnSet = new UnauthorizedAccessException("登录项只读") };
        var vm = new AutoStartCardViewModel(svc, new AppSettingsStore(TempDir()));
        var err = await vm.WriteAsync(true);
        Assert.Equal("登录项只读", err);
        Assert.Contains("开机自启设置失败", vm.Error!); // 错误面文案（对齐 TS message.error）
        Assert.Null(vm.Pending); // 目标态作废
    }

    // ===== 使用模式与外观 =====

    [Fact]
    public async Task 使用模式_专家切换_文案与持久化()
    {
        var store = new AppSettingsStore(TempDir());
        var vm = new UsageModeCardViewModel(store);
        var r = await vm.ApplyModeAsync(true);
        Assert.Null(r);
        Assert.Equal("已切换到专家模式：高级参数已展开", vm.Success);
        Assert.True(store.Load().advancedMode);
        r = await vm.ApplyModeAsync(false);
        Assert.Equal("已切换到新手模式：高级参数已收起", vm.Success);
        Assert.False(store.Load().advancedMode);
    }

    [Fact]
    public async Task 主题_热切换委托与事件发布()
    {
        var store = new AppSettingsStore(TempDir());
        var applied = new List<ThemeMode>();
        var notified = 0;
        var vm = new UsageModeCardViewModel(store, m => applied.Add(m), () => notified++);
        await vm.ApplyThemeAsync(ThemeMode.Dark);
        Assert.Equal("主题已更新", vm.Success);
        Assert.Equal([ThemeMode.Dark], applied);
        Assert.Equal(1, notified);
        Assert.Equal(ThemeMode.Dark, store.Load().theme);
    }
}
