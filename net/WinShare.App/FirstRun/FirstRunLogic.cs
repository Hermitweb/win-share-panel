// 移植自 src/utils/firstRun.ts（首启三问向导的判定与参数拼装）。
// 「老用户绝不打扰」是行为契约：有共享 / 已完成 / 专家模式三种情形都必须收敛到"不弹"，
// 故全部做成纯函数以便表驱动单测（挂载方只消费结果）。
using WinShare.Core.Contracts;

namespace WinShare.App.FirstRun;

/// <summary>权限档位（TS: PermTier）。</summary>
public enum PermTier { Read, Change, Full }

/// <summary>档位文案（TS: PERM_TIERS，逐字对齐；顺序即展示顺序，"最安全"排第一并作默认）。</summary>
public sealed record PermTierOption(PermTier Tier, string Label, string Desc);

/// <summary>向导步骤（TS: GUIDE_STEPS，与 UI 同源）。</summary>
public sealed record GuideStep(string Title, string Hint);

public static class FirstRunLogic
{
    /// <summary>TS: FIRST_RUN_DONE_KEY / FIRST_RUN_DONE_VALUE。</summary>
    public const string DoneKey = "winshare.firstRunCompleted";
    public const string DoneValue = "1";

    public static readonly IReadOnlyList<GuideStep> GuideSteps = new List<GuideStep>
    {
        new("选文件夹", "要对外共享哪个文件夹？"),
        new("给谁用", "别人能对它做到哪一步？"),
        new("完成", "确认名字，马上就能用"),
    };

    public static readonly IReadOnlyList<PermTierOption> PermTiers = new List<PermTierOption>
    {
        new(PermTier.Read, "只让他们看看", "能打开、复制文件，但不能改动或删除——给不熟悉的人最稳妥"),
        new(PermTier.Change, "大家一起放文件", "能看、能新增和修改文件，不能动权限设置——家人同事协作够用"),
        new(PermTier.Full, "完全交给他们", "能看、能改、能删、能改权限——只给绝对信任的人"),
    };

    public static readonly PermTier DefaultPermTier = PermTier.Read;

    /// <summary>开发/测试用的默认账号（TS: DEFAULT_READ_ACCESS）。</summary>
    public static readonly IReadOnlyList<string> DefaultAccess = new[] { "Everyone" };

    /// <summary>TS: isMarkedComplete(flag) —— '1'/true/'true' 算已标记。</summary>
    public static bool IsMarkedComplete(string? flag) =>
        flag == DoneValue || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// TS: shouldShowGuide(shares, advancedMode, storageFlag) —— 三条件任一成立都不弹：
    /// ①已有共享 ②已完成标记 ③专家模式。只有「空共享 + 未标记 + 新手模式」才弹；
    /// shares 传 null 视为"尚未加载"→ 不弹（避免数据稍后才到时骚扰老用户）。
    /// </summary>
    public static bool ShouldShowGuide(IReadOnlyList<Share>? shares, bool? advancedMode, string? storageFlag)
    {
        if (advancedMode == true) return false;
        if (IsMarkedComplete(storageFlag)) return false;
        if (shares is null) return false;
        return shares.Count == 0;
    }

    /// <summary>TS: tierToAccess(tier) —— 恰好一个权限键。</summary>
    public static CreateShareOpts TierToAccess(PermTier tier) => tier switch
    {
        PermTier.Full => new CreateShareOpts { fullAccess = DefaultAccess },
        PermTier.Change => new CreateShareOpts { changeAccess = DefaultAccess },
        _ => new CreateShareOpts { readAccess = DefaultAccess },
    };

    /// <summary>向导步骤三的「高级参数」折叠区（未展开=保持默认=完全不提交）。</summary>
    public sealed record AdvancedOpts(int? ConcurrentUserLimit = null, CachingMode? CachingMode = null);

    /// <summary>TS: buildCreateInput —— 建共享入参唯一出口：路径/名 trim，档位展开，高级参数仅在展开过时并入。</summary>
    public static CreateShareOpts BuildCreateInput(string name, string path, PermTier tier, AdvancedOpts? advanced = null)
    {
        CreateShareOpts baseOpts = TierToAccess(tier);
        var input = new CreateShareOpts
        {
            name = name.Trim(),
            path = path.Trim(),
            fullAccess = baseOpts.fullAccess,
            changeAccess = baseOpts.changeAccess,
            readAccess = baseOpts.readAccess,
        };
        if (advanced?.ConcurrentUserLimit is > 0)
        {
            input = input with { concurrentUserLimit = advanced.ConcurrentUserLimit };
        }
        if (advanced?.CachingMode is { } mode && mode != CachingMode.None)
        {
            input = input with { cachingMode = mode };
        }
        return input;
    }
}
