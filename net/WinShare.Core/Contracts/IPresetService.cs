namespace WinShare.Core.Contracts;

/// <summary>
/// 权限预设管理（对应 electron/services/preset.ts 导出：
/// resolveAccount / listPresets / getPreset / savePreset / updatePreset /
/// deletePreset / duplicatePreset / applyPreset / exportPresets / importPresets）。
/// </summary>
public interface IPresetService
{
    /// <summary>TS: resolveAccount(account)（内置名→本机全限定账号，同步）。</summary>
    string ResolveAccount(string account);

    Task<IReadOnlyList<PermissionPreset>> ListPresetsAsync();
    Task<PermissionPreset?> GetPresetAsync(string id);
    Task SavePresetAsync(PermissionPreset preset);
    /// <summary>TS: updatePreset(id, updates: Partial&lt;PermissionPreset&gt;)。</summary>
    Task UpdatePresetAsync(string id, PermissionPreset updates);
    Task DeletePresetAsync(string id);
    /// <summary>TS: duplicatePreset(id, newName?)。</summary>
    Task<PermissionPreset> DuplicatePresetAsync(string id, string? newName = null);
    /// <summary>TS: applyPreset(shareName, presetId, mode: 'overwrite' | 'merge')，复用 user.setSharePermissions 事务补偿。</summary>
    Task ApplyPresetAsync(string shareName, string presetId, ApplyPresetMode mode);
    /// <summary>TS: exportPresets()（自定义预设 JSON 导出）。</summary>
    Task<string> ExportPresetsAsync();
    /// <summary>TS: importPresets(json)，返回 { imported, skipped, errors }；2MB/500 条护栏。</summary>
    Task<ImportReport> ImportPresetsAsync(string json);
}
