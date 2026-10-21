using System.Collections.Concurrent;
using System.Text.Json;
using WinShare.Core.Contracts;

namespace WinShare.Core.Ps;

/// <summary>
/// electron/lib/psRuntimeProbe.ts 的 C# 移植（非阻塞 $PSVersionTable 版本探测，5 分钟 TTL 缓存）。
/// 在应用启动时探测一次引擎大版本（5/7）与完整版本号，供 cmdlet 差异排查
/// （IIS COM 仅 5.1 注册 / ConvertTo-Json 深度 / .NET API 面等）。
/// 探测失败（超时/引擎启动失败）不缓存负结果，下次调用重试。
/// </summary>
public static class PsRuntimeProbe
{
    private static readonly ConcurrentDictionary<string, (PsVersionInfo Info, long ProbedAt)> Cache = new();
    private const int VersionCacheMs = 5 * 60 * 1000;

    /// <summary>
    /// TS: probePsVersion(deps, run) — 探测某可执行文件的 PowerShell 版本。
    /// 执行器语义差异（实现侧）：TS 的 run 负责解析 JSON 返回强类型；
    /// C# 版 run 只返回原始 JSON stdout 串（TS 默认执行器内部行为即 spawn + JSON.parse），解析在本层。
    /// </summary>
    public static async Task<PsVersionInfo> ProbeVersionAsync(
        NativePathDeps? deps = null,
        Func<string, Task<string>>? run = null)
    {
        string exe = NativePathResolver.ResolveExe(deps);
        if (Cache.TryGetValue(exe, out var hit)
            && Environment.TickCount64 - hit.ProbedAt < VersionCacheMs)
        {
            return hit.Info;
        }
        if (run is null)
        {
            // 无执行器（如启动最早期）：只记「未探测」占位，caller 不阻塞
            return new PsVersionInfo { major = 0, full = "unknown", exe = exe, probedAt = Environment.TickCount64 };
        }
        try
        {
            // 最小探测命令：纯读 $PSVersionTable，不加载任何模块，5.1 / 7.x 都能跑
            string json = await run(
                "$v = $PSVersionTable.PSVersion; [PSCustomObject]@{ major = $v.Major; full = $v.ToString() } | ConvertTo-Json -Compress");
            using JsonDocument doc = JsonDocument.Parse(json ?? "{}");
            int major = doc.RootElement.TryGetProperty("major", out JsonElement m) ? m.GetInt32() : 0;
            string full = doc.RootElement.TryGetProperty("full", out JsonElement f) ? f.GetString() ?? "unknown" : "unknown";
            var result = new PsVersionInfo { major = major, full = full, exe = exe, probedAt = Environment.TickCount64 };
            Cache[exe] = (result, result.probedAt);
            return result;
        }
        catch
        {
            // 探测失败（超时 / 引擎启动失败）：不缓存负结果，下次重试
            return new PsVersionInfo { major = 0, full = "unknown", exe = exe, probedAt = Environment.TickCount64 };
        }
    }

    /// <summary>TS: resetPsVersionCache() — 测试钩子：清版本缓存。</summary>
    public static void ResetPsVersionCache() => Cache.Clear();
}

