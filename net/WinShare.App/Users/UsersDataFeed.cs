using WinShare.Core.Contracts;

namespace WinShare.App.Users;

/// <summary>
/// 用户/组/共享 数据束（对齐 Users.tsx 的 react-query ['users-groups'] + 矩阵源的
/// Promise.all([api.share.list, api.user.list, api.user.groups])；
/// 各源独立成败 → *Ok 标志 + 空列表（对齐 T3a DashboardDataFeed 空馈送降级：
/// 服务缺席不崩不臆造，界面走空态）。
/// </summary>
public sealed record UsersBundle
{
    public IReadOnlyList<LocalUser> Users { get; init; } = Array.Empty<LocalUser>();
    public IReadOnlyList<LocalGroup> Groups { get; init; } = Array.Empty<LocalGroup>();
    public IReadOnlyList<Share> Shares { get; init; } = Array.Empty<Share>();
    /// <summary>users 源取数成功（C# 逐源 catch，以标志表达 TS 的全成/全败语义）。</summary>
    public bool UsersOk { get; init; }
    /// <summary>groups 源取数成功。</summary>
    public bool GroupsOk { get; init; }
    /// <summary>shares 源取数成功（矩阵共享列；TS 侧 .catch(() => []) 静默）。</summary>
    public bool SharesOk { get; init; }
    /// <summary>users 源失败原因（首轮失败提示用；成功=null）。</summary>
    public string? UsersError { get; init; }
    /// <summary>groups 源失败原因。</summary>
    public string? GroupsError { get; init; }
}

/// <summary>用户/组数据馈送（可测试接缝；生产实现=契约服务，缺席源走空馈送）。</summary>
public interface IUsersDataFeed
{
    Task<UsersBundle> GetAsync();
}

/// <summary>
/// 契约服务馈送：users/groups 走 IUserService，shares 走 IShareService；
/// 服务槽位（UsersModule 静态槽，类比 AppServices.SystemService）延迟解析——
/// 服务实现落地并注入后自动点亮，缺席源返回空列表 + Ok=false（如实空态）。
/// </summary>
public sealed class ContractUsersDataFeed : IUsersDataFeed
{
    readonly Func<IUserService?> _user;
    readonly Func<IShareService?> _share;

    public ContractUsersDataFeed(Func<IUserService?> user, Func<IShareService?> share)
    {
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _share = share ?? throw new ArgumentNullException(nameof(share));
    }

    public async Task<UsersBundle> GetAsync()
    {
        var user = _user();
        var share = _share();

        // 三源并行（对齐 TS Promise.all），逐源 catch → 空列表（对齐 T3a 逐源跳过）
        var usersTask = user is null
            ? Task.FromResult<(IReadOnlyList<LocalUser>?, string?)>((null, "IUserService 未注册"))
            : FetchUsers(user.ListUsersAsync());
        var groupsTask = user is null
            ? Task.FromResult<(IReadOnlyList<LocalGroup>?, string?)>((null, "IUserService 未注册"))
            : FetchGroups(user.ListGroupsAsync());
        var sharesTask = share is null
            ? Task.FromResult<(IReadOnlyList<Share>?, string?)>((null, "IShareService 未注册"))
            : FetchShares(share.ListSharesAsync());

        // T3d 最小修复：三任务异构 tuple 无法经 Task.WhenAll 推断（CS0411），改顺序 await（原并行度损失由 T3b 后续收敛）
        var usersTuple = await usersTask;
        var groupsTuple = await groupsTask;
        var sharesTuple = await sharesTask;

        return new UsersBundle
        {
            Users = usersTuple.Item1 ?? Array.Empty<LocalUser>(),
            Groups = groupsTuple.Item1 ?? Array.Empty<LocalGroup>(),
            Shares = sharesTuple.Item1 ?? Array.Empty<Share>(),
            UsersOk = usersTuple.Item1 is not null,
            GroupsOk = groupsTuple.Item1 is not null,
            SharesOk = sharesTuple.Item1 is not null,
            UsersError = usersTuple.Item2,
            GroupsError = groupsTuple.Item2,
        };
    }

    static async Task<(IReadOnlyList<LocalUser>?, string?)> FetchUsers(Task<IReadOnlyList<LocalUser>> t)
    {
        try { return (await t, null); }
        catch (Exception e) { return (null, e.Message); } // 单源失败不影响其他源
    }

    static async Task<(IReadOnlyList<LocalGroup>?, string?)> FetchGroups(Task<IReadOnlyList<LocalGroup>> t)
    {
        try { return (await t, null); }
        catch (Exception e) { return (null, e.Message); }
    }

    static async Task<(IReadOnlyList<Share>?, string?)> FetchShares(Task<IReadOnlyList<Share>> t)
    {
        try { return (await t, null); }
        catch (Exception e) { return (null, e.Message); }
    }
}
