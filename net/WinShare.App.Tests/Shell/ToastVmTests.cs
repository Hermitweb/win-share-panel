// v2.2.0 Toast 纯逻辑（堆叠上限 / 超时收敛 / 手动关闭 / Changed 通知）——无 UI 依赖。
using WinShare.App.Shell;

namespace WinShare.App.Tests.Shell;

public class ToastVmTests
{
    static DateTimeOffset T(int sec) => new(2026, 1, 1, 0, 0, sec, TimeSpan.Zero);

    [Fact]
    public void Push_AddsItem_AndFiresChanged()
    {
        var vm = new ToastVm();
        int fired = 0;
        vm.Changed += () => fired++;
        var item = vm.Push(ToastLevel.Success, "已保存");
        Assert.Single(vm.Items);
        Assert.Equal("已保存", vm.Items[0].Text);
        Assert.True(fired >= 1);
        Assert.Equal(1, item.Id);
    }

    [Fact]
    public void Push_OverCap_DropsOldest()
    {
        var vm = new ToastVm();
        vm.Push(ToastLevel.Success, "a");
        vm.Push(ToastLevel.Success, "b");
        vm.Push(ToastLevel.Success, "c");
        vm.Push(ToastLevel.Success, "d");
        Assert.Equal(ToastVm.MaxVisible, vm.Items.Count);
        Assert.DoesNotContain(vm.Items, i => i.Text == "a"); // 最旧被顶掉
        Assert.Equal("d", vm.Items[^1].Text);
    }

    [Fact]
    public void PruneExpired_SuccessAfterTtl_WarnAfterTtl_DangerStays()
    {
        var vm = new ToastVm();
        // Push 的 At 取真实时钟且逐条相隔微秒——各按自身 At 构造超时点
        vm.Push(ToastLevel.Success, "s");
        vm.Push(ToastLevel.Warn, "w");
        vm.Push(ToastLevel.Danger, "d");
        var successAt = vm.Items.First(i => i.Text == "s").At;
        var warnAt = vm.Items.First(i => i.Text == "w").At;

        // TTL 内不清
        Assert.False(vm.PruneExpired(successAt + ToastVm.SuccessTtl - TimeSpan.FromMilliseconds(50)));
        Assert.Equal(3, vm.Items.Count);

        // Success 4s 到期
        Assert.True(vm.PruneExpired(successAt + ToastVm.SuccessTtl));
        Assert.DoesNotContain(vm.Items, i => i.Text == "s");

        // Warn 6s 到期；Danger 永驻
        Assert.True(vm.PruneExpired(warnAt + ToastVm.WarnTtl));
        Assert.DoesNotContain(vm.Items, i => i.Text == "w");
        Assert.Single(vm.Items);
        Assert.Equal("d", vm.Items[0].Text);
    }

    [Fact]
    public void Dismiss_RemovesById_AndReportsExistence()
    {
        var vm = new ToastVm();
        var a = vm.Push(ToastLevel.Danger, "a");
        vm.Push(ToastLevel.Danger, "b");
        Assert.True(vm.Dismiss(a.Id));
        Assert.False(vm.Dismiss(a.Id)); // 二次关闭不存在
        Assert.Single(vm.Items);
        Assert.Equal("b", vm.Items[0].Text);
    }

    [Fact]
    public void DurationFor_LevelsMatchContract()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), ToastVm.DurationFor(ToastLevel.Success));
        Assert.Equal(TimeSpan.FromSeconds(6), ToastVm.DurationFor(ToastLevel.Warn));
        Assert.Equal(Timeout.InfiniteTimeSpan, ToastVm.DurationFor(ToastLevel.Danger));
    }

    [Theory]
    [InlineData(ToastLevel.Success, "BrushSuccess")]
    [InlineData(ToastLevel.Warn, "BrushWarning")]
    [InlineData(ToastLevel.Danger, "BrushDanger")]
    public void LevelBrushKey_MapsThemeTokens(ToastLevel level, string key)
    {
        Assert.Equal(key, ToastHost.LevelBrushKey(level));
    }
}
