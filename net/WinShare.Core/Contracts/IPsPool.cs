namespace WinShare.Core.Contracts;

/// <summary>PowerShell 进程池执行模式（TS: electron/lib/powershellPool.ts Mode = 'JSON' | 'VOID'）。</summary>
public enum PsPoolMode { Json, Void }

/// <summary>进程池选项（TS: PowerShellPoolOptions；factory 为 Worker 工厂注入点，实现侧定义）。</summary>
public record PsPoolOptions
{
    /// <summary>池大小，缺省 3，下限 1。</summary>
    public int? Size { get; init; }
    /// <summary>标记 token（缺省随机）。</summary>
    public string? Token { get; init; }
}

/// <summary>
/// PowerShell 常驻进程池（对应 electron/lib/powershellPool.ts PowerShellPool 公共面：
/// execute / prewarm / shutdown。模块级单例 isPoolEnabled / getPool /
/// prewarmPool / shutdownPool 归实现侧静态门面，__resetPoolForTesting 测试钩子不在契约内。
/// </summary>
public interface IPsPool
{
    /// <summary>
    /// TS: execute(command, mode, { timeout })。
    /// 提交命令，返回标记前的原始 stdout（Json 模式为 JSON 串；Void 模式为空串）；
    /// 池关闭时拒绝（等价 TS "PowerShell 进程池已关闭"）。
    /// </summary>
    Task<string> ExecuteAsync(string command, PsPoolMode mode, int timeoutMs);

    /// <summary>TS: prewarm()（后台预热 1 个 worker，失败静默）。</summary>
    void Prewarm();

    /// <summary>TS: shutdown()（拒队 + 优雅退出 + 强杀兜底）。</summary>
    void Shutdown();
}
