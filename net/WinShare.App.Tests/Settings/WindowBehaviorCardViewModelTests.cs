// v2.2.0 安装与运行配置：窗口行为卡（任务栏图标 / 关闭按钮行为）——持久化 + 回调 + Busy 门控。
using WinShare.App.Settings;
using WinShare.App.State;

namespace WinShare.App.Tests.Settings;

public class WindowBehaviorCardViewModelTests : IDisposable
{
    readonly string _dir;
    readonly List<(bool show, bool close)> _applied = new();

    public WindowBehaviorCardViewModelTests()
    {
        _dir = Path.Combine(AppContext.BaseDirectory, "wsp-win-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private WindowBehaviorCardViewModel Make()
    {
        return new WindowBehaviorCardViewModel(new AppSettingsStore(_dir),
            (show, close) => _applied.Add((show, close)));
    }

    [Fact]
    public async Task 初始态_读取持久化默认值()
    {
        var vm = Make();
        Assert.True(vm.ShowInTaskbar);
        Assert.True(vm.CloseToTray);
        Assert.Empty(_applied); // 构造不触发回调
    }

    [Fact]
    public async Task 关闭行为写入_持久化且回调()
    {
        var vm = Make();
        var err = await vm.SetCloseToTrayAsync(false);
        Assert.Null(err);
        Assert.False(vm.CloseToTray);
        Assert.True(vm.ShowInTaskbar); // 不受影响（只改了关闭行为）
        var persisted = new AppSettingsStore(_dir).Load();
        Assert.False(persisted.closeToTray);
        Assert.True(persisted.showInTaskbar);
        var (show, close) = _applied.Single();
        Assert.True(show);
        Assert.False(close);
    }

    [Fact]
    public async Task 任务栏开关写入_持久化且回调()
    {
        var vm = Make();
        var err = await vm.SetShowTaskbarAsync(false);
        Assert.Null(err);
        Assert.False(vm.ShowInTaskbar);
        Assert.True(vm.CloseToTray); // 不受影响（只改了任务栏）
        Assert.False(new AppSettingsStore(_dir).Load().showInTaskbar);
        Assert.Equal((false, true), _applied.Single());
    }

    [Fact]
    public async Task Busy期间_重复提交被门控()
    {
        var vm = Make();
        var first = vm.SetShowTaskbarAsync(false);
        var second = await vm.SetShowTaskbarAsync(true); // Busy 中 → 返回 null 且不写入
        Assert.Null(second);
        await first;
        Assert.False(vm.ShowInTaskbar);
    }
}
