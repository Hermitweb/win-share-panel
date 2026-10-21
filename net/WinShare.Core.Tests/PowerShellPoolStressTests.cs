using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Ps;

namespace WinShare.Core.Tests;

/// <summary>
/// electron/lib/powershellPool.stress.test.ts 的 C# 逐场景移植（100 并发压力模拟，3 个场景）。
/// 全局并发计数（跨所有 worker）：跟踪峰值并发，验证不超过池大小。
/// </summary>
public class PowerShellPoolStressTests
{
    private static readonly string Ok = "___PS_OK_TEST___";

    // 并发计数
    private readonly object _countLock = new();
    private int _active;
    private int _peak;
    // 可配置延迟函数：每条命令的响应延迟（ms）；PositiveInfinity = 卡死（等超时）
    // 通过引用读取，测试中修改后对所有（含懒 spawn 的）worker 生效
    private Func<string, double> _delayFn = _ => 8;

    // 压力测试用伪造 worker：收到载荷后按 DelayFn 调度响应
    private sealed class StressWorkerHandle : IWorkerHandle
    {
        private readonly PowerShellPoolStressTests _owner;
        public StressWorkerHandle(PowerShellPoolStressTests owner)
        {
            _owner = owner;
        }

        public List<string> Written { get; } = new();
        public bool Killed { get; private set; }
        private bool _pending;

        public event Action<string>? Stdout;
        public event Action<int?>? Exited;

        public void Write(string line)
        {
            lock (Written)
            {
                string payload = line.EndsWith("\n", StringComparison.Ordinal) ? line[..^1] : line;
                Written.Add(payload);
                string decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(payload));
                int sep = decoded.IndexOf('\u0000');
                string command = decoded[(sep + 1)..];
                _pending = true;
                _owner.NoteStart();
                double delay = _owner._delayFn(command);
                if (double.IsFinite(delay))
                {
                    Task.Delay((int)delay).ContinueWith(_ =>
                    {
                        lock (this)
                        {
                            if (Killed)
                            {
                                return; // 超时被 kill，不再响应
                            }
                            _pending = false;
                        }
                        _owner.NoteEnd();
                        Stdout?.Invoke($"\"{command}-result\"\n" + Ok);
                    });
                }
                // delay == PositiveInfinity → 不响应，等待池超时 kill
            }
        }

        public bool Kill()
        {
            lock (this)
            {
                // 超时 kill：若有在飞命令，释放并发计数
                if (_pending)
                {
                    _pending = false;
                    _owner.NoteEnd();
                }
                Killed = true;
            }
            // kill 会触发真实进程 exit；模拟之（OnExit 因 alive=false 早退）
            Exited?.Invoke(1);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private void NoteStart()
    {
        lock (_countLock)
        {
            _active++;
            if (_active > _peak)
            {
                _peak = _active;
            }
        }
    }

    private void NoteEnd()
    {
        lock (_countLock)
        {
            _active--;
        }
    }

    private PowerShellPool MakeStressPool(int size, List<StressWorkerHandle> handles) =>
        new(new PsPoolOptions { Size = size, Token = "TEST" },
            _ =>
            {
                var h = new StressWorkerHandle(this);
                handles.Add(h);
                return h;
            });

    [Fact]
    /// <summary>[Source: powershellPool.stress.test.ts#场景1：100 并发全部成功，size=3 → 排队执行、worker 复用无 churn、并发不超限]</summary>
    public async Task Scenario1_100Concurrent_AllSucceed_NoChurn_ConcurrencyCapped()
    {
        var handles = new List<StressWorkerHandle>();
        _delayFn = _ => 8; // 每条命令 8ms
        var pool = MakeStressPool(3, handles);
        try
        {
            const int n = 100;
            string[] commands = Enumerable.Range(0, n).Select(i => "c" + i).ToArray();
            long t0 = Environment.TickCount64;
            string[] results = await Task.WhenAll(commands.Select(cmd => pool.ExecuteAsync(cmd, PsPoolMode.Json, 5000)).ToArray());
            long elapsed = Environment.TickCount64 - t0;

            // 全部成功且结果唯一
            Assert.Equal(n, results.Length);
            Assert.Equal(n, results.Select(r => r.Trim()).Distinct().Count());

            // 仅 spawn 3 个 worker（无 churn），证明排队复用
            Assert.Equal(3, handles.Count);
            // 每个 worker 处理多条命令（约 N/3）
            int totalWrites = handles.Sum(h => h.Written.Count);
            Assert.Equal(n, totalWrites);
            foreach (StressWorkerHandle h in handles)
            {
                Assert.True(h.Written.Count > 20); // 每个 worker 处理 30+ 条
            }

            // 峰值并发不超过池大小，且 100 命令足以打满
            lock (_countLock)
            {
                Assert.Equal(3, _peak);
            }
            // 无并发计数泄漏
            lock (_countLock)
            {
                Assert.Equal(0, _active);
            }

            // 总耗时合理：≈ ceil(100/3)*8ms ≈ 272ms（留宽松上下界）
            Assert.True(elapsed > 150, "expected >150ms, actual " + elapsed + "ms");
            Assert.True(elapsed < 2000, "expected <2000ms, actual " + elapsed + "ms");
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.stress.test.ts#场景2：100 并发含 10 条卡死命令（超时），size=3 → 超时 reject + 中毒重启 + 其余成功]</summary>
    public async Task Scenario2_100Concurrent_10Hanged_TimeoutRestartAndRestSucceed()
    {
        var handles = new List<StressWorkerHandle>();
        var pool = MakeStressPool(3, handles);
        try
        {
            const int n = 100;
            // 索引 % 10 == 9 的命令卡死（c9,c19,...,c99 共 10 条），其余 6ms 响应
            _delayFn = cmd =>
            {
                Match m = Regex.Match(cmd, @"^c(\d+)$");
                int idx = m.Success ? int.Parse(m.Groups[1].Value) : -1;
                return idx % 10 == 9 ? double.PositiveInfinity : 6;
            };

            string[] commands = Enumerable.Range(0, n).Select(i => "c" + i).ToArray();
            long t0 = Environment.TickCount64;
            Task<string>[] tasks = commands.Select(cmd => pool.ExecuteAsync(cmd, PsPoolMode.Json, 25)).ToArray();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
                // allSettled 等价：await 会展开内部异常（catch-all），逐 Task 检视结算状态
            }
            long elapsed = Environment.TickCount64 - t0;

            var fulfilled = tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToList();
            var rejected = tasks.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerExceptions.First()).ToList();

            // 10 条超时 reject，90 条成功
            Assert.Equal(90, fulfilled.Count);
            Assert.Equal(10, rejected.Count);
            foreach (Exception e in rejected)
            {
                Assert.Equal("Command timed out", e.Message);
            }
            // 成功的结果唯一
            Assert.Equal(90, fulfilled.Select(r => r.Trim()).Distinct().Count());

            // 峰值并发不超过池大小（超时 kill 后补 spawn，不超额）
            lock (_countLock)
            {
                Assert.True(_peak <= 3);
            }
            // 无并发计数泄漏
            lock (_countLock)
            {
                Assert.Equal(0, _active);
            }

            // worker churn：初始 3 + 10 次超时重启，spawn 总数 <= 3 + 10
            Assert.True(handles.Count >= 3, "spawn total should be >=3, actual " + handles.Count);
            Assert.True(handles.Count <= 3 + 10, "spawn total should be <=13, actual " + handles.Count);
            // 被超时 kill 的 worker 共 10 个（10 条慢命令各 kill 一个）
            int killedCount = handles.Count(h => h.Killed);
            Assert.Equal(10, killedCount);
            _ = elapsed;
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: powershellPool.stress.test.ts#场景3：100 并发全部卡死（极端），size=3 timeout=20 → 全部超时，无丢失/悬挂，池可恢复]</summary>
    public async Task Scenario3_100Concurrent_AllHanged_AllTimeout_PoolRecoverable()
    {
        var handles = new List<StressWorkerHandle>();
        var pool = MakeStressPool(3, handles);
        try
        {
            const int n = 100;
            _delayFn = _ => double.PositiveInfinity; // 全部卡死

            string[] commands = Enumerable.Range(0, n).Select(i => "c" + i).ToArray();
            long t0 = Environment.TickCount64;
            Task<string>[] tasks = commands.Select(cmd => pool.ExecuteAsync(cmd, PsPoolMode.Json, 20)).ToArray();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
                // allSettled 等价：全部超时 reject，逐 Task 检视
            }
            long elapsed = Environment.TickCount64 - t0;

            // 全部超时 reject，无 fulfilled，无悬挂
            Assert.Equal(n, tasks.Length);
            var rejected = tasks.Where(t => t.IsFaulted).ToArray();
            Assert.Equal(n, rejected.Length);
            foreach (string? _ in rejected.Select(t => t.Exception!.InnerExceptions.First().Message))
            {
                Assert.Equal("Command timed out", _);
            }

            // 峰值并发不超过池大小，无计数泄漏
            lock (_countLock)
            {
                Assert.True(_peak <= 3);
                Assert.Equal(0, _active);
            }

            // 每条命令都 kill 了一个 worker
            Assert.Equal(n, handles.Count(h => h.Killed));

            Assert.True(elapsed < 5000, "expected <5000ms, actual " + elapsed + "ms");

            // 池仍可用：后续命令能正常执行（worker 已恢复）
            _delayFn = _ => 5;
            string after = await pool.ExecuteAsync("after-recovery", PsPoolMode.Json, 5000);
            Assert.Equal("\"after-recovery-result\"", after.Trim());
        }
        finally
        {
            pool.Shutdown();
        }
    }
}

