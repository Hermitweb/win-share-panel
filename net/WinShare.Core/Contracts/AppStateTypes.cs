namespace WinShare.Core.Contracts;

/// <summary>主题模式（TS: AppState.theme）。</summary>
public enum ThemeMode { Light, Dark }

/// <summary>
/// 告警规则（TS: types.ts AlertRules；idleAlertMinutes 为 null 表示关闭。
/// patch 语义下全部字段可空，表示"只更新非空键"，对应 TS AppStatePatch 的 Partial&lt;AlertRules&gt;）。
/// </summary>
public record AlertRules
{
    public int? idleAlertMinutes { get; init; }
    public bool smb1Alert { get; init; }
    public bool weakPasswordAlert { get; init; }
    /// <summary>共享所在盘剩余空间低于该 GB 数列黄标/详情告警。</summary>
    public int diskLowGb { get; init; }
}

/// <summary>仪表板连接数采样点（TS: types.ts HistoryPoint）。</summary>
public record HistoryPoint
{
    /// <summary>毫秒时间戳。</summary>
    public long ts { get; init; }
    public int sessions { get; init; }
    public int openFiles { get; init; }
}

/// <summary>操作日志动作（TS: JournalEntry.action）。</summary>
public enum JournalAction { Create, Delete, ToggleOff, Permset }

/// <summary>
/// 操作日志条目（TS: types.ts JournalEntry；snapshot 为 TS 联合 ShareSnapshot | SharePermission[]，
/// C# 侧用 object? 承载，delete/toggle-off 载 ShareSnapshot、permset 载权限数组）。
/// </summary>
public record JournalEntry
{
    public string id { get; init; }
    /// <summary>毫秒时间戳。</summary>
    public long ts { get; init; }
    public JournalAction action { get; init; }
    public Protocol protocol { get; init; }
    public string name { get; init; }
    public string? detail { get; init; }
    public object? snapshot { get; init; }
    public bool undoable { get; init; }
}

/// <summary>新增日志条目草案（TS: stateStore.addJournal 入参 Omit&lt;JournalEntry, 'id' | 'ts'&gt;，id/ts 由 store 生成）。</summary>
public record JournalEntryDraft
{
    public JournalAction action { get; init; }
    public Protocol protocol { get; init; }
    public string name { get; init; }
    public string? detail { get; init; }
    public object? snapshot { get; init; }
    public bool undoable { get; init; }
}

/// <summary>应用级持久状态（TS: types.ts AppState）。</summary>
public record AppState
{
    public ThemeMode theme { get; init; }
    /// <summary>false=新手模式（隐藏 SMB1/枚举/租约等高级概念）。</summary>
    public bool advancedMode { get; init; }
    /// <summary>${protocol}:${name} 复合 key。</summary>
    public IReadOnlyList<string> pinned { get; init; }
    public AlertRules alertRules { get; init; }
    public bool autoStart { get; init; }
    public IReadOnlyList<HistoryPoint> dashboardHistory { get; init; }
    public IReadOnlyList<JournalEntry> journal { get; init; }
}

/// <summary>
/// 状态补丁（TS: types.ts AppStatePatch = Omit&lt;Partial&lt;AppState&gt;,'alertRules'&gt; &amp; { alertRules?: Partial&lt;AlertRules&gt; }；
/// 全可空，主进程与既有值浅合并）。
/// </summary>
public record AppStatePatch
{
    public ThemeMode? theme { get; init; }
    public bool? advancedMode { get; init; }
    public IReadOnlyList<string>? pinned { get; init; }
    public AlertRules? alertRules { get; init; }
    public bool? autoStart { get; init; }
    public IReadOnlyList<HistoryPoint>? dashboardHistory { get; init; }
    public IReadOnlyList<JournalEntry>? journal { get; init; }
}
