using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Ps;

namespace WinShare.Core.Tests;

/// <summary>
/// electron/services/psRuntime.test.ts 的 C# 逐例移植（多版本候选 + 引擎识别 + 失败诊断）。
/// 与 TS nativePaths.test.ts 同一手法：注入 exists/arch 模拟「只有 5.1 / 只有 7.x / 双引擎 / 全缺」
/// 四种机器形态，不依赖测试机器的 %SystemRoot% / %ProgramFiles% / process.arch。
/// </summary>
public class PsRuntimeServiceTests
{
    private const string Root = "C:\\Windows";
    private const string Sys = Root + "\\System32";
    private const string Pf = "C:\\Program Files";
    private const string Ps51 = Sys + "\\WindowsPowerShell\\v1.0\\powershell.exe";
    private const string Pwsh7 = Sys + "\\WindowsPowerShell\\v1.0\\pwsh.exe";
    private const string PwshPf = Pf + "\\PowerShell\\7\\pwsh.exe";

    // 统一 deps：64 位进程（arch='x64'，不命中 Sysnative）+ programFilesNative 注入
    private static NativePathDeps Deps(Func<string, bool> exists) => new()
    {
        platform = "win32",
        arch = "x64",
        systemRoot = Root,
        programFilesNative = Pf,
        exists = exists,
    };
    private static NativePathDeps Only51() => Deps(p => p == Ps51);
    private static NativePathDeps Only7() => Deps(p => p == Pwsh7);
    private static NativePathDeps Both() => Deps(p => p == Ps51 || p == Pwsh7 || p == PwshPf);
    private static NativePathDeps None() => Deps(_ => false);

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#probePowerShell 只有 5.1：resolvedPath = 5.1，engine = windowsPowerShell，realPathFound=true]</summary>
    public void Probe_Only51()
    {
        var service = new PsRuntimeService();
        PsRuntimeStatus s = service.ProbePowerShell(Only51());
        Assert.True(s.exists);
        Assert.True(s.realPathFound);
        Assert.Equal(Ps51, s.resolvedPath);
        Assert.Equal(PsEngine.WindowsPowerShell, s.engine);
        // 5 个候选：3 个真实路径 + 2 个裸名
        Assert.Equal(5, s.candidates?.Count);
        var pwshSys = s.candidates?.FirstOrDefault(c => c.path == Pwsh7);
        Assert.False(pwshSys?.exists ?? true);
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#probePowerShell 只有 7.x（System32 视图）：resolvedPath = pwsh，engine = powershellCore]</summary>
    public void Probe_Only7()
    {
        var service = new PsRuntimeService();
        PsRuntimeStatus s = service.ProbePowerShell(Only7());
        Assert.True(s.exists);
        Assert.True(s.realPathFound);
        Assert.Equal(Pwsh7, s.resolvedPath);
        Assert.Equal(PsEngine.PowershellCore, s.engine);
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#probePowerShell 5.1 与 7.x 都在：优先 5.1（不冒险切引擎）]</summary>
    public void Probe_Both_Prefers51()
    {
        var service = new PsRuntimeService();
        PsRuntimeStatus s = service.ProbePowerShell(Both());
        Assert.Equal(Ps51, s.resolvedPath);
        Assert.Equal(PsEngine.WindowsPowerShell, s.engine);
        Assert.True(s.realPathFound);
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#probePowerShell 全都不存在（精简系统）：回退裸名 5.1，realPathFound=false，候选全标记不存在]</summary>
    public void Probe_None_FallsBackToBareName()
    {
        var service = new PsRuntimeService();
        PsRuntimeStatus s = service.ProbePowerShell(None());
        Assert.Equal("powershell.exe", s.resolvedPath);
        Assert.True(s.exists); // 裸名不 stat，乐观
        Assert.False(s.realPathFound); // 没有任何真实绝对路径存在
        var absolute = s.candidates?.Where(c => Regex.IsMatch(c.path, @"^[A-Za-z]:[\\/]")).ToList();
        Assert.Equal(3, absolute?.Count); // 5.1 / 7.x System32 / 7.x PF 三个真实路径
        if (absolute is not null)
        {
            foreach (PsCandidateSummary c in absolute)
            {
                Assert.False(c.exists);
            }
        }
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#engineLabel 5.1 → "Windows PowerShell 5.1"，7.x → "PowerShell 7+（pwsh）"]</summary>
    public void EngineLabel_MapsEngineToReadableName()
    {
        var service = new PsRuntimeService();
        PsRuntimeStatus s51 = service.ProbePowerShell(Only51());
        Assert.Equal("Windows PowerShell 5.1", service.EngineLabel(s51));
        service.ResetPsProbeCache();
        PsRuntimeStatus s7 = service.ProbePowerShell(Only7());
        Assert.Equal("PowerShell 7+（pwsh）", service.EngineLabel(s7));
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#缓存命中：force=false 时不重新 stat]</summary>
    public void CacheHit_NoRestatWhenForceFalse()
    {
        var service = new PsRuntimeService();
        PsRuntimeStatus first = service.ProbePowerShell(Only51());
        PsRuntimeStatus hit = service.GetPowershellStatus();
        Assert.Equal(first.probedAt, hit.probedAt);
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#diagnosePsRuntime 两个引擎的真实路径都缺：realPathFound=false，ok=false，建议 sfc + 安装 PowerShell 7]</summary>
    public void Diagnose_BothEnginesMissing_SuggestsSfcAndInstall7()
    {
        var service = new PsRuntimeService();
        PsRuntimeDiagnosis d = service.DiagnosePsRuntime("ENOENT", None());
        Assert.False(d.ok); // 双引擎真实路径全缺 → ok=false
        Assert.Matches(new Regex("5\\.1 与 7\\.x 的真实路径都缺失"), d.reason);
        string joined = string.Join("|", d.suggestions);
        Assert.Matches(new Regex("sfc /scannow"), joined);
        Assert.Matches(new Regex("winget install Microsoft\\.PowerShell"), joined);
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#diagnosePsRuntime 7.x 引擎 + IIS 相关 cmdlet 失败：提示 5.1 才有 IIS COM]</summary>
    public void Diagnose_7xWithIisFailure_Mentions51OnlyIisCom()
    {
        var service = new PsRuntimeService();
        PsRuntimeDiagnosis d = service.DiagnosePsRuntime("IIS 模块未注册 0x80040154", Only7());
        Assert.True(d.ok);
        Assert.Matches(new Regex("PowerShell 7\\+"), d.reason);
        string joined = string.Join("|", d.suggestions);
        Assert.Matches(new Regex("IIS|5\\.1"), joined);
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#diagnosePsRuntime 5.1 引擎 + 0x80070003（杀软拦截）：建议白名单放行]</summary>
    public void Diagnose_51With0x80070003_SuggestsAntivirusWhitelist()
    {
        var service = new PsRuntimeService();
        PsRuntimeDiagnosis d = service.DiagnosePsRuntime("spawn ENOENT 0x80070003", Only51());
        Assert.True(d.ok);
        string joined = string.Join("|", d.suggestions);
        Assert.Matches(new Regex("杀软|白名单|0x80070003"), joined);
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#diagnosePsRuntime 拒绝访问：建议以管理员运行]</summary>
    public void Diagnose_AccessDenied_SuggestsAdmin()
    {
        var service = new PsRuntimeService();
        PsRuntimeDiagnosis d = service.DiagnosePsRuntime("access is denied", Only51());
        string joined = string.Join("|", d.suggestions);
        Assert.Matches(new Regex("管理员"), joined);
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#diagnosePsRuntime CLIXML 执行失败：建议 sfc（区分 5.1 / 7.x 的 .NET 损坏）]</summary>
    public void Diagnose_ClixmlExecutionFailure_SuggestsSfcOrCorruption()
    {
        var service = new PsRuntimeService();
        PsRuntimeDiagnosis d = service.DiagnosePsRuntime("CLIXML <Objs>", Only51());
        string joined = string.Join("|", d.suggestions);
        Assert.Matches(new Regex("sfc|损坏"), joined);
    }

    [Fact]
    /// <summary>[Source: psRuntime.test.ts#diagnosePsRuntime 本机仅有 7.x（无 5.1）：额外提示"核心 SMB 可用，IIS/32 位 cmdlet 可能差异"]</summary>
    public void Diagnose_Only7No51_MentionsCoreSmbAvailable()
    {
        var service = new PsRuntimeService();
        PsRuntimeDiagnosis d = service.DiagnosePsRuntime("命令执行失败", Only7());
        string joined = string.Join("|", d.suggestions);
        Assert.Matches(new Regex("本机仅有 PowerShell 7|核心 SMB"), joined);
    }
}

