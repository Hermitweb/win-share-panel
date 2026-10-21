using WinShare.App.Shares;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Shares;

/// <summary>
/// 契约服务 stub（T3b 验收约定：Core 服务实现本轮不做，测试用 stub 注入契约接口；
/// 生产运行期缺席走 SharesServices 空槽位 → NoopSharesFeed 空馈送降级路径）。
/// </summary>

/// <summary>可编排行为/结果的 IShareService stub。</summary>
public sealed class FakeShareService : IShareService
{
    public List<Share> Shares { get; } = new();
    public List<SharePermission> Permissions { get; } = new();
    public ShareConnections? Connections { get; set; }
    public List<ShareOpenFile> OpenFiles { get; set; } = new();
    public CloseOpenFilesResult? CloseResult { get; set; }
    public string ExportJson { get; set; } = "{}";
    public ImportReport? ImportReport { get; set; }
    public List<(string name, CreateShareOpts opts)> Created { get; } = new();
    public List<(string name, UpdateShareOpts opts)> Updated { get; } = new();
    public List<(string name, bool enabled)> Toggled { get; } = new();
    public List<string> Deleted { get; } = new();
    /// <summary>命中即抛异常（模拟 allSettled 的 rejected 项）。</summary>
    public HashSet<string> FailOn { get; } = new();
    public ShareSnapshot? Restored { get; set; }

    public Task<IReadOnlyList<Share>> ListSharesAsync() => Task.FromResult<IReadOnlyList<Share>>(Shares);
    public Task<Share> GetShareAsync(string name) => Task.FromResult(Shares.FirstOrDefault(s => s.name == name)
        ?? throw new KeyNotFoundException($"share not found: {name}"));
    public Task<Share> CreateShareAsync(CreateShareOpts opts)
    {
        var share = new Share
        {
            name = opts.name, path = opts.path, description = opts.description ?? "",
            protocol = Protocol.Smb, type = ShareType.Disk, hidden = false, encrypted = opts.encrypted ?? false,
            concurrentUsers = 0, status = ShareStatus.Enabled, cached = false,
        };
        Shares.Add(share);
        Created.Add((opts.name, opts));
        return Task.FromResult(share);
    }
    public Task<Share> UpdateShareAsync(string name, UpdateShareOpts opts)
    {
        Updated.Add((name, opts));
        return Task.FromResult(Shares.FirstOrDefault(s => s.name == name) ?? new Share
        {
            name = name, path = @"E:\DocsFallback", description = "", protocol = Protocol.Smb,
            type = ShareType.Disk, hidden = false, encrypted = false, concurrentUsers = 0,
            status = ShareStatus.Enabled, cached = false,
        });
    }
        public Task DeleteShareAsync(string name)
    {
        if (FailOn.Contains(name)) return Task.FromException(new Exception("模拟失败"));
        Deleted.Add(name);
        return Task.CompletedTask;
    }
    public Task RestoreShareAsync(ShareSnapshot snapshot) { Restored = snapshot; return Task.CompletedTask; }
    public Task ToggleShareAsync(string name, bool enabled)
    {
        if (FailOn.Contains(name)) return Task.FromException(new Exception("模拟失败"));
        Toggled.Add((name, enabled));
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<SharePermission>> GetSharePermissionsAsync(string name) =>
        Task.FromResult<IReadOnlyList<SharePermission>>(Permissions);
    public Task<ShareConnections> GetShareConnectionsAsync(string name)
    {
        if (FailOn.Contains(name)) return Task.FromException<ShareConnections>(new Exception("模拟失败"));
        return Task.FromResult(Connections ?? new ShareConnections { concurrentUsers = 0, clientConnections = Array.Empty<ClientConnection>() });
    }
    public Task<IReadOnlyList<ShareOpenFile>> GetShareOpenFilesAsync(string name) =>
        Task.FromResult<IReadOnlyList<ShareOpenFile>>(OpenFiles);
    public Task<CloseOpenFilesResult> CloseShareOpenFilesAsync(string name) => Task.FromResult(
        CloseResult ?? new CloseOpenFilesResult { closed = OpenFiles.Count, failed = 0 });
    public Task<string> ExportConfigAsync() => Task.FromResult(ExportJson);
    public Task<ImportReport> ImportConfigAsync(string json) => Task.FromResult(
        ImportReport ?? new ImportReport { imported = 0, skipped = 0, errors = Array.Empty<string>() });
}

/// <summary>IPresetService stub（预设列表 + 应用记录）。</summary>
public sealed class FakePresetService : IPresetService
{
    public List<PermissionPreset> Presets { get; } = new();
    public List<(string share, string preset, ApplyPresetMode mode)> Applied { get; } = new();
    public string ResolveAccount(string account) => account;
    public Task<IReadOnlyList<PermissionPreset>> ListPresetsAsync() =>
        Task.FromResult<IReadOnlyList<PermissionPreset>>(Presets);
    public Task<PermissionPreset?> GetPresetAsync(string id) =>
        Task.FromResult<PermissionPreset?>(Presets.FirstOrDefault(p => p.id == id));
    public Task SavePresetAsync(PermissionPreset preset) { Presets.Add(preset); return Task.CompletedTask; }
    public Task UpdatePresetAsync(string id, PermissionPreset updates) { return Task.CompletedTask; }
    public Task DeletePresetAsync(string id) { return Task.CompletedTask; }
    public Task<PermissionPreset> DuplicatePresetAsync(string id, string? newName = null) =>
        Task.FromResult(new PermissionPreset
        {
            id = "dup", name = newName ?? "复制", description = "", builtIn = false,
            entries = Array.Empty<PresetEntry>(),
        });
    public Task ApplyPresetAsync(string shareName, string presetId, ApplyPresetMode mode)
    {
        Applied.Add((shareName, presetId, mode));
        return Task.CompletedTask;
    }
    public Task<string> ExportPresetsAsync() => Task.FromResult("[]");
    public Task<ImportReport> ImportPresetsAsync(string json) => Task.FromResult(new ImportReport
    {
        imported = 0, skipped = 0, errors = Array.Empty<string>(),
    });
}

/// <summary>IUserService stub（候选账号 + 权限保存记录）。</summary>
public sealed class FakeUserService : IUserService
{
    public List<LocalUser> Users { get; } = new();
    public List<LocalGroup> Groups { get; } = new();
    public List<(string share, IReadOnlyList<SharePermission> perms)> Saved { get; } = new();
    public NtfsAcl? Ntfs { get; set; }

    public Task<IReadOnlyList<LocalUser>> ListUsersAsync() =>
        Task.FromResult<IReadOnlyList<LocalUser>>(Users);
    public Task<IReadOnlyList<LocalGroup>> ListGroupsAsync() =>
        Task.FromResult<IReadOnlyList<LocalGroup>>(Groups);
    public Task<LocalUser> GetUserAsync(string name) => Task.FromResult(Users[0]);
    public Task CreateUserAsync(CreateUserOpts opts) => Task.CompletedTask;
    public Task UpdateUserAsync(string name, UpdateUserOpts opts) => Task.CompletedTask;
    public Task DeleteUserAsync(string name) => Task.CompletedTask;
    public Task SetUserPasswordAsync(string name, string password) => Task.CompletedTask;
    public Task EnableUserAsync(string name) => Task.CompletedTask;
    public Task DisableUserAsync(string name) => Task.CompletedTask;
    public Task RenameUserAsync(string oldName, string newName) => Task.CompletedTask;
    public Task CreateGroupAsync(CreateGroupOpts opts) => Task.CompletedTask;
    public Task DeleteGroupAsync(string name) => Task.CompletedTask;
    public Task UpdateGroupAsync(string name, string description) => Task.CompletedTask;
    public Task RenameGroupAsync(string oldName, string newName) => Task.CompletedTask;
    public Task AddGroupMemberAsync(string group, string member) => Task.CompletedTask;
    public Task RemoveGroupMemberAsync(string group, string member) => Task.CompletedTask;
    public Task<IReadOnlyList<SharePermission>> GetSharePermissionsAsync(string shareName) =>
        Task.FromResult<IReadOnlyList<SharePermission>>(Array.Empty<SharePermission>());
    public Task<IReadOnlyList<SharePermission>> GetUserSharePermissionsAsync(string username) =>
        Task.FromResult<IReadOnlyList<SharePermission>>(Array.Empty<SharePermission>());
    public Task SetSharePermissionsAsync(string shareName, IReadOnlyList<SharePermission> perms)
    {
        Saved.Add((shareName, perms));
        return Task.CompletedTask;
    }
    public Task<NtfsAcl> GetNtfsPermissionsAsync(string path) => Task.FromResult(
        Ntfs ?? new NtfsAcl { path = path, entries = Array.Empty<NtfsAclEntry>() });
}

/// <summary>IDiskService stub。</summary>
public sealed class FakeDiskService : IDiskService
{
    public IReadOnlyList<DiskUsage> Usages { get; set; } = Array.Empty<DiskUsage>();
    public string SuggestRootResult { get; set; } = "E:\\Share";
    public Task<IReadOnlyList<DiskUsage>> GetDiskUsagesAsync() => Task.FromResult(Usages);
    public string? DriveOfPath(string p) =>
        p?.StartsWith("E:") == true ? "E:" : p?.StartsWith("D:") == true ? "D:" : null;
    public bool IsUnc(string p) => p?.StartsWith(@"\\") == true;
    public string SuggestShareRoot(IReadOnlyList<DiskUsage> usages, string userHome = "") => "E:\\";
    public Task<string> SuggestRootAsync() => Task.FromResult(SuggestRootResult);
}

/// <summary>可编排的共享页数据馈送（对齐 ISharesDataFeed 的源级失败语义）。</summary>
public sealed class FakeSharesFeed : ISharesDataFeed
{
    public IReadOnlyList<Share>? Shares { get; set; }
    public IReadOnlyList<PermissionPreset>? Presets { get; set; }
    public IReadOnlyList<LocalUser>? Users { get; set; }
    public IReadOnlyList<LocalGroup>? Groups { get; set; }
    public IReadOnlyList<DiskUsage>? Disks { get; set; }
    public OsInfo? OsInfo { get; set; }
    public Dictionary<Protocol, ProtocolFeatureState>? Caps { get; set; }
    public Dictionary<Protocol, ProtocolCapabilities?>? CapMap { get; set; }
    public List<Protocol> Installed { get; } = new();

    public Task<IReadOnlyList<Share>?> ListSharesAsync(string? protocol)
    {
        IReadOnlyList<Share>? result;
        if (Shares is null)
        {
            result = null;
        }
        else if (protocol is null)
        {
            result = Shares;
        }
        else
        {
            result = Shares.Where(s => s.protocol.ToString().ToLowerInvariant() == protocol).ToList();
        }
        return Task.FromResult(result);
    }
    public Task<IReadOnlyList<PermissionPreset>?> ListPresetsAsync() =>
        Task.FromResult<IReadOnlyList<PermissionPreset>?>(Presets);
    public Task<(IReadOnlyList<LocalUser> users, IReadOnlyList<LocalGroup> groups)> ListUsersGroupsAsync() =>
        Task.FromResult((users: Users ?? new List<LocalUser>(), groups: Groups ?? new List<LocalGroup>()));
    public Task<IReadOnlyList<DiskUsage>?> ListDiskUsagesAsync() => Task.FromResult(Disks);
    public Task<OsInfo?> GetOsInfoAsync() => Task.FromResult(OsInfo);
    public Task<Dictionary<Protocol, ProtocolFeatureState>?> DetectProtocolsAsync() => Task.FromResult(Caps);
    public Task<Dictionary<Protocol, ProtocolCapabilities?>?> GetCapabilitiesMapAsync() =>
        Task.FromResult(CapMap);
    public Task InstallProtocolAsync(Protocol protocol)
    {
        Installed.Add(protocol);
        return Task.CompletedTask;
    }
}

/// <summary>常用测试共享（SMB E: 盘，带连接数）。</summary>
public static class TestShares
{
    public static Share Smb(string name = "Docs", int concurrent = 0, string path = @"E:\Docs") => new()
    {
        name = name, path = path, description = "", protocol = Protocol.Smb, type = ShareType.Disk,
        hidden = false, encrypted = false, concurrentUsers = concurrent,
        status = ShareStatus.Enabled, cached = false,
    };

    public static Share Ftp(string name = "ftp-site", int port = 21) => new()
    {
        name = name, path = @"D:\ftproot", description = "", protocol = Protocol.Ftp, type = ShareType.Disk,
        hidden = false, encrypted = false, concurrentUsers = 0,
        status = ShareStatus.Enabled, cached = false, port = port,
    };
}
