namespace WinShare.Core.Contracts;

/// <summary>
/// PowerShell 运行时探测与诊断（对应 electron/services/psRuntime.ts 导出：
/// probePowerShell / getPowershellStatus / engineLabel / diagnosePsRuntime /
/// resetPsProbeCache。
/// 本地 TestOverrides 与 __setTestOverrides 测试钩子不属于契约面；
/// 版本探测 probePsVersion（electron/lib/psRuntimeProbe.ts）归实现侧，
/// 其 DTO 见 PsVersionInfo）。
/// </summary>
public interface IPsRuntimeService
{
    /// <summary>TS: probePowerShell(deps={}, force=false)（同步）。</summary>
    PsRuntimeStatus ProbePowerShell(NativePathDeps? deps = null, bool force = false);

    /// <summary>TS: getPowershellStatus()（同步，返回缓存探测结果）。</summary>
    PsRuntimeStatus GetPowershellStatus();

    /// <summary>TS: engineLabel(status)（同步，引擎+路径的人类可读标签）。</summary>
    string EngineLabel(PsRuntimeStatus status);

    /// <summary>TS: diagnosePsRuntime(rawError, deps={})（同步，按失败原因给修复建议）。</summary>
    PsRuntimeDiagnosis DiagnosePsRuntime(string rawError, NativePathDeps? deps = null);

    /// <summary>TS: resetPsProbeCache()（测试钩子：清除缓存，下次探测重新 stat）。</summary>
    void ResetPsProbeCache();
}
