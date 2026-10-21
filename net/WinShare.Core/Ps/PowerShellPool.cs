using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;

namespace WinShare.Core.Ps;

/// <summary>
/// Worker 句柄抽象（实现侧注入点，对应 TS WorkerHandle/WorkerFactory）：
/// 生产用 <see cref="RunspaceWorkerHandle"/>（进程内长驻 Runspace），测试用伪造句柄。
/// C# 进程内版省略 TS 句柄的 stderr 读取面（进程内 worker 无独立 stderr 通道）。
/// </summary>
public interface IWorkerHandle : IDisposable
{
    /// <summary>TS: handle.stdin.write(line) — 投递一行协议数据（含换行）。</summary>
    void Write(string line);

    /// <summary>TS: handle.stdout.on('data') — worker 输出（标记协议数据）。</summary>
    event Action<string>? Stdout;

    /// <summary>TS: handle.on('exit'|'error') — C# 合并 exit/error 为一个事件（null code = 错误路径）。</summary>
    event Action<int?>? Exited;

    /// <summary>TS: handle.kill() — 停止 worker（实现侧随后触发 Exited）。</summary>
    bool Kill();
}

/// <summary>
/// Worker 启动参数（实现侧对 TS WorkerFactory 签名的扩展：直接携带 4 个协议标记，
/// 避免从脚本字符串正则回提取；ServerEncodedCommand 保留以维持 TS 协议等价性）。
/// </summary>
public sealed record WorkerLaunch(string ServerEncodedCommand, string OkMarker, string ErrMarker, string ErrEndMarker, string QuitMarker);

/// <summary>TS: WorkerFactory = (serverEncodedCommand: string) => WorkerHandle</summary>
public delegate IWorkerHandle WorkerFactory(WorkerLaunch launch);

/// <summary>TS: 'PowerShell 进程池已关闭'</summary>
public sealed class PoolClosedException : Exception
{
    public PoolClosedException() : base("PowerShell 进程池已关闭") { }
}

/// <summary>TS: 'Command timed out'（含 "timed out" → 上层 isRetryable 匹配为可重试）。</summary>
public sealed class PsPoolTimeoutException : Exception
{
    public PsPoolTimeoutException() : base("Command timed out") { }
}

/// <summary>TS: 'PowerShell 进程意外退出，请重试'（含「重试」→ 上层 isRetryable 匹配 → 换新 worker 重试）。</summary>
public sealed class PsPoolWorkerCrashedException : Exception
{
    public PsPoolWorkerCrashedException() : base("PowerShell 进程意外退出，请重试") { }
}

/// <summary>
/// TS: new Error('Command failed') + stderr 字段（ERR/ERR_END 标记之间提取的可读文本），
/// 交由上层 formatPsError 归一化（权限不足 / 可重试判定等）。
/// </summary>
public sealed class PsCommandFailedException : Exception
{
    /// <summary>TS: (err as Error &amp; { stderr?: string }).stderr</summary>
    public string Stderr { get; }

    public PsCommandFailedException(string? stderr) : base("Command failed") => Stderr = stderr ?? "";
}

/// <summary>
/// PowerShell 常驻池（electron/lib/powershellPool.ts 协议与派发语义的 C# 移植）：
/// 长驻 worker 池（缺省 3，下限 1）、FIFO 排队、并行派发、每命令独立超时
/// （超时 = 中毒 kill + 补 spawn）、崩溃补 spawn、shutdown 拒绝排队 + 在飞命令。
/// 进程内版：生产 worker 为 <see cref="RunspaceWorkerHandle"/>（Runspace 宿主，非 Start-Process），
/// 分发 = BlockingCollection 通道 + 池派发状态机；标记协议（Base64(UTF-16LE) 载荷 +
/// mode NUL 命令体 + OK/ERR/ERR_END 标记 + quit 标记 + M-3 空闲期丢弃）与 TS 逐行等价。
/// 线程模型：单一 _gate 锁保护整个协议状态机（伪造 worker 测试经事件驱动）。
/// </summary>
public sealed class PowerShellPool : IPsPool
{
    private sealed class Worker
    {
        public IWorkerHandle Handle = null!;
        public bool Alive;
        public bool Busy;
        public PendingCommand? Current;
        public string Buffer = "";
    }

    private sealed class PendingCommand
    {
        public string Command = "";
        public string Mode = "";
        public int TimeoutMs;
        public TaskCompletionSource<string> Tcs = null!;
        public Worker? Worker;
        public System.Threading.Timer? Timer;
    }

    private readonly List<Worker> _workers = new();
    private readonly List<PendingCommand> _queue = new();
    private readonly object _gate = new();
    private readonly string _token;
    private readonly int _size;
    private readonly WorkerFactory _factory;
    private readonly string _okMarker;
    private readonly string _errMarker;
    private readonly string _errEndMarker;
    private readonly string _quitMarker;
    private bool _shuttingDown;

    public PowerShellPool(PsPoolOptions? options = null, WorkerFactory? factory = null)
    {
        _size = Math.Max(1, options?.Size ?? 3);
        _token = string.IsNullOrEmpty(options?.Token) ? RandomToken() : options.Token!;
        _okMarker = $"___PS_OK_{_token}___";
        _errMarker = $"___PS_ERR_{_token}___";
        _errEndMarker = $"___PS_ERR_END_{_token}___";
        _quitMarker = $"___PS_QUIT_{_token}___";
        _factory = factory ?? DefaultWorkerFactory;
    }

    /// <summary>TS defaultWorkerFactory — 进程内版：创建 Runspace worker（而非 spawn 外部进程）。</summary>
    public static IWorkerHandle DefaultWorkerFactory(WorkerLaunch launch) => new RunspaceWorkerHandle(launch);

    // M-4：标记 token 依赖不可预测性降低"外部输出伪造协议标记"错配面，用加密安全随机源
    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

    // === IPsPool ===

    /// <summary>
    /// TS: execute(command, mode, { timeout }) — 提交命令，返回标记前的原始 stdout
    /// （JSON 模式为 JSON 串；VOID 模式为空串）；池关闭时拒绝。
    /// </summary>
    public Task<string> ExecuteAsync(string command, PsPoolMode mode, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_shuttingDown)
            {
                tcs.TrySetException(new PoolClosedException());
                return tcs.Task;
            }
            var cmd = new PendingCommand
            {
                Command = command,
                Mode = mode == PsPoolMode.Json ? "JSON" : "VOID",
                TimeoutMs = timeoutMs,
                Tcs = tcs,
            };
            _queue.Add(cmd);
            Dispatch();
        }
        return tcs.Task;
    }

    /// <summary>TS: prewarm() — 后台预热 1 个 worker（不阻塞，失败静默——首次 execute 时按需 spawn）。</summary>
    public void Prewarm()
    {
        lock (_gate)
        {
            if (_shuttingDown)
            {
                return;
            }
            if (_workers.Any(w => w.Alive))
            {
                return;
            }
            try
            {
                SpawnWorker();
            }
            catch
            {
                // 预热失败忽略，execute 时再尝试
            }
        }
    }

    /// <summary>TS: shutdown() — 拒绝排队 + 在飞命令 + 优雅退出 + 强杀兜底。</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            _shuttingDown = true;
            // 拒绝所有排队命令（避免悬挂任务）
            var q = _queue.ToList();
            _queue.Clear();
            foreach (var p in q)
            {
                if (p.Timer is { } timer)
                {
                    timer.Dispose();
                    p.Timer = null;
                }
                p.Tcs.TrySetException(new PoolClosedException());
            }
            // 优雅退出 + 强杀兜底；同时拒绝在飞命令（避免悬挂任务）
            foreach (var w in _workers)
            {
                w.Alive = false;
                if (w.Current is { } cmd)
                {
                    if (cmd.Timer is { } timer)
                    {
                        timer.Dispose();
                        cmd.Timer = null;
                    }
                    w.Current = null;
                    cmd.Tcs.TrySetException(new PoolClosedException());
                }
                try { w.Handle.Write(_quitMarker + "\n"); } catch { /* 输入通道已关闭，忽略 */ }
                try { w.Handle.Kill(); } catch { /* 忽略 */ }
                try { w.Handle.Dispose(); } catch { /* 实现侧：释放 Runspace 资源 */ }
            }
            _workers.Clear();
        }
    }

    // === 内部协议状态机（TS: spawnWorker/dispatch/assign/processBuffer/finishCommand/onTimeout/onExit）===

    private Worker SpawnWorker()
    {
        string script = BuildServerScript(_okMarker, _errMarker, _errEndMarker, _quitMarker);
        var handle = _factory(new WorkerLaunch(
            PsArgs.EncodeCommand(script), _okMarker, _errMarker, _errEndMarker, _quitMarker));
        var worker = new Worker { Handle = handle, Alive = true };
        _workers.Add(worker);
        handle.Stdout += chunk => OnStdoutData(worker, chunk);
        handle.Exited += code => OnExit(worker, code);
        return worker;
    }

    private void Dispatch()
    {
        if (_shuttingDown)
        {
            return;
        }
        while (_queue.Count > 0)
        {
            var worker = _workers.FirstOrDefault(w => w.Alive && !w.Busy);
            if (worker == null)
            {
                int aliveCount = _workers.Count(w => w.Alive);
                if (aliveCount < _size)
                {
                    try { worker = SpawnWorker(); }
                    catch { return; } // spawn 失败：暂停派发，等下次 execute/回调重试
                }
                else
                {
                    return; // 全忙：等待某 worker 完成后触发 dispatch
                }
            }
            var cmd = _queue[0];
            _queue.RemoveAt(0);
            Assign(worker, cmd);
        }
    }

    private void Assign(Worker worker, PendingCommand cmd)
    {
        worker.Busy = true;
        worker.Current = cmd;
        worker.Buffer = "";
        cmd.Worker = worker;
        string payload = PsArgs.BuildPayload(cmd.Mode, cmd.Command);
        try { worker.Handle.Write(payload + "\n"); }
        catch { /* 同步写失败罕见（EPIPE 类异步触发）；交由 Exited 处理 */ }
        cmd.Timer = new System.Threading.Timer(
            _ => OnTimeout(cmd.Worker!, cmd), null, cmd.TimeoutMs, System.Threading.Timeout.Infinite);
    }

    private void OnStdoutData(Worker worker, string chunk)
    {
        lock (_gate)
        {
            if (!worker.Alive)
            {
                return;
            }
            // M-3：无在飞命令（预热/空闲期）时直接丢弃 stdout 杂散字节——
            // 否则残留在 buffer 中（含可能的伪造标记串）会在下一条命令边界被错误消费。
            if (worker.Current == null)
            {
                return;
            }
            worker.Buffer += chunk;
            ProcessBuffer(worker);
        }
    }

    private void ProcessBuffer(Worker worker)
    {
        if (worker.Current == null)
        {
            return; // 无在飞命令（如预热期杂散输出），忽略
        }

        // 错误路径：需 errMarker + errEndMarker 齐全
        int errIdx = worker.Buffer.IndexOf(_errMarker, StringComparison.Ordinal);
        if (errIdx >= 0)
        {
            string afterErr = worker.Buffer[(errIdx + _errMarker.Length)..];
            int endIdx = afterErr.IndexOf(_errEndMarker, StringComparison.Ordinal);
            if (endIdx < 0)
            {
                return; // 等待 ERR_END
            }
            // 提取 ERR 与 ERR_END 之间文本：去首尾换行 + trim
            string errMsg = afterErr[..endIdx];
            errMsg = Regex.Replace(errMsg, @"^\r?\n", "");
            errMsg = Regex.Replace(errMsg, @"\r?\n$", "");
            errMsg = errMsg.Trim();
            worker.Buffer = afterErr[(endIdx + _errEndMarker.Length)..];
            var cmd = worker.Current;
            FinishCommand(worker, cmd);
            // 构造与 execFile stderr 等价的错误对象，交由上层 formatPsError 处理
            cmd.Tcs.TrySetException(new PsCommandFailedException(errMsg));
            Dispatch();
            return;
        }

        // 成功路径
        int okIdx = worker.Buffer.IndexOf(_okMarker, StringComparison.Ordinal);
        if (okIdx >= 0)
        {
            string jsonStr = worker.Buffer[..okIdx];
            worker.Buffer = worker.Buffer[(okIdx + _okMarker.Length)..];
            var cmd = worker.Current;
            FinishCommand(worker, cmd);
            cmd.Tcs.TrySetResult(jsonStr);
            Dispatch();
            return;
        }

        // 既无 OK 也无 ERR：继续等待更多数据
    }

    private void FinishCommand(Worker worker, PendingCommand cmd)
    {
        if (cmd.Timer is { } timer)
        {
            timer.Dispose();
            cmd.Timer = null;
        }
        worker.Current = null;
        worker.Busy = false;
    }

    private void OnTimeout(Worker worker, PendingCommand cmd)
    {
        lock (_gate)
        {
            if (worker.Current != cmd)
            {
                return; // 已完成（防御）
            }
            if (cmd.Timer is { } timer)
            {
                timer.Dispose();
                cmd.Timer = null;
            }
            worker.Current = null; // 防止 Exited/迟到标记双重处理
            worker.Busy = false;
            worker.Alive = false; // 标记中毒，Exited 触发时跳过
            try { worker.Handle.Kill(); } catch { /* 忽略 */ }
            try { worker.Handle.Dispose(); } catch { /* 忽略 */ }
            _workers.Remove(worker);
            // 超时错误经上层 formatPsError 转为"命令执行超时"（含 "timed out" → 可重试）
            cmd.Tcs.TrySetException(new PsPoolTimeoutException());
            Dispatch();
        }
    }

    private void OnExit(Worker worker, int? code)
    {
        lock (_gate)
        {
            if (!worker.Alive)
            {
                return; // 已处理（超时 kill / shutdown）
            }
            worker.Alive = false;
            if (worker.Current is { } cmd)
            {
                if (cmd.Timer is { } timer)
                {
                    timer.Dispose();
                    cmd.Timer = null;
                }
                worker.Current = null;
                worker.Busy = false;
                // 崩溃错误含"重试"→ isRetryable 匹配 → 上层换新 worker 重试
                cmd.Tcs.TrySetException(new PsPoolWorkerCrashedException());
            }
            _workers.Remove(worker);
            try { worker.Handle.Dispose(); } catch { /* 忽略 */ }
            Dispatch();
        }
    }

    /// <summary>
    /// worker 服务端脚本（TS buildServerScript 1:1 移植；进程内 Runspace worker 以其为协议参考，
    /// 未来若需外部进程工厂直接 -EncodedCommand 下发亦可复用）。
    /// </summary>
    internal static string BuildServerScript(string okMarker, string errMarker, string errEndMarker, string quitMarker) =>
        string.Join("\n", new[]
        {
            "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8",
            "$OutputEncoding=[System.Text.Encoding]::UTF8",
            "$ProgressPreference='SilentlyContinue'",
            // B-1：默认 Continue 会把 cmdlet 非终止性错误（权限不足、名称冲突等）写进错误流后以退出码 0 结束
            // → 写操作"假成功"。Stop 使错误成为终止性 → 走下方 catch → ERR 标记 reject，真实失败暴露到 UI 与日志。
            "$ErrorActionPreference='Stop'",
            "while ($true) {",
            "  $line = [Console]::In.ReadLine()",
            "  if ($null -eq $line) { break }",
            $"  if ($line -eq '{quitMarker}') {{ break }}",
            "  if ($line -eq '') { continue }",
            "  try {",
            "    $script = [System.Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($line))",
            "    $mode = $script.Substring(0,4)",
            "    $body = $script.Substring(5)",
            "    $sb = [ScriptBlock]::Create($body)",
            "    if ($mode -eq 'JSON') {",
            "      $result = & $sb",
            "      if ($null -ne $result) { $result | ConvertTo-Json -Depth 5 -Compress }",
            "    } else {",
            "      $null = & $sb",
            "    }",
            $"    [Console]::Out.WriteLine('{okMarker}'); [Console]::Out.Flush()",
            "  } catch {",
            $"    [Console]::Out.WriteLine('{errMarker}')",
            "    [Console]::Out.WriteLine($_.Exception.Message)",
            $"    [Console]::Out.WriteLine('{errEndMarker}'); [Console]::Out.Flush()",
            "  }",
            "}",
        });
}

