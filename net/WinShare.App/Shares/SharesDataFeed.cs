using WinShare.Core.Contracts;

namespace WinShare.App.Shares;

/// <summary>
/// 共享页数据束（对齐 Shares.tsx 的 react-query 源集 + 不可用时如实 null）：
/// - Shares：adapter.list（按协议筛选；null=源不可用 → 界面如实空态，不臆造）
/// - Presets/Users/Groups：preset.list / user.list / user.groups（TS catch(() => []) 语义=空列表）
/// - Disks：disk.usages（null=查盘失败，降级空水位表，不显示 0%）
/// - ProtocolCaps：protocol.detect（null=未加载，创建选项默认视为可用）
/// - OsInfo：system.osInfo（null → iisBlocked=false，对齐 TS osInfo ? !features.iisAvailable : false）
/// </summary>
public sealed record SharesBundle
{
    public IReadOnlyList<Share>? Shares { get; init; }
    public IReadOnlyList<PermissionPreset> Presets { get; init; } = Array.Empty<PermissionPreset>();
    public IReadOnlyList<LocalUser> Users { get; init; } = Array.Empty<LocalUser>();
    public IReadOnlyList<LocalGroup> Groups { get; init; } = Array.Empty<LocalGroup>();
    public IReadOnlyList<DiskUsage>? Disks { get; init; }
    public Dictionary<Protocol, ProtocolFeatureState>? ProtocolCaps { get; init; }
    public OsInfo? OsInfo { get; init; }
    /// <summary>TS api.adapter.capabilities()：各协议能力位（supports* 门控行内操作）。</summary>
    public Dictionary<Protocol, ProtocolCapabilities?>? ProtocolCapabilitiesMap { get; init; }
}

/// <summary>
/// 共享页数据馈送抽象（可测试接缝，同 DashboardDataFeed 模式）：
/// 生产实现 = ContractSharesFeed（按契约服务，逐源 try/catch）；测试注入假束。
/// </summary>
public interface ISharesDataFeed
{
    /// <summary>TS api.adapter.list(proto)；null=共享源不可用（服务缺席/失败）。</summary>
    Task<IReadOnlyList<Share>?> ListSharesAsync(string? protocol);
    Task<IReadOnlyList<PermissionPreset>?> ListPresetsAsync();
    /// <summary>TS api.user.list/groups：失败 catch(() => []) → 空列表（源可用但空）。</summary>
    Task<(IReadOnlyList<LocalUser> users, IReadOnlyList<LocalGroup> groups)> ListUsersGroupsAsync();
    /// <summary>TS api.disk.usages().catch(() => [])；null=查盘失败降级（水位隐藏）。</summary>
    Task<IReadOnlyList<DiskUsage>?> ListDiskUsagesAsync();
    Task<OsInfo?> GetOsInfoAsync();
    /// <summary>TS api.protocol.detect；null=能力未加载。</summary>
    Task<Dictionary<Protocol, ProtocolFeatureState>?> DetectProtocolsAsync();
    /// <summary>TS api.adapter.capabilities（未注册协议的能力为 null）；源缺席返回 null。</summary>
    Task<Dictionary<Protocol, ProtocolCapabilities?>?> GetCapabilitiesMapAsync();
    /// <summary>TS api.protocol.install；服务缺席抛 ServiceUnavailableException。</summary>
    Task InstallProtocolAsync(Protocol protocol);
}

/// <summary>契约服务不可用（占位降级路径专用）。</summary>
public sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message) : base(message) { }
}

/// <summary>
/// 契约服务馈送：各源独立 try/catch（对齐 TS per-query error 语义），
/// 未注入的服务保持 null。非 SMB 协议的列表走 IProtocolService.AdapterListAsync；
/// 协议路由缺席时仅 SMB 列表可经 IShareService 提供，其余协议如实 null。
/// </summary>
public sealed class ContractSharesFeed : ISharesDataFeed
{
    readonly IShareService? _share;
    readonly IPresetService? _preset;
    readonly IUserService? _user;
    readonly IDiskService? _disk;
    readonly ISystemService? _system;
    readonly IProtocolService? _protocol;

    public ContractSharesFeed(
        IShareService? share = null,
        IPresetService? preset = null,
        IUserService? user = null,
        IDiskService? disk = null,
        ISystemService? system = null,
        IProtocolService? protocol = null)
    {
        _share = share; _preset = preset; _user = user; _disk = disk; _system = system; _protocol = protocol;
    }

    public async Task<IReadOnlyList<Share>?> ListSharesAsync(string? protocol)
    {
        try
        {
            if (protocol is null || protocol == "smb")
            {
                Protocol? proto = protocol == "smb" ? Protocol.Smb : null;
                if (_protocol is { } ps) return await ps.AdapterListAsync(proto);
                if (proto is null || proto == Protocol.Smb)
                {
                    if (_share is not null) return await _share.ListSharesAsync();
                    return null; // 协议路由与 SMB 服务都缺席 → 如实空态
                }
            }
            if (_protocol is { } p) return await p.AdapterListAsync(ProtocolExt.ParseName(protocol!));
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PermissionPreset>?> ListPresetsAsync()
    {
        try
        {
            if (_preset is null) return null;
            return await _preset.ListPresetsAsync();
        }
        catch { return null; }
    }

    public async Task<(IReadOnlyList<LocalUser> users, IReadOnlyList<LocalGroup> groups)> ListUsersGroupsAsync()
    {
        try
        {
            if (_user is null) return ([], []);
            var users = await _user.ListUsersAsync();
            var groups = await _user.ListGroupsAsync();
            return (users, groups);
        }
        catch { return ([], []); }
    }

    public async Task<IReadOnlyList<DiskUsage>?> ListDiskUsagesAsync()
    {
        try
        {
            if (_disk is null) return null;
            return await _disk.GetDiskUsagesAsync();
        }
        catch { return null; }
    }

    public async Task<OsInfo?> GetOsInfoAsync()
    {
        try
        {
            if (_system is null) return null;
            return await _system.GetOsInfoAsync();
        }
        catch { return null; }
    }

    public async Task<Dictionary<Protocol, ProtocolFeatureState>?> DetectProtocolsAsync()
    {
        try
        {
            if (_protocol is null) return null;
            return await _protocol.DetectProtocolsAsync();
        }
        catch { return null; }
    }

    public Task<Dictionary<Protocol, ProtocolCapabilities?>?> GetCapabilitiesMapAsync()
    {
        Dictionary<Protocol, ProtocolCapabilities?>? result;
        try
        {
            if (_protocol is null) return Task.FromResult<Dictionary<Protocol, ProtocolCapabilities?>?>(null);
            result = _protocol.GetCapabilitiesMap();
        }
        catch { return Task.FromResult<Dictionary<Protocol, ProtocolCapabilities?>?>(null); }
        return Task.FromResult(result);
    }

    public Task InstallProtocolAsync(Protocol protocol)
    {
        if (_protocol is null)
            return Task.FromException(new ServiceUnavailableException("协议安装需要协议路由契约服务（未接入）"));
        return _protocol.InstallProtocolAsync(protocol);
    }
}

/// <summary>空馈送（服务层未接前默认）：全 null/空，界面走空态不崩（对齐 NoopDashboardFeed 写法）。</summary>
public sealed class NoopSharesFeed : ISharesDataFeed
{
    public Task<IReadOnlyList<Share>?> ListSharesAsync(string? protocol) =>
        Task.FromResult<IReadOnlyList<Share>?>(null);
    public Task<IReadOnlyList<PermissionPreset>?> ListPresetsAsync() =>
        Task.FromResult<IReadOnlyList<PermissionPreset>?>(null);
    public Task<(IReadOnlyList<LocalUser>, IReadOnlyList<LocalGroup>)> ListUsersGroupsAsync() =>
        Task.FromResult<(IReadOnlyList<LocalUser>, IReadOnlyList<LocalGroup>)>(([], []));
    public Task<IReadOnlyList<DiskUsage>?> ListDiskUsagesAsync() =>
        Task.FromResult<IReadOnlyList<DiskUsage>?>(null);
    public Task<OsInfo?> GetOsInfoAsync() => Task.FromResult<OsInfo?>(null);
    public Task<Dictionary<Protocol, ProtocolFeatureState>?> DetectProtocolsAsync() =>
        Task.FromResult<Dictionary<Protocol, ProtocolFeatureState>?>(null);
    public Task<Dictionary<Protocol, ProtocolCapabilities?>?> GetCapabilitiesMapAsync() =>
        Task.FromResult<Dictionary<Protocol, ProtocolCapabilities?>?>(null);
    public Task InstallProtocolAsync(Protocol protocol) =>
        Task.FromException(new ServiceUnavailableException("协议安装需要协议路由契约服务（未接入）"));
}

static class ProtocolExt
{
    public static Protocol ParseName(string? s) => s?.ToLowerInvariant() switch
    {
        "smb" => Protocol.Smb,
        "nfs" => Protocol.Nfs,
        "ftp" => Protocol.Ftp,
        "webdav" => Protocol.Webdav,
        _ => Protocol.Smb,
    };
}
