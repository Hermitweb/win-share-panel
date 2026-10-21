namespace WinShare.Core.Contracts;

/// <summary>权限预设条目（TS: types.ts PresetEntry）。</summary>
public record PresetEntry
{
    public string account { get; init; }
    public AccountType accountType { get; init; }
    /// <summary>TS 侧 access 白名单仅 Full/Change/Read（applyPreset 校验）。</summary>
    public ShareAccess access { get; init; }
    public bool? deny { get; init; }
}

/// <summary>权限预设（TS: types.ts PermissionPreset）。</summary>
public record PermissionPreset
{
    public string id { get; init; }
    public string name { get; init; }
    public string description { get; init; }
    public bool builtIn { get; init; }
    public IReadOnlyList<PresetEntry> entries { get; init; }
    public string? category { get; init; }
    public string? createdAt { get; init; }
    public string? updatedAt { get; init; }
}

/// <summary>预设应用模式（TS: applyPreset 第三参 'overwrite' | 'merge'）。</summary>
public enum ApplyPresetMode { Overwrite, Merge }
