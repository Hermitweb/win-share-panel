namespace WinShare.Core.Contracts;

/// <summary>
/// 本地用户/组与共享权限（对应 electron/services/user.ts 导出：
/// listUsers / listGroups / getUser / createUser / updateUser / deleteUser /
/// setUserPassword / enableUser / disableUser / renameUser /
/// createGroup / deleteGroup / updateGroup / renameGroup / addGroupMember /
/// removeGroupMember / getSharePermissions / getUserSharePermissions /
/// setSharePermissions / getNtfsPermissions）。
/// </summary>
public interface IUserService
{
    Task<IReadOnlyList<LocalUser>> ListUsersAsync();
    Task<IReadOnlyList<LocalGroup>> ListGroupsAsync();
    Task<LocalUser> GetUserAsync(string name);
    Task CreateUserAsync(CreateUserOpts opts);
    /// <summary>TS: updateUser(name, opts: UpdateUserOpts)。</summary>
    Task UpdateUserAsync(string name, UpdateUserOpts opts);
    Task DeleteUserAsync(string name);
    Task SetUserPasswordAsync(string name, string password);
    Task EnableUserAsync(string name);
    Task DisableUserAsync(string name);
    Task RenameUserAsync(string oldName, string newName);
    Task CreateGroupAsync(CreateGroupOpts opts);
    Task DeleteGroupAsync(string name);
    /// <summary>TS: updateGroup(name, description)。</summary>
    Task UpdateGroupAsync(string name, string description);
    Task RenameGroupAsync(string name, string newName);
    Task AddGroupMemberAsync(string group, string member);
    Task RemoveGroupMemberAsync(string group, string member);
    /// <summary>TS: user.getSharePermissions(shareName)（读路径，与 IShareService 同名方法等价）。</summary>
    Task<IReadOnlyList<SharePermission>> GetSharePermissionsAsync(string shareName);
    /// <summary>TS: getUserSharePermissions(username)，该用户跨共享权限（单次 PowerShell 批量查询）。</summary>
    Task<IReadOnlyList<SharePermission>> GetUserSharePermissionsAsync(string username);
    /// <summary>TS: setSharePermissions(shareName, perms)，含备份+回滚事务补偿。</summary>
    Task SetSharePermissionsAsync(string shareName, IReadOnlyList<SharePermission> perms);
    /// <summary>TS: getNtfsPermissions(path)。</summary>
    Task<NtfsAcl> GetNtfsPermissionsAsync(string path);
}
