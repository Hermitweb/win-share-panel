namespace WinShare.Core.Contracts;

/// <summary>
/// SMB 共享管理（对应 electron/services/share.ts 导出：
/// listShares / getShare / createShare / updateShare / deleteShare / restoreShare /
/// toggleShare / getSharePermissions / getShareConnections / getShareOpenFiles /
/// closeShareOpenFiles / exportConfig / importConfig。
/// TS restoreShare 入参 DisabledRecord 与 types.ts ShareSnapshot 同形，C# 统一用 ShareSnapshot）。
/// </summary>
public interface IShareService
{
    /// <summary>TS: listShares()。</summary>
    Task<IReadOnlyList<Share>> ListSharesAsync();

    /// <summary>TS: getShare(name)。</summary>
    Task<Share> GetShareAsync(string name);

    /// <summary>TS: createShare(opts)。</summary>
    Task<Share> CreateShareAsync(CreateShareOpts opts);

    /// <summary>TS: updateShare(name, opts)。</summary>
    Task<Share> UpdateShareAsync(string name, UpdateShareOpts opts);

    /// <summary>TS: deleteShare(name)。</summary>
    Task DeleteShareAsync(string name);

    /// <summary>TS: restoreShare(r: DisabledRecord)，入参形状=ShareSnapshot。</summary>
    Task RestoreShareAsync(ShareSnapshot snapshot);

    /// <summary>TS: toggleShare(name, enabled)。</summary>
    Task ToggleShareAsync(string name, bool enabled);

    /// <summary>TS: getSharePermissions(name)。</summary>
    Task<IReadOnlyList<SharePermission>> GetSharePermissionsAsync(string name);

    /// <summary>TS: getShareConnections(name)，返回 { concurrentUsers, clientConnections }。</summary>
    Task<ShareConnections> GetShareConnectionsAsync(string name);

    /// <summary>TS: getShareOpenFiles(name)。</summary>
    Task<IReadOnlyList<ShareOpenFile>> GetShareOpenFilesAsync(string name);

    /// <summary>TS: closeShareOpenFiles(name)，返回 { closed, failed }。</summary>
    Task<CloseOpenFilesResult> CloseShareOpenFilesAsync(string name);

    /// <summary>TS: exportConfig()，导出全部共享配置 JSON。</summary>
    Task<string> ExportConfigAsync();

    /// <summary>TS: importConfig(json)，返回 { imported, skipped, errors }。</summary>
    Task<ImportReport> ImportConfigAsync(string json);
}
