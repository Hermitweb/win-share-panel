// [Source: src/utils/firstRun.ts + firstRun.test.ts] —— 首启判定与建参拼装（纯函数，表驱动）。
using WinShare.App.FirstRun;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.FirstRun;

public class FirstRunLogicTests
{
    private static Share ShareOf(string name) => new() { name = name, path = @"C:\x" };

    // [Source: firstRun.test.ts#shouldShowGuide] 三条件任一成立都不弹
    [Fact]
    public void ShouldShowGuide_EmptyShares_NotMarked_Novice_Shows()
    {
        Assert.True(FirstRunLogic.ShouldShowGuide(Array.Empty<Share>(), advancedMode: false, storageFlag: null));
    }

    [Theory]
    [InlineData(true, null, true)]      // 已有共享 → 不弹
    [InlineData(false, "1", true)]      // 已完成标记 → 不弹
    [InlineData(true, null, false)]     // 专家模式 → 不弹（无论是否有共享）
    public void ShouldShowGuide_AnyConditionSuppresses(bool hasShare, string? flag, bool advanced)
    {
        IReadOnlyList<Share> shares = hasShare ? new[] { ShareOf("s1") } : Array.Empty<Share>();
        Assert.False(FirstRunLogic.ShouldShowGuide(shares, advanced, flag));
    }

    // [Source: firstRun.test.ts] 共享列表未加载（null）→ 不弹（避免数据稍后才到时闪窗）
    [Fact]
    public void ShouldShowGuide_NullShares_Suppressed()
    {
        Assert.False(FirstRunLogic.ShouldShowGuide(null, advancedMode: false, storageFlag: null));
    }

    // [Source: firstRun.test.ts#isMarkedComplete] '1' / 'true' 算已标记
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMarkedComplete_Table(string? flag, bool expected)
    {
        Assert.Equal(expected, FirstRunLogic.IsMarkedComplete(flag));
    }

    // [Source: firstRun.test.ts#tierToAccess] 恰好一个权限键 + 默认 read
    [Fact]
    public void TierToAccess_MapsExactlyOneKey()
    {
        CreateShareOpts read = FirstRunLogic.TierToAccess(PermTier.Read);
        Assert.NotNull(read.readAccess);
        Assert.Null(read.fullAccess);
        Assert.Null(read.changeAccess);

        CreateShareOpts change = FirstRunLogic.TierToAccess(PermTier.Change);
        Assert.NotNull(change.changeAccess);
        Assert.Null(change.readAccess);

        CreateShareOpts full = FirstRunLogic.TierToAccess(PermTier.Full);
        Assert.NotNull(full.fullAccess);
        Assert.Null(full.changeAccess);
    }

    // [Source: firstRun.test.ts#buildCreateInput] trim + 档位展开 + 高级参数仅展开时并入
    [Fact]
    public void BuildCreateInput_TrimsAndAppliesAdvanced()
    {
        CreateShareOpts plain = FirstRunLogic.BuildCreateInput("  共享  ", @"  C:\data  ", PermTier.Read);
        Assert.Equal("共享", plain.name);
        Assert.Equal(@"C:\data", plain.path);
        Assert.Null(plain.concurrentUserLimit);
        Assert.Null(plain.cachingMode);

        CreateShareOpts adv = FirstRunLogic.BuildCreateInput(
            "s", @"C:\d", PermTier.Change,
            new FirstRunLogic.AdvancedOpts(ConcurrentUserLimit: 5, CachingMode: CachingMode.Manual));
        Assert.Equal(5, adv.concurrentUserLimit);
        Assert.Equal(CachingMode.Manual, adv.cachingMode);
        Assert.NotNull(adv.changeAccess);
    }

    // [Source: firstRun.test.ts#buildCreateInput] 0 / None 不写入（保持默认）
    [Fact]
    public void BuildCreateInput_ZeroLimitAndNoneCache_NotWritten()
    {
        CreateShareOpts opts = FirstRunLogic.BuildCreateInput(
            "s", @"C:\d", PermTier.Read,
            new FirstRunLogic.AdvancedOpts(ConcurrentUserLimit: 0, CachingMode: CachingMode.None));
        Assert.Null(opts.concurrentUserLimit);
        Assert.Null(opts.cachingMode);
    }

    // [Source: firstRun.ts#GUIDE_STEPS / PERM_TIERS] 三步契约与三档文案同源
    [Fact]
    public void GuideStepsAndPermTiers_ContractShape()
    {
        Assert.Equal(3, FirstRunLogic.GuideSteps.Count);
        Assert.Equal("选文件夹", FirstRunLogic.GuideSteps[0].Title);
        Assert.Equal("给谁用", FirstRunLogic.GuideSteps[1].Title);
        Assert.Equal("完成", FirstRunLogic.GuideSteps[2].Title);

        Assert.Equal(3, FirstRunLogic.PermTiers.Count);
        // 最安全的「只看看」排第一且为默认档
        Assert.Equal(PermTier.Read, FirstRunLogic.PermTiers[0].Tier);
        Assert.Equal(PermTier.Read, FirstRunLogic.DefaultPermTier);
    }

    // [Source: FirstRunGuide.tsx] 向导窗口可构造（STA 设计期实例化；服务缺席时应正常降级不抛）
    [Fact]
    public void FirstRunGuideWindow_CreatesWithoutThrowing()
    {
        object? instance = null;
        Exception? instEx = null;
        var sta = new System.Threading.Thread(() =>
        {
            try
            {
                var store = new FirstRunFlagStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ws-firstrun-test.flag"));
                instance = new FirstRunGuideWindow(null, null, store);
            }
            catch (Exception e) { instEx = e; }
        })
        { IsBackground = true };
        sta.SetApartmentState(System.Threading.ApartmentState.STA);
        sta.Start();
        sta.Join();

        if (instEx is not null) throw instEx;
        Assert.NotNull(instance);
    }
}
