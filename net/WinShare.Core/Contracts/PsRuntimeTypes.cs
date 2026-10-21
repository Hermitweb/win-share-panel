namespace WinShare.Core.Contracts;

/// <summary>PowerShell 引擎（TS: types.ts PsEngine）。</summary>
public enum PsEngine { WindowsPowerShell, PowershellCore, Unknown }

/// <summary>
/// PowerShell 候选（TS: electron/lib/nativePaths.ts PsCandidate，用于同步路径解析层；
/// 与 types.ts PsCandidateSummary 字段一致，两个名字对应两处 TS 定义）。
/// </summary>
public record PsCandidate
{
    public string path { get; init; }
    public PsEngine engine { get; init; }
    public bool exists { get; init; }
    public string note { get; init; }
}

/// <summary>PowerShell 候选摘要（TS: types.ts PsCandidateSummary，UI 展示"这台机器有哪些 PowerShell"）。</summary>
public record PsCandidateSummary
{
    public string path { get; init; }
    public PsEngine engine { get; init; }
    public bool exists { get; init; }
    public string note { get; init; }
}

/// <summary>
/// PowerShell 运行时探测状态（TS: types.ts PsRuntimeStatus，v1.4.0 多版本兼容：
/// 5.1 优先，缺失兜底 7.x，再缺则裸名 PATH 兜底）。
/// </summary>
public record PsRuntimeStatus
{
    public string resolvedPath { get; init; }
    public bool exists { get; init; }
    /// <summary>是否至少存在一个真实绝对路径候选（裸名不算）。</summary>
    public bool realPathFound { get; init; }
    /// <summary>stat 抛错时的可读原因（如权限不足）。</summary>
    public string? statError { get; init; }
    /// <summary>探测时间戳（毫秒）。</summary>
    public long probedAt { get; init; }
    public PsEngine? engine { get; init; }
    public IReadOnlyList<PsCandidateSummary>? candidates { get; init; }
}

/// <summary>PowerShell 运行时诊断（TS: types.ts PsRuntimeDiagnosis）。</summary>
public record PsRuntimeDiagnosis
{
    public bool ok { get; init; }
    /// <summary>给渲染层/日志的短文案（中文）。</summary>
    public string reason { get; init; }
    public IReadOnlyList<string> suggestions { get; init; }
    public string? rawError { get; init; }
}

/// <summary>PowerShell 版本探测结果（TS: electron/lib/psRuntimeProbe.ts PsVersionInfo）。</summary>
public record PsVersionInfo
{
    /// <summary>$PSVersionTable.PSVersion.Major（5 / 7）。</summary>
    public int major { get; init; }
    public string full { get; init; }
    public string exe { get; init; }
    /// <summary>探测时间戳（毫秒）。</summary>
    public long probedAt { get; init; }
}

/// <summary>
/// 原生路径解析依赖注入（TS: electron/lib/nativePaths.ts NativePathDeps，
/// 各可选字段为测试注入点）。
/// </summary>
public record NativePathDeps
{
    public string? platform { get; init; }
    /// <summary>'ia32' 表示 32 位进程。</summary>
    public string? arch { get; init; }
    public string? systemRoot { get; init; }
    public string? programFilesNative { get; init; }
    public Func<string, bool>? exists { get; init; }
}
