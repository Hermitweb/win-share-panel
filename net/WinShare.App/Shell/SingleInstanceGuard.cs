using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinShare.App.Events;

namespace WinShare.App.Shell;

/// <summary>
/// 单实例语义（对齐 electron/main.ts requestSingleInstanceLock + second-instance）：
/// - 首实例持 Mutex；
/// - 二实例进程启动后立即命名管道发 "activate" 给首实例，自身退出；
/// - 首实例收到后：恢复最小化 → 显示隐藏 → 聚焦（对齐 main.ts second-instance handler）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string MutexName = @"Local\WinSharePanel.SingleInstance.v1";
    public const string PipeName = "WinSharePanel.Activate";

    readonly Mutex _mutex;
    NamedPipeServerStream? _server;
    CancellationTokenSource? _cts;
    bool _disposed;

    /// <summary>
    /// 二实例调用：尝试向首实例发激活消息（成功=true，首实例已激活）。
    /// </summary>
    public static bool TryActivateExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            try { client.Connect(500); }
            catch { return false; }
            var bytes = Encoding.UTF8.GetBytes("activate");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// acquireWait 非 null 时（仅提权重启链路）：首获失败不立即判二实例，短窗重试——
    /// 旧实例「放锁（AppShell.Quit）→ 进程退出」与新提权实例「启动 → 争锁」存在毫秒级竞态，
    /// 6s 窗口覆盖 UAC 接受后的启动间隔；普通二实例仍走立即失败 + 激活消息。
    /// </summary>
    public SingleInstanceGuard(Action onSecondInstanceActivate, TimeSpan? acquireWait = null)
    {
        _mutex = new Mutex(true, MutexName, out var first);
        if (!first && acquireWait is { } wait)
        {
            var deadline = DateTime.UtcNow + wait;
            while (!first && DateTime.UtcNow < deadline)
            {
                try
                {
                    first = _mutex.WaitOne(TimeSpan.FromMilliseconds(150));
                }
                catch (AbandonedMutexException)
                {
                    first = true; // 旧实例异常退出弃锁 → 接管所有权
                }
            }
        }
        if (!first)
        {
            _mutex.Dispose();
            throw new NotFirstInstanceException();
        }
        StartPipeServer(onSecondInstanceActivate);
    }

    void StartPipeServer(Action onActivate)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _server = new NamedPipeServerStream(PipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte);
                    await _server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(_server, Encoding.UTF8, leaveOpen: true);
                    var line = await reader.ReadLineAsync(CancellationToken.None);
                    if (line == "activate")
                    {
                        try { onActivate(); } catch { /* 激活失败不阻塞 */ }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // 管道错误：下一轮重建
                }
                finally
                {
                    _server?.Dispose();
                    _server = null;
                }
            }
        }, token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _server?.Dispose();
        _mutex.Dispose();
    }
}

/// <summary>非首实例进程（应退出并由首实例接管 UI）。</summary>
public sealed class NotFirstInstanceException : Exception
{
    public NotFirstInstanceException() : base("WinShare Panel 已在运行。") { }
}
