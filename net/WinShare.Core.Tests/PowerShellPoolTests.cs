using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Ps;

namespace WinShare.Core.Tests;

/// <summary>
/// electron/lib/powershellPool.test.ts 的 C# 逐例移植（伪造 worker 标记协议，15 个场景）。
/// 池实现为进程内 Runspace 池；协议层（Base64(UTF-16LE) 载荷 + OK/ERR/ERR_END 标记 +
/// FIFO 排队 + 超时中毒 + 崩溃恢复 + M-3 空闲丢弃）与 TS 逐行等价。
/// </summary>
public class PowerShellPoolTests
{
    // 固定 token，使标记可预测（TS: const TOKEN = 'TEST'）
    private const string Token = "TEST";
    private static readonly string Ok = $"___PS_OK_{Token}___";
    private static readonly string Err = $"___PS_ERR_{Token}___";
    private static readonly string ErrEnd = $"___PS_ERR_END_{Token}___";

    // 伪造 worker 句柄：捕获 stdin 写入，stdout 由测试通过 respond() 推送，事件模拟 exit/error
    private sealed class FakeWorkerHandle : IWorkerHandle
    {
        public List<string> Written { get; } = new();
        public bool Killed { get; private set; }
        public event Action<string>? Stdout;
        public event Action<int?>? Exited;

        public void Write(string line)
        {
            lock (Written)
            {
                Written.Add(line);
            }
        }

        // 测试推送 stdout 响应
        public void Respond(string text) => Stdout?.Invoke(text);

        // 模拟进程退出
        public void SimulateExit(int? code) => Exited?.Invoke(code);

        public bool Kill()
        {
            Killed = true;
            // kill 会触发真实进程 exit；模拟之（OnExit 因 alive=false 早退）
            Exited?.Invoke(1);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static PowerShellPool MakePool(int size, List<FakeWorkerHandle> handles) =>
        new(new PsPoolOptions { Size = size, Token = Token },
            _ =>
            {
                var h = new FakeWorkerHandle();
                handles.Add(h);
                return h;
            });

    // 解码载荷：Base64(UTF16LE) → 原始字符串，拆分 mode NUL command
    private static (string Mode, string Command) DecodePayload(string payload)
    {
        string decoded = Encoding.Unicode.GetString(Convert.FromBase64String(payload));
        int sep = decoded.IndexOf('\u0000');
        return (decoded[..sep], decoded[(sep + 1)..]);
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#单命令 JSON 模式：返回标记前的原始 stdout]</summary>
    public async Task SingleCommand_JsonMode_ReturnsRawStdoutBeforeMarker()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            Task<string> p = pool.ExecuteAsync("Get-SmbShare", PsPoolMode.Json, 5000);
            // execute 的 dispatch 同步完成 → worker 已派发
            Assert.Single(handles);
            var h = handles[0];
            // 验证载荷编码正确（mode + NUL + command）
            var (mode, command) = DecodePayload(h.Written[0].TrimEnd('\n'));
            Assert.Equal("JSON", mode);
            Assert.Equal("Get-SmbShare", command);

            h.Respond("{\"name\":\"share1\"}\n" + Ok);
            string result = await p;
            Assert.Equal("{\"name\":\"share1\"}", result.Trim());
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#多行 JSON 输出：标记解析正确]</summary>
    public async Task MultiLineJson_MarkerParsingCorrect()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            Task<string> p = pool.ExecuteAsync("Get-SmbSession", PsPoolMode.Json, 5000);
            var h = handles[0];
            string multiLineJson = "[\n  {\"Client\":\"a\"},\n  {\"Client\":\"b\"}\n]";
            h.Respond(multiLineJson + "\n" + Ok);
            string result = await p;
            using JsonDocument doc = JsonDocument.Parse(result);
            var items = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("Client").GetString()!).ToArray();
            Assert.Equal(new[] { "a", "b" }, items);
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#VOID 模式：空输出，返回空串]</summary>
    public async Task VoidMode_EmptyOutput_ReturnsEmptyString()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            Task<string> p = pool.ExecuteAsync("Remove-SmbShare -Name x", PsPoolMode.Void, 5000);
            var h = handles[0];
            var (mode, _) = DecodePayload(h.Written[0].TrimEnd('\n'));
            Assert.Equal("VOID", mode);
            // VOID 服务端不输出 JSON，仅写 OK 标记行
            h.Respond(Ok + "\n");
            string result = await p;
            Assert.Equal("", result);
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#M-3：空闲期杂散 stdout（含伪造 OK 标记）被丢弃，不影响也不提前完成后续命令]</summary>
    public async Task IdleStrayStdout_WithForgedMarker_Discarded()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            pool.Prewarm();
            Assert.Single(handles);
            var h = handles[0];
            // 空闲期推送非预期输出（含伪造标记串）——修复前会累积进 buffer
            h.Respond("stray junk before any command\n" + Ok);

            Task<string> p = pool.ExecuteAsync("Get-SmbShare", PsPoolMode.Json, 5000);
            bool settled = false;
            _ = p.ContinueWith(_ => settled = true); // fire-and-forget：仅记录 p 是否提前结算
            await Task.Delay(50);
            // 伪造标记不得提前 resolve 真实命令
            Assert.False(settled);
            h.Respond("{\"name\":\"real\"}\n" + Ok);
            string result = await p;
            using JsonDocument doc = JsonDocument.Parse(result.Trim());
            Assert.Equal("real", doc.RootElement.GetProperty("name").GetString());
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#多命令 FIFO 排队：size=1 时串行执行]</summary>
    public async Task FifoQueuing_Size1_SerialExecution()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(1, handles);
        try
        {
            Task<string> p1 = pool.ExecuteAsync("c1", PsPoolMode.Json, 5000);
            Task<string> p2 = pool.ExecuteAsync("c2", PsPoolMode.Json, 5000);
            Task<string> p3 = pool.ExecuteAsync("c3", PsPoolMode.Json, 5000);

            // size=1 → 仅 1 个 worker，c2/c3 排队
            Assert.Single(handles);
            var h = handles[0];

            h.Respond("\"r1\"\n" + Ok);
            Assert.Equal("\"r1\"", (await p1).Trim());

            // c1 完成后 dispatch 派发 c2 到同一 worker
            h.Respond("\"r2\"\n" + Ok);
            Assert.Equal("\"r2\"", (await p2).Trim());

            h.Respond("\"r3\"\n" + Ok);
            Assert.Equal("\"r3\"", (await p3).Trim());
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#并发命令跨多 worker 并行派发：2 命令均派发后才响应]</summary>
    public async Task ConcurrentDispatch_TwoWorkersParallel()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            Task<string> p1 = pool.ExecuteAsync("c1", PsPoolMode.Json, 5000);
            Task<string> p2 = pool.ExecuteAsync("c2", PsPoolMode.Json, 5000);

            // 关键断言：2 个 worker 均已派发（并行），在任一响应前
            Assert.Equal(2, handles.Count);
            Assert.Single(handles[0].Written);
            Assert.Single(handles[1].Written);

            // 分别响应
            handles[0].Respond("\"a\"\n" + Ok);
            handles[1].Respond("\"b\"\n" + Ok);

            string[] results = await Task.WhenAll(p1, p2);
            Assert.Equal("\"a\"", results[0].Trim());
            Assert.Equal("\"b\"", results[1].Trim());
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#ERR 路径：reject 带 stderr 的错误对象]</summary>
    public async Task ErrPath_RejectsWithErrorObjectCarryingStderr()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            Task<string> p = pool.ExecuteAsync("Throw-Error", PsPoolMode.Json, 5000);
            var h = handles[0];
            h.Respond(Err + "\nAccess is denied\n" + ErrEnd);
            PsCommandFailedException ex = await Assert.ThrowsAsync<PsCommandFailedException>(async () => await p);
            Assert.Equal("Command failed", ex.Message);
            Assert.Equal("Access is denied", ex.Stderr);
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#ERR 多行消息：提取 ERR 与 ERR_END 之间的文本]</summary>
    public async Task ErrMultiLineMessage_ExtractsTextBetweenMarkers()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            Task<string> p = pool.ExecuteAsync("Bad", PsPoolMode.Json, 5000);
            var h = handles[0];
            h.Respond(Err + "\nLine1\nLine2\n" + ErrEnd);
            PsCommandFailedException ex = await Assert.ThrowsAsync<PsCommandFailedException>(async () => await p);
            Assert.Equal("Line1\nLine2", ex.Stderr);
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#ERR 标记到达但 ERR_END 未到：继续等待，不提前 reject]</summary>
    public async Task ErrMarkerWithoutErrEnd_WaitsNotRejectsEarly()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            Task<string> p = pool.ExecuteAsync("Bad", PsPoolMode.Json, 5000);
            var h = handles[0];
            h.Respond(Err + "\npartial msg");
            // 仅 ERR 无 ERR_END → 仍在等待，promise 未决
            Task delay = Task.Delay(60);
            Task winner = await Task.WhenAny(p, delay);
            Assert.True(winner == delay);

            // 补齐 ERR_END → reject，消息为 ERR 与 ERR_END 之间的全部文本
            h.Respond("\nmore\n" + ErrEnd);
            PsCommandFailedException ex = await Assert.ThrowsAsync<PsCommandFailedException>(async () => await p);
            Assert.Equal("partial msg\nmore", ex.Stderr);
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#超时：reject 超时错误 + kill 中毒 worker + 后续命令补 spawn 成功]</summary>
    public async Task Timeout_RejectsKillsPoisonedWorkerAndRespawns()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            Task<string> p = pool.ExecuteAsync("Slow", PsPoolMode.Json, 50);
            var h = handles[0];
            // 不响应，等超时
            PsPoolTimeoutException ex = await Assert.ThrowsAsync<PsPoolTimeoutException>(async () => await p);
            Assert.Equal("Command timed out", ex.Message);
            Assert.True(h.Killed);

            // 后续命令应补 spawn 新 worker 并成功
            Task<string> p2 = pool.ExecuteAsync("After", PsPoolMode.Json, 5000);
            Assert.Equal(2, handles.Count);
            handles[1].Respond("\"ok\"\n" + Ok);
            Assert.Equal("\"ok\"", (await p2).Trim());
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#Worker 崩溃（busy 时 exit）：reject 在飞命令（可重试）+ 后续补 spawn]</summary>
    public async Task CrashWhileBusy_RejectsInFlightCommandAndRespawns()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            Task<string> p = pool.ExecuteAsync("Crash", PsPoolMode.Json, 5000);
            var h = handles[0];
            // 模拟进程意外退出
            h.SimulateExit(1);
            PsPoolWorkerCrashedException ex = await Assert.ThrowsAsync<PsPoolWorkerCrashedException>(async () => await p);
            Assert.Equal("PowerShell 进程意外退出，请重试", ex.Message);
            // 错误消息含"重试"→ isRetryable 匹配 → 上层换新 worker 重试
            Assert.Matches(new Regex("重试|timed out|ECONNRESET", RegexOptions.IgnoreCase), ex.Message);

            // 后续命令补 spawn 新 worker
            Task<string> p2 = pool.ExecuteAsync("After", PsPoolMode.Json, 5000);
            Assert.Equal(2, handles.Count);
            handles[1].Respond("\"ok\"\n" + Ok);
            Assert.Equal("\"ok\"", (await p2).Trim());
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#Worker 崩溃（idle 时 exit）：标记 dead，下次命令补 spawn]</summary>
    public async Task CrashWhileIdle_MarksDeadAndRespawnsOnNextCommand()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            // 先执行并完成一条命令，使 worker 进入 idle
            Task<string> p1 = pool.ExecuteAsync("First", PsPoolMode.Json, 5000);
            handles[0].Respond("\"r1\"\n" + Ok);
            await p1;
            Assert.Single(handles);

            // 模拟 idle worker 崩溃（无在飞命令）
            handles[0].SimulateExit(1);
            // 不应 reject 任何命令（无在飞）

            // 下次命令补 spawn 新 worker
            Task<string> p2 = pool.ExecuteAsync("Second", PsPoolMode.Json, 5000);
            Assert.Equal(2, handles.Count);
            handles[1].Respond("\"r2\"\n" + Ok);
            Assert.Equal("\"r2\"", (await p2).Trim());
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#崩溃后重试（模拟 runPowerShell 重试）：新 worker 执行成功]</summary>
    public async Task RetryAfterCrash_NewWorkerExecutesSuccessfully()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            // 第一次：崩溃
            Task<string> p1 = pool.ExecuteAsync("Cmd", PsPoolMode.Json, 5000);
            handles[0].SimulateExit(1);
            await Assert.ThrowsAsync<PsPoolWorkerCrashedException>(async () => await p1);
            // 第二次（模拟 runPowerShell 换新 worker 重试）：成功
            Task<string> p2 = pool.ExecuteAsync("Cmd", PsPoolMode.Json, 5000);
            Assert.Equal(2, handles.Count);
            handles[1].Respond("\"recovered\"\n" + Ok);
            Assert.Equal("\"recovered\"", (await p2).Trim());
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#shutdown：拒绝在飞与排队命令 + kill 全部 worker]</summary>
    public async Task Shutdown_RejectsInFlightAndQueuedKillsAllWorkers()
    {
        var handles = new List<FakeWorkerHandle>();
        // 用 size=2：c1/c2 派发到 h0/h1（在飞），c3 排队
        var pool = MakePool(2, handles);
        try
        {
            Task<string> p1 = pool.ExecuteAsync("c1", PsPoolMode.Json, 5000);
            Task<string> p2 = pool.ExecuteAsync("c2", PsPoolMode.Json, 5000);
            Task<string> p3 = pool.ExecuteAsync("c3", PsPoolMode.Json, 5000);
            Assert.Equal(2, handles.Count);

            pool.Shutdown();

            // 在飞（c1/c2）与排队（c3）均被拒绝
            await Assert.ThrowsAsync<PoolClosedException>(async () => await p1);
            await Assert.ThrowsAsync<PoolClosedException>(async () => await p2);
            await Assert.ThrowsAsync<PoolClosedException>(async () => await p3);
            Assert.True(handles[0].Killed);
            Assert.True(handles[1].Killed);

            // shutdown 后 execute 立即拒绝
            await Assert.ThrowsAsync<PoolClosedException>(async () => await pool.ExecuteAsync("after", PsPoolMode.Json, 5000));
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.test.ts#残余字节处理：OK 标记后的杂散字节不污染下一条命令（assign 清空 buffer）]</summary>
    public async Task StrayBytesAfterMarker_DoNotPolluteNextCommand()
    {
        var handles = new List<FakeWorkerHandle>();
        var pool = MakePool(3, handles);
        try
        {
            // 模拟一次响应中 OK 后紧跟杂散字节（边界场景）
            Task<string> p1 = pool.ExecuteAsync("c1", PsPoolMode.Json, 5000);
            var h = handles[0];
            h.Respond("\"r1\"\n" + Ok + "\n\"stray\"");
            Assert.Equal("\"r1\"", (await p1).Trim());
            // stray 残留在 buffer；下一条命令 assign 时 buffer 被清空，stray 不影响解析
            Task<string> p2 = pool.ExecuteAsync("c2", PsPoolMode.Json, 5000);
            h.Respond("\"r2\"\n" + Ok);
            Assert.Equal("\"r2\"", (await p2).Trim());
        }
        finally
        {
            pool.Shutdown();
        }
    }
}

