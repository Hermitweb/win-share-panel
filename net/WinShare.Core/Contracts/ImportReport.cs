namespace WinShare.Core.Contracts;

/// <summary>
/// 导入汇总（TS: share.ts importConfig 与 preset.ts importPresets 共用的
/// { imported, skipped, errors } 返回形状）。
/// </summary>
public record ImportReport
{
    public int imported { get; init; }
    public int skipped { get; init; }
    public IReadOnlyList<string> errors { get; init; }
}
