namespace WinShare.Core.Contracts;

/// <summary>
/// 一键诊断（对应 electron/services/diagnose.ts 导出：runDiagnose / applyFix；
/// 本地类型 Fix = DiagnoseFixKind）。
/// </summary>
public interface IDiagnoseService
{
    /// <summary>TS: runDiagnose(opts: { shareName? })，逐项容错（探测失败记 warn 不崩整体）。</summary>
    Task<IReadOnlyList<DiagnoseItem>> RunDiagnoseAsync(DiagnoseOptions? opts = null);

    /// <summary>TS: applyFix(fix, args?: { shareName? })，执行自动修复并返回说明文本。</summary>
    Task<string> ApplyFixAsync(DiagnoseFixKind fix, DiagnoseOptions? args = null);
}
