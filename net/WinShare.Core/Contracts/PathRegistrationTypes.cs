namespace WinShare.Core.Contracts;

/// <summary>
/// 应用路径注册状态（TS: electron/services/pathRegistration.ts PathRegistration；
/// pathLength 脱敏：只回长度不回完整 PATH）。
/// </summary>
public record PathRegistration
{
    /// <summary>是否已存在本应用目录条目。</summary>
    public bool registered { get; init; }
    public int pathLength { get; init; }
    /// <summary>本次操作结果（供 UI 提示）。</summary>
    public string message { get; init; }
    /// <summary>是否广播了 WM_SETTINGCHANGE（PS 缺失时为 false）。</summary>
    public bool broadcast { get; init; }
}
