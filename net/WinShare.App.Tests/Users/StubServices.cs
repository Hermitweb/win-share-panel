using WinShare.App.Users;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Users;

/// <summary>
/// IUserService 可配置 stub（测试专用；记录调用、支持按名失败注入）。
/// </summary>
public sealed class StubUserService : IUserService
{
    public IReadOnlyList<LocalUser> Users { get; set; } = Array.Empty<LocalUser>();
    public IReadOnlyList<LocalGroup> Groups { get; set; } = Array.Empty<LocalGroup>();
    public IReadOnlyList<SharePermission> Perms { get; set; } = Array.Empty<SharePermission>();
    public NtfsAcl? Ntfs { get; set; }
    public List<string> Calls { get; } = new();
    public HashSet<string> FailNames { get; } = new();
    public bool ThrowOnAny { get; set; }

    void MaybeFail(string name)
    {
        Calls.Add(name);
        if (ThrowOnAny) throw new InvalidOperationException($"stub: {name}");
        if (FailNames.Contains(name)) throw new InvalidOperationException($"stub fail: {name}");
    }

    public Task<IReadOnlyList<LocalUser>> ListUsersAsync() { MaybeFail("ListUsers"); return Task.FromResult(Users); }
    public Task<IReadOnlyList<LocalGroup>> ListGroupsAsync() { MaybeFail("ListGroups"); return Task.FromResult(Groups); }
    public Task<LocalUser> GetUserAsync(string name) { MaybeFail("GetUser:" + name); throw new NotImplementedException(); }
    public Task CreateUserAsync(CreateUserOpts opts) { MaybeFail("CreateUser:" + opts.name); return Task.CompletedTask; }
    public Task UpdateUserAsync(string name, UpdateUserOpts opts) { MaybeFail("UpdateUser:" + name); return Task.CompletedTask; }
    public Task DeleteUserAsync(string name) { MaybeFail("DeleteUser:" + name); return Task.CompletedTask; }
    public Task SetUserPasswordAsync(string name, string password) { MaybeFail("SetPassword:" + name); return Task.CompletedTask; }
    public Task EnableUserAsync(string name) { MaybeFail("EnableUser:" + name); return Task.CompletedTask; }
    public Task DisableUserAsync(string name) { MaybeFail("DisableUser:" + name); return Task.CompletedTask; }
    public Task RenameUserAsync(string oldName, string newName) { MaybeFail("RenameUser:" + oldName); return Task.CompletedTask; }
    public Task CreateGroupAsync(CreateGroupOpts opts) { MaybeFail("CreateGroup:" + opts.name); return Task.CompletedTask; }
    public Task DeleteGroupAsync(string name) { MaybeFail("DeleteGroup:" + name); return Task.CompletedTask; }
    public Task UpdateGroupAsync(string name, string description) { MaybeFail("UpdateGroup:" + name); return Task.CompletedTask; }
    public Task RenameGroupAsync(string name, string newName) { MaybeFail("RenameGroup:" + name); return Task.CompletedTask; }
    public Task AddGroupMemberAsync(string group, string member) { MaybeFail($"AddMember:{group}:{member}"); return Task.CompletedTask; }
    public Task RemoveGroupMemberAsync(string group, string member) { MaybeFail($"RemoveMember:{group}:{member}"); return Task.CompletedTask; }
    public Task<IReadOnlyList<SharePermission>> GetSharePermissionsAsync(string shareName)
    {
        MaybeFail("SharePerms:" + shareName);
        // 每个共享返回同一 stub 权限集（测试按共享名过滤由调用方语义保证）
        return Task.FromResult(Perms);
    }
    public Task<IReadOnlyList<SharePermission>> GetUserSharePermissionsAsync(string username)
    {
        MaybeFail("UserPerms:" + username);
        return Task.FromResult(Perms);
    }
    public Task SetSharePermissionsAsync(string shareName, IReadOnlyList<SharePermission> perms)
    {
        MaybeFail("SetPerms:" + shareName);
        return Task.CompletedTask;
    }
    public Task<NtfsAcl> GetNtfsPermissionsAsync(string path)
    {
        MaybeFail("Ntfs:" + path);
        return Task.FromResult(Ntfs ?? new NtfsAcl { path = path, entries = Array.Empty<NtfsAclEntry>() });
    }
}

/// <summary>IShareService 最小 stub（矩阵共享列 + 权限）。</summary>
public sealed class StubShareService : IShareService
{
    public IReadOnlyList<Share> Shares { get; set; } = Array.Empty<Share>();
    public IReadOnlyList<SharePermission> Perms { get; set; } = Array.Empty<SharePermission>();
    public List<string> Calls { get; } = new();

    public Task<IReadOnlyList<Share>> ListSharesAsync()
    {
        Calls.Add("ListShares");
        return Task.FromResult(Shares);
    }
    public Task<Share> GetShareAsync(string name) => Task.FromException<Share>(new NotImplementedException());
    public Task<Share> CreateShareAsync(CreateShareOpts opts) => Task.FromException<Share>(new NotImplementedException());
    public Task<Share> UpdateShareAsync(string name, UpdateShareOpts opts) => Task.FromException<Share>(new NotImplementedException());
    public Task DeleteShareAsync(string name) => Task.CompletedTask;
    public Task RestoreShareAsync(ShareSnapshot snapshot) => Task.CompletedTask;
    public Task ToggleShareAsync(string name, bool enabled) => Task.CompletedTask;
    public Task<IReadOnlyList<SharePermission>> GetSharePermissionsAsync(string name)
    {
        Calls.Add("GetSharePermissions:" + name);
        return Task.FromResult(Perms);
    }
    public Task<ShareConnections> GetShareConnectionsAsync(string name) =>
        Task.FromResult(new ShareConnections { concurrentUsers = 0, clientConnections = Array.Empty<ClientConnection>() });
    public Task<IReadOnlyList<ShareOpenFile>> GetShareOpenFilesAsync(string name) =>
        Task.FromResult<IReadOnlyList<ShareOpenFile>>(Array.Empty<ShareOpenFile>());
    public Task<CloseOpenFilesResult> CloseShareOpenFilesAsync(string name) =>
        Task.FromResult(new CloseOpenFilesResult());
    public Task<string> ExportConfigAsync() => Task.FromResult("{}");
    public Task<ImportReport> ImportConfigAsync(string json) =>
        Task.FromResult(new ImportReport { imported = 0, skipped = 0, errors = Array.Empty<string>() });
}

/// <summary>可配置读路径 stub（绕过 IShareService 依赖面：矩阵共享列/账号直接给）。</summary>
public sealed class StubUsersDataFeed : IUsersDataFeed
{
    public UsersBundle Bundle { get; set; } = new();
    public Func<UsersBundle>? Next { get; set; }

    public Task<UsersBundle> GetAsync() =>
        Task.FromResult(Next?.Invoke() ?? Bundle);
}

/// <summary>工厂 helper（测试数据）。</summary>
public static class TestUsers
{
    public static LocalUser User(string name, string? fullName = "", bool enabled = true,
        string? description = "", string[]? groups = null, string? lastLogon = null) => new()
    {
        name = name,
        fullName = fullName ?? "",
        enabled = enabled,
        description = description ?? "",
        groups = groups ?? Array.Empty<string>(),
        lastLogon = lastLogon ?? "",
    };

    public static LocalGroup Group(string name, string? description = "", params string[] members) => new()
    {
        name = name,
        description = description ?? "",
        members = members
            .Select(m => new GroupMember
            {
                name = m,
                objectClass = MemberObjectClass.User,
                principalSource = "Local",
            })
            .ToList(),
    };

    public static Share Share(string name, string path = @"C:\data", Protocol protocol = Protocol.Smb,
        ShareType type = ShareType.Disk) => new()
    {
        name = name,
        path = path,
        description = "",
        protocol = protocol,
        type = type,
        hidden = false,
        encrypted = false,
        concurrentUsers = 0,
        status = ShareStatus.Enabled,
        cached = false,
    };

    public static SharePermission Perm(string share, string account, ShareAccess access,
        bool deny = false, AccountType type = AccountType.User) => new()
    {
        shareName = share,
        account = account,
        accountType = type,
        access = access,
        deny = deny,
    };
}
