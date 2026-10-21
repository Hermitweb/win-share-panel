namespace WinShare.Core.Contracts;

/// <summary>一键诊断可自动修复项键（TS: types.ts DiagnoseFixKind）。</summary>
public enum DiagnoseFixKind { ServiceLanman, FirewallSmb, AclReadEveryone }

/// <summary>诊断项状态（TS: DiagnoseItem.status）。</summary>
public enum DiagnoseStatus { Pass, Warn, Fail }

/// <summary>一键诊断单项结果（TS: types.ts DiagnoseItem；fix 存在时 UI 呈现修复按钮）。</summary>
public record DiagnoseItem
{
    public string key { get; init; }
    public string label { get; init; }
    public DiagnoseStatus status { get; init; }
    public string detail { get; init; }
    public DiagnoseFixKind? fix { get; init; }
}

/// <summary>诊断入参（TS: diagnose.ts runDiagnose(opts: { shareName?: string })）。</summary>
public record DiagnoseOptions
{
    public string? shareName { get; init; }
}
