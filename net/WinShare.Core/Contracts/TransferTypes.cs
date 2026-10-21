namespace WinShare.Core.Contracts;

/// <summary>设置迁移常量与 DTO（TS: electron/services/transfer.ts）。</summary>
public static class Transfer
{
    /// <summary>TS: TRANSFER_FORMAT。</summary>
    public const string Format = "winshare-panel/settings";
    /// <summary>TS: TRANSFER_VERSION。</summary>
    public const int Version = 1;
    /// <summary>TS: MAX_JSON_BYTES（512KB，先拒超大再 parse）。</summary>
    public const int MaxJsonBytes = 512 * 1024;
}

/// <summary>设置导出负载（TS: transfer.ts SettingsPayload；刻意不含 dashboardHistory/journal）。</summary>
public record SettingsPayload
{
    public ThemeMode theme { get; init; }
    public bool advancedMode { get; init; }
    public IReadOnlyList<string> pinned { get; init; }
    public AlertRules alertRules { get; init; }
    public bool autoStart { get; init; }
}

/// <summary>设置导出包（TS: transfer.ts SettingsBundle）。</summary>
public record SettingsBundle
{
    public string format { get; init; }
    public int version { get; init; }
    public string exportedAt { get; init; }
    public SettingsPayload settings { get; init; }
}

/// <summary>设置导入结果（TS: transfer.ts ImportResult，applied=实际被应用的字段名）。</summary>
public record SettingsImportResult
{
    public IReadOnlyList<string> applied { get; init; }
}
