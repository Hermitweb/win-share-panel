using System.Text.RegularExpressions;
using WinShare.Core.Contracts;

namespace WinShare.Core.Ps;

/// <summary>
/// electron/services/psRuntime.ts 的 C# 移植（同步 stat + 候选枚举 + 失败诊断，零 spawn）。
/// 进程内托管版语义：候选枚举回答「本机是否具备 PowerShell 宿主可用」——
/// PowerShell 7（pwsh）宿主可用性判断 + Windows PowerShell 5.1 回落路径判断，
/// 返回结构与 TS psRuntime.ts 一致（PsRuntimeStatus / PsRuntimeDiagnosis，types.ts 对应 DTO）。
/// 设计原则与 TS 一致：不修改解析策略（NativePathResolver 是唯一真相源）、纯同步 stat 零 spawn。
/// </summary>
public sealed class PsRuntimeService : IPsRuntimeService
{
    private readonly object _gate = new();
    private PsRuntimeStatus? _cached;
    private TestOverrides? _testOverrides;

    /// <summary>
    /// 测试钩子：TS __setTestOverrides 等价（本地 TestOverrides 不属于契约面）。
    /// 生产不生效——测试可注入候选枚举模拟「只有 5.1 / 只有 7.x / 双引擎 / 全缺」。
    /// </summary>
    public sealed record TestOverrides
    {
        public Func<string, bool>? Exists { get; init; }
        public Func<List<PsCandidate>>? Candidates { get; init; }
    }

    public void SetTestOverrides(TestOverrides? overrides)
    {
        lock (_gate)
        {
            _testOverrides = overrides;
        }
    }

    /// <summary>
    /// TS: probePowerShell(deps={}, force=false) — 同步探测 PowerShell 可执行文件；force=true 时重新 stat。
    /// 候选的 exists 标记由 NativePathResolver 统一计算（接受 deps.exists 注入），
    /// 本函数不单独对 resolvedPath 二次 stat（避免两处 stat 逻辑漂移）。
    /// </summary>
    public PsRuntimeStatus ProbePowerShell(NativePathDeps? deps = null, bool force = false)
    {
        lock (_gate)
        {
            if (_cached != null && !force)
            {
                return _cached;
            }
            string resolvedPath = NativePathResolver.ResolveExe(deps);
            bool isAbsolutePath = NativePathResolver.IsAbsolute(resolvedPath) || resolvedPath.StartsWith('/');

            IReadOnlyList<PsCandidate>? all;
            PsEngine engine;
            bool realPathFound;
            IReadOnlyList<PsCandidateSummary>? candidates;

            var candsOverride = _testOverrides?.Candidates;
            if (candsOverride is not null)
            {
                all = candsOverride();
                candidates = ToSummaries(all);
                var hit = all.FirstOrDefault(c => c.path == resolvedPath);
                engine = hit?.engine ?? PsEngine.Unknown;
                realPathFound = all.Any(c => c.exists && NativePathResolver.IsAbsolute(c.path));
            }
            else if (deps?.platform is null or "win32")
            {
                // 生产 / win32 测试路径：用 NativePathResolver 统一 stat
                // （它接受 deps.exists 注入，测试可模拟"只有 5.1 / 只有 7.x / 双引擎 / 全缺"）
                all = NativePathResolver.ListCandidates(deps);
                candidates = ToSummaries(all);
                var hit = all.FirstOrDefault(c => c.path == resolvedPath);
                engine = hit?.engine ?? PsEngine.Unknown;
                realPathFound = all.Any(c => c.exists && NativePathResolver.IsAbsolute(c.path));
            }
            else
            {
                engine = PsEngine.Unknown;
                candidates = null;
                // 非 win32 平台：裸名交给 PATH，乐观认为存在
                realPathFound = true;
            }

            // 裸名（交给 PATH）：不 stat，记乐观 exists=true；
            // 绝对路径：exists 直接取候选里该路径的 stat 结果（与 resolver 同源，避免漂移）
            bool exists = !isAbsolutePath
                ? true
                : (candidates?.FirstOrDefault(c => c.path == resolvedPath)?.exists ?? RealExists(resolvedPath));

            _cached = new PsRuntimeStatus
            {
                resolvedPath = resolvedPath,
                exists = exists,
                realPathFound = realPathFound,
                statError = null,
                probedAt = Environment.TickCount64,
                engine = engine,
                candidates = candidates,
            };
            return _cached;
        }
    }

    /// <summary>TS: getPowershellStatus() — 读取最近一次探测结果；从未探测时触发一次。</summary>
    public PsRuntimeStatus GetPowershellStatus() => ProbePowerShell();

    /// <summary>TS: engineLabel(status) — 当前选中引擎的人类可读名（日志/UI 用）。</summary>
    public string EngineLabel(PsRuntimeStatus status)
    {
        return status.engine switch
        {
            PsEngine.WindowsPowerShell => "Windows PowerShell 5.1",
            PsEngine.PowershellCore => "PowerShell 7+（pwsh）",
            _ => status.resolvedPath,
        };
    }

    /// <summary>
    /// TS: diagnosePsRuntime(rawError, deps={}) — 根据一次 PowerShell 调用失败 + 当前探测结果，
    /// 给出可执行修复建议（sfc /scannow、杀软白名单、安装 PowerShell 7 等）。
    /// 多版本兼容：5.1 缺失但 7.x 可用时提示「已自动切到 pwsh.exe（7.x），部分 IIS cmdlet 行为可能有差异」。
    /// </summary>
    public PsRuntimeDiagnosis DiagnosePsRuntime(string rawError, NativePathDeps? deps = null)
    {
        var probe = ProbePowerShell(deps, true);
        var suggestions = new List<string>();

        // 用"真实绝对路径候选是否存在"判断双引擎缺失（裸名不算——PATH 兜底不可靠）
        bool realMissing = !probe.realPathFound;

        if (realMissing)
        {
            suggestions.Add("PowerShell 可执行文件未找到：" + probe.resolvedPath + "（5.1 与 7.x 的真实路径都不存在，仅剩 PATH 裸名兜底）");
            suggestions.Add(@"检查 Windows 是否被精简/优化（如""系统还原""、小智优工具箱等）删除了 %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe");
            suggestions.Add("以管理员身份运行 `sfc /scannow` 修复系统文件，或「设置 → 系统 → 恢复」重置");
            suggestions.Add("或安装 PowerShell 7：`winget install Microsoft.PowerShell`（应用会自动识别 pwsh.exe）");
        }
        else if (!probe.exists)
        {
            suggestions.Add("选中的 " + probe.resolvedPath + " 不存在，但候选里还有其他 PowerShell，请重启应用");
        }
        else
        {
            suggestions.Add("已找到 PowerShell：" + probe.resolvedPath + "（" + EngineLabel(probe) + "）");
            string msg = (rawError ?? "").ToLowerInvariant();
            if (msg.Contains("0x80070003") || msg.Contains("enoent"))
            {
                suggestions.Add("错误码 0x80070003（文件找不到）：进程能枚举到可执行文件但无法启动——多为杀软/EDR/AppLocker 拦截 spawn");
                suggestions.Add("在杀软白名单中放行：" + probe.resolvedPath);
            }
            if (msg.Contains("access is denied") || msg.Contains("拒绝访问"))
            {
                suggestions.Add("拒绝访问：以管理员身份运行 WinShare Panel（打包版已声明 requireAdministrator，非管理员启动会触发 UAC 提示）");
            }
            if (msg.Contains("clixml") || msg.Contains("powershell 命令执行失败"))
            {
                suggestions.Add("PowerShell 已启动但脚本执行失败：多为系统被精简导致 .NET Framework 4.x 损坏（5.1 引擎）或 .NET 8 运行时缺失（7.x 引擎），运行 `sfc /scannow` 或重装对应引擎");
            }
            if (probe.engine == PsEngine.PowershellCore)
            {
                suggestions.Add("当前引擎为 PowerShell 7+（pwsh.exe）：部分 IIS WebAdministration 的 COM 组件只在 5.1 注册，FTP/WebDAV 管理可能不可用——可安装 Windows PowerShell 5.1 后重启应用");
            }
            bool has51 = probe.candidates is not null
                && probe.candidates.Any(c => c.engine == PsEngine.WindowsPowerShell && c.exists && NativePathResolver.IsAbsolute(c.path));
            if (!has51 && probe.engine == PsEngine.PowershellCore)
            {
                suggestions.Add("本机仅有 PowerShell 7（无 5.1）：应用已自动切到 pwsh.exe，核心 SMB 功能可用；IIS（FTP/WebDAV）与 32 位相关 cmdlet 行为可能有差异，详见 docs/COMPATIBILITY.md");
            }
        }

        return new PsRuntimeDiagnosis
        {
            // ok 反映"是否找到真实可用的 PowerShell"：双引擎真实路径全缺时 ok=false
            // （即使裸名兜底让 probe.exists 乐观为 true，也不能算"找到了 PowerShell"）
            ok = probe.realPathFound,
            reason = probe.realPathFound
                ? "PowerShell 可执行文件存在（" + EngineLabel(probe) + "），启动/执行环节失败（详见应用日志 app.log）"
                : "PowerShell 可执行文件未找到（5.1 与 7.x 的真实路径都缺失），所有依赖 PowerShell 的功能不可用",
            suggestions = suggestions,
            rawError = rawError,
        };
    }

    /// <summary>TS: resetPsProbeCache() — 测试钩子：清除缓存，让下一次探测重新 stat。</summary>
    public void ResetPsProbeCache()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private static IReadOnlyList<PsCandidateSummary> ToSummaries(IReadOnlyList<PsCandidate> all) =>
        all.Select(c => new PsCandidateSummary { path = c.path, engine = c.engine, exists = c.exists, note = c.note })
            .ToList();

    // 纯逻辑兜底：deps 未注入 exists 时用 File.Exists 的真实探测（生产路径）
    private static bool RealExists(string p)
    {
        try { return File.Exists(p); }
        catch { return false; }
    }
}

