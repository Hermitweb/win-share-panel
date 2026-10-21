using System.Collections.Concurrent;
using System.Text;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.PowerShell;

// 注：.NET 9+ SMA 升级后 API 面差异：不再直接依赖 PSThreadOptions / Runspace 实例级
// SetSessionStateVariable / Runspace.Stop()；PowerShell.Create(runspace) 自带当前线程绑定，
// ExecutionPolicy 经 init 脚本设置（-ExecutionPolicy Bypass 语义等价）。

namespace WinShare.Core.Ps;

/// <summary>
/// 生产进程内 worker：1 个长驻 Runspace + 命令循环（BlockingCollection 通道分发），
/// 标记协议 1:1 对齐 TS powershellPool.ts（Base64(UTF-16LE) 载荷、mode NUL body、
/// OK/ERR/ERR_END 标记、quit 标记、B-1 $ErrorActionPreference='Stop' 错误语义——
/// 非终止性错误暴露为失败而非假成功）。JSON 模式输出经同 Runspace 内
/// ConvertTo-Json -Depth 5 -Compress 序列化，与 TS worker 端管道语义一致。
/// </summary>
public sealed class RunspaceWorkerHandle : IWorkerHandle
{
    private readonly BlockingCollection<string> _input;
    private readonly Runspace _runspace;
    private readonly Task _loop;
    private readonly string _okMarker;
    private readonly string _errMarker;
    private readonly string _errEndMarker;
    private readonly string _quitMarker;
    private readonly object _stateGate = new();
    private volatile object? _current;
    private int _killed;
    private int _exitedFired;
    private int _disposed;

    public event Action<string>? Stdout;
    public event Action<int?>? Exited;

    public RunspaceWorkerHandle(WorkerLaunch launch)
    {
        _okMarker = launch.OkMarker;
        _errMarker = launch.ErrMarker;
        _errEndMarker = launch.ErrEndMarker;
        _quitMarker = launch.QuitMarker;
        _input = new BlockingCollection<string>();
        // 关键：进程内 Runspace 的 ExecutionPolicy 默认 Undefined → 回落机器策略（本机常为 Restricted），
        // 导致 SmbShare / WebAdministration 等**脚本模块**（.psm1）加载失败：
        //   "cannot be loaded because running scripts is disabled on this system"
        // 表现为 Get-SmbServerConfiguration 等命令报「模块无法加载」。
        // TS 版靠 spawn 参数 `-ExecutionPolicy Bypass` 规避；进程内等效做法 = InitialSessionState.ExecutionPolicy。
        InitialSessionState iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        _runspace = RunspaceFactory.CreateRunspace(iss);
        // .NET 9/10 ref 面把 ThreadOptions/Initialize 实例 API 裁掉（runtime 面仍具备）：
        // 反射设置 ReuseApplicationThread —— 本地 Runspace 默认 SingleThread：跨线程 Invoke 会
        // 把引擎 marshaled 到初始化线程，池线程不 pump → 永久死锁（压测挂起根因）。
        try
        {
            var threadOptionsProperty = typeof(Runspace).GetProperty("ThreadOptions");
            object reuse = Enum.Parse(typeof(PSThreadOptions), "ReuseApplicationThread");
            threadOptionsProperty?.SetValue(_runspace, reuse);
        }
        catch
        {
            // 反射失败时保持默认（宁可初始化失败快速暴露，不静默降级到死锁配置）
        }
        // 本地 Runspace 必须显式 Open（本 SMA 版本无惰性 open；构造失败会使池派发永久挂起——
        // 与 TS 默认工厂 spawn 失败静默重试的语义不同：C# 版让池层 catch 后保持可重试）
        _runspace.Open();
        // 一次性初始化（对齐 TS worker 启动语义）：
        // 进程内无真实控制台宿主，[Console]::OutputEncoding 用 try/catch 防无控制台环境毒化 worker；
        // $OutputEncoding/$ProgressPreference/$ErrorActionPreference 与 TS 逐行对齐。
        using (var init = PowerShell.Create(_runspace))
        {
            init.AddScript(
                "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch { }" + "\n" +
                "$OutputEncoding=[System.Text.Encoding]::UTF8" + "\n" +
                "$ProgressPreference='SilentlyContinue'" + "\n" +
                "$ErrorActionPreference='Stop'");
            init.Invoke();
        }
        _loop = Task.Run(Loop);
    }

    private void Loop()
    {
        int code = 1;
        try
        {
            // 通道分发：池 Assign 时 Write(payload + "\n")；退出 = 输入通道完成
            foreach (string line in _input.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref _killed) == 1)
                {
                    break;
                }
                string normalized = line.TrimEnd('\r', '\n'); // 对齐 TS ReadLine() 剥离换行语义
                if (normalized.Length == 0)
                {
                    continue; // TS: if ($line -eq '') { continue }
                }
                if (normalized == _quitMarker)
                {
                    code = 0;
                    break; // TS: quit 标记优雅退出
                }
                try
                {
                    string script = Encoding.Unicode.GetString(Convert.FromBase64String(normalized));
                    string mode = script[..4]; // 'JSON' / 'VOID'
                    string body = script[5..]; // 第 5 字符为 NUL 分隔
                    RunBody(mode, body);
                    Emit(_okMarker + "\n");
                }
                catch (Exception ex)
                {
                    Emit(_errMarker + "\n" + ex.Message + "\n" + _errEndMarker + "\n");
                }
            }
        }
        catch
        {
            // 循环健壮：输入通道 Dispose/kill 中途等
        }
        finally
        {
            FireExited(code);
        }
    }

    // 命令体经 AddScript/AddArgument 传入（零字符串拼接），与上游 PsArgs 闸门配合杜绝注入。
    private void RunBody(string mode, string body)
    {
        var pw = PowerShell.Create(_runspace);
        _current = pw;
        try
        {
            pw.AddScript(body);
            object?[] results = pw.Invoke()?.Cast<object?>().ToArray() ?? Array.Empty<object?>();
            if (pw.HadErrors)
            {
                // B-1 兜底：$ErrorActionPreference='Stop' 已把非终止性错误转终止性（Invoke 直接抛）；
                // 若错误流仍残留，暴露为失败而非假成功
                string err = string.Join("\n", pw.Streams.Error.Select(e => e.ToString()));
                throw new Exception(string.IsNullOrEmpty(err) ? "PowerShell 命令执行失败" : err);
            }
            if (mode == "JSON" && results.Length > 0)
            {
                // TS 语义：$result | ConvertTo-Json -Depth 5 -Compress（结果非 null 才输出）
                var pw2 = PowerShell.Create(_runspace);
                // AddScript 必须先于 AddArgument（PowerShell 实例要求先有命令再绑参数）
                pw2.AddScript("$args | ConvertTo-Json -Depth 5 -Compress");
                pw2.AddArgument(results.Length == 1 ? results[0] : results);
                object?[] jsonResults = pw2.Invoke()?.Cast<object?>().ToArray() ?? Array.Empty<object?>();
                if (pw2.HadErrors)
                {
                    string err = string.Join("\n", pw2.Streams.Error.Select(e => e.ToString()));
                    throw new Exception(string.IsNullOrEmpty(err) ? "PowerShell 输出序列化失败" : err);
                }
                string json = string.Join("\n", jsonResults.Where(r => r != null).Select(r => Convert.ToString(r) ?? ""));
                if (json.Length > 0)
                {
                    Emit(json + "\n");
                }
            }
            // VOID 模式：丢弃输出（TS: $null = & $sb），仅写 OK 标记由循环统一处理
        }
        finally
        {
            _current = null;
        }
    }

    private void Emit(string text)
    {
        Stdout?.Invoke(text);
    }

    public void Write(string line)
    {
        try
        {
            _input.Add(line);
        }
        catch (InvalidOperationException)
        {
            // 输入通道已完成（worker 关闭中），忽略——对齐 TS "写 stdin 可能 EPIPE，吞掉由 exit 处理"
        }
    }

    public bool Kill()
    {
        Interlocked.Exchange(ref _killed, 1);
        // 强杀兜底：停当前命令 + 关闭输入通道；Exited 由循环 finally 触发
        // （.NET 9/10 SMA 公开面无 Runspace.Stop()；Dispose 时回收 runspace）
        var current = _current as PowerShell;
        if (current is not null)
        {
            try { current.Stop(); } catch { /* 忽略 */ }
        }
        try { _input.CompleteAdding(); } catch { /* 忽略 */ }
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try { _input.Dispose(); } catch { /* 忽略 */ }
        try { _runspace.Dispose(); } catch { /* 忽略 */ }
    }

    private void FireExited(int code)
    {
        lock (_stateGate)
        {
            if (Interlocked.Exchange(ref _exitedFired, 1) != 0)
            {
                return; // 只触发一次（TS: 真实进程 exit 事件仅一次）
            }
        }
        Exited?.Invoke(code);
    }
}

