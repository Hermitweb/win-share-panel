using WinShare.Core.Contracts;

namespace WinShare.Core.Ps;

/// <summary>
/// 模块级单例门面（TS powershellPool.ts 模块级 getPool/prewarmPool/shutdownPool/__resetPoolForTesting 的 C# 等价）：
/// 懒创建、导入无副作用；环境开关与 TS 对齐——
/// WINSHARE_PSPOOL=0 禁用池；WINSHARE_PS_POOL_SIZE 设池大小（缺省 3，无效值/0 回落 3）。
/// 注：契约面未定义 IPoolSingleton（见回报契约缺口），实现侧以本静态门面承载单例语义。
/// </summary>
public static class PoolSingleton
{
    private static readonly object Gate = new();
    private static PowerShellPool? _instance;

    /// <summary>TS: isPoolEnabled()</summary>
    public static bool IsPoolEnabled() =>
        Environment.GetEnvironmentVariable("WINSHARE_PSPOOL") != "0";

    /// <summary>TS: getPool() — 懒创建单例（size 读 WINSHARE_PS_POOL_SIZE）。</summary>
    public static PowerShellPool GetPool()
    {
        lock (Gate)
        {
            if (_instance is null)
            {
                int size = 3;
                string? raw = Environment.GetEnvironmentVariable("WINSHARE_PS_POOL_SIZE");
                // 对齐 TS Number(env) || 3：不可解析或 0/负数均回落 3
                if (raw is not null && int.TryParse(raw, out int parsed) && parsed > 0)
                {
                    size = parsed;
                }
                _instance = new PowerShellPool(new PsPoolOptions { Size = size });
            }
            return _instance;
        }
    }

    /// <summary>TS: prewarmPool() — 启用时预热单例。</summary>
    public static void PrewarmPool()
    {
        if (IsPoolEnabled())
        {
            GetPool().Prewarm();
        }
    }

    /// <summary>TS: shutdownPool()</summary>
    public static void ShutdownPool()
    {
        lock (Gate)
        {
            _instance?.Shutdown();
            _instance = null;
        }
    }

    /// <summary>TS: __resetPoolForTesting() — 测试专用重置单例。</summary>
    public static void ResetForTesting()
    {
        lock (Gate)
        {
            _instance?.Shutdown();
            _instance = null;
        }
    }
}

