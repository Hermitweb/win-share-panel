using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;

namespace WinShare.Core.Ps;

/// <summary>
/// electron/lib/nativePaths.ts 的 C# 移植（位宽对齐 + PowerShell 多版本兼容层）。
/// 策略与 TS 一致：5.1 优先，缺失才兜底 7.x，再缺则裸名 PATH 兜底；
/// 32 位进程在 64 位系统经 Sysnative 前缀取原生 PowerShell（C# 宿主进程通常 64 位，
/// Arch 可经 NativePathDeps.Arch 注入测试）。
/// 候选位置（按优先级）：
/// 1. SystemRoot\Sysnative|System32\WindowsPowerShell\v1.0\powershell.exe（5.1，位宽对齐）
/// 2. 同目录 pwsh.exe（7.x，Windows 官方默认把 pwsh 注册进 System32 的 WindowsPowerShell 目录）
/// 3. ProgramFilesNative\PowerShell\7\pwsh.exe（winget / 安装包常见位置；32 位进程用 %ProgramW6432% 原生视图）
/// 4. 裸名 powershell.exe / pwsh.exe（最后兜底，spawn/启动失败由诊断层处理）
/// </summary>
public static class NativePathResolver
{
    private const string PowerShellRelative = "WindowsPowerShell\\v1.0\\powershell.exe";
    private const string PwshRelative = "WindowsPowerShell\\v1.0\\pwsh.exe";
    private const string PwshProgramFilesRelative = "PowerShell\\7\\pwsh.exe";

    /// <summary>
    /// TS: nativePowerShellExe — 解析当前进程应使用的 PowerShell 可执行文件
    /// （取第一个「存在」的真实路径候选；全缺时回退裸名 5.1，启动失败由诊断层兜底）。
    /// </summary>
    public static string ResolveExe(NativePathDeps? deps = null)
    {
        if (PlatformOf(deps) != "win32")
        {
            return "powershell.exe";
        }
        foreach (PsCandidate c in ListCandidates(deps))
        {
            // 跳过裸名候选（path 无盘符前缀 = 裸名），取第一个存在的真实路径
            if (c.exists && IsAbsolute(c.path))
            {
                return c.path;
            }
        }
        return "powershell.exe";
    }

    /// <summary>TS: listPowerShellCandidates — 按优先级枚举全部候选（每个候选带 exists 标记）。</summary>
    public static IReadOnlyList<PsCandidate> ListCandidates(NativePathDeps? deps = null)
    {
        string platform = PlatformOf(deps);
        if (platform != "win32")
        {
            // 非 Windows（跨平台开发机）：交给 PATH，不探测真实路径
            return new List<PsCandidate>
            {
                new() { path = "powershell.exe", engine = PsEngine.WindowsPowerShell, exists = true, note = "非 Windows 平台，裸名交给 PATH（开发机）" },
                new() { path = "pwsh.exe", engine = PsEngine.PowershellCore, exists = true, note = "非 Windows 平台，裸名交给 PATH（开发机）" },
            };
        }

        Func<string, bool> exists = ExistsOf(deps);
        (string prefix, bool viaSysnative) = NativePrefixOf(deps, exists);
        string widthNote = viaSysnative ? "经 Sysnative（原生 64 位）" : "System32（原生）";
        string pf = ProgramFilesNativeOf(deps);

        var candidates = new List<PsCandidate>();

        // 1. 5.1（位宽对齐）
        string ws51 = prefix + "\\" + PowerShellRelative;
        candidates.Add(new PsCandidate { path = ws51, engine = PsEngine.WindowsPowerShell, exists = exists(ws51), note = "Windows PowerShell 5.1，" + widthNote });

        // 2. 7.x（System32 视图的 WindowsPowerShell 目录，与 5.1 同目录同位宽）
        string pwshSys = prefix + "\\" + PwshRelative;
        candidates.Add(new PsCandidate { path = pwshSys, engine = PsEngine.PowershellCore, exists = exists(pwshSys), note = "PowerShell 7+（系统注册位置），" + widthNote });

        // 3. 7.x（winget / 安装包常见位置，原生 Program Files）
        string pwshPf = pf + "\\" + PwshProgramFilesRelative;
        candidates.Add(new PsCandidate { path = pwshPf, engine = PsEngine.PowershellCore, exists = exists(pwshPf), note = @"PowerShell 7+（winget 安装位置，%ProgramFiles%\PowerShell\7）" });

        // 4. 裸名兜底（仅当 1-3 全缺失时才有意义；启动失败由调用方诊断）
        candidates.Add(new PsCandidate { path = "powershell.exe", engine = PsEngine.WindowsPowerShell, exists = true, note = "裸名兜底（交给 PATH）" });
        candidates.Add(new PsCandidate { path = "pwsh.exe", engine = PsEngine.PowershellCore, exists = true, note = "裸名兜底（交给 PATH）" });
        return candidates;
    }

    /// <summary>TS: nativeSystemPath — 解析 System32 相对路径（32 位进程优先 Sysnative，stat 事实判定）。</summary>
    public static string NativeSystemPath(string relative, NativePathDeps? deps = null)
    {
        string root = SystemRootOf(deps);
        if (PlatformOf(deps) == "win32" && ExistsOf(deps)(root + "\\Sysnative\\" + relative))
        {
            return root + "\\Sysnative\\" + relative;
        }
        return root + "\\System32\\" + relative;
    }

    /// <summary>绝对路径判定（TS: /^[A-Za-z]:[\\/]/ 或 posix 前导 /）。</summary>
    public static bool IsAbsolute(string path) =>
        Regex.IsMatch(path, @"^[A-Za-z]:[\\/]") || path.StartsWith('/');

    internal static string PlatformOf(NativePathDeps? deps) =>
        deps?.platform ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win32" : "linux");

    /// <summary>位宽判定用进程事实（C# 宿主通常 64 位；ia32 可经 deps.Arch 注入测试）。</summary>
    internal static bool Is32Bit(NativePathDeps? deps) =>
        // .NET 9/10 面：ProcessArchitecture 枚举为 internal，位宽判定改用 Environment.Is64BitProcess
        (deps?.arch ?? (Environment.Is64BitProcess ? "x64" : "ia32")) == "ia32";

    /// <summary>TS: systemRootOf — %SystemRoot% → %windir% → C:\Windows，去尾部斜杠。</summary>
    internal static string SystemRootOf(NativePathDeps? deps)
    {
        string raw = deps?.systemRoot
            ?? Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir")
            ?? "C:\\Windows";
        return raw.TrimEnd('\\', '/') is { Length: > 0 } trimmed ? trimmed : "C:\\Windows";
    }

    /// <summary>
    /// TS: programFilesNativeOf — 原生 Program Files：
    /// 32 位进程用 %ProgramW6432%（WOW64 保留的原生 64 位 ProgramFiles 环境变量），
    /// 64 位进程 %ProgramFiles% 即原生；都缺失时落回 C:\Program Files。
    /// </summary>
    internal static string ProgramFilesNativeOf(NativePathDeps? deps)
    {
        bool is32Bit = Is32Bit(deps);
        string raw = deps?.programFilesNative
            ?? (is32Bit
                ? Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetEnvironmentVariable("ProgramFiles")
                : Environment.GetEnvironmentVariable("ProgramFiles"))
            ?? "C:\\Program Files";
        return raw.TrimEnd('\\', '/') is { Length: > 0 } trimmed ? trimmed : "C:\\Program Files";
    }

    /// <summary>
    /// TS: nativePrefixOf — 位宽对齐前缀：32 位进程在 64 位系统命中 Sysnative，其余落回 System32。
    /// 位宽判定用 Arch（进程事实），不靠"Sysnative 里能不能 stat 到 5.1"：
    /// 精简系统删了 5.1 时 Sysnative 目录仍在（32 位进程可见），靠 5.1 存在性判定会误落 System32。
    /// </summary>
    internal static (string prefix, bool viaSysnative) NativePrefixOf(NativePathDeps? deps, Func<string, bool> exists)
    {
        string root = SystemRootOf(deps);
        string sysnativePrefix = root + "\\Sysnative";
        bool canUseSysnative =
            PlatformOf(deps) == "win32"
            && Is32Bit(deps)
            && (exists(sysnativePrefix + "\\" + PowerShellRelative)
                || exists(sysnativePrefix + "\\" + PwshRelative));
        return (canUseSysnative ? sysnativePrefix : root + "\\System32", canUseSysnative);
    }

    internal static Func<string, bool> ExistsOf(NativePathDeps? deps) =>
        deps?.exists ?? (path =>
        {
            try { return File.Exists(path); }
            catch { return false; }
        });
}

