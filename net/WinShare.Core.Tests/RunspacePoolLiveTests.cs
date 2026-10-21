using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Ps;

namespace WinShare.Core.Tests;

/// <summary>
/// 进程内 Runspace 端到端冒烟（对应 TS 池测试中"默认工厂真实 worker"的路径，但无需外部进程）：
/// 真实 RunspaceWorkerHandle + PowerShellPool 全协议；JSON 序列化经同 Runspace 内
/// ConvertTo-Json -Depth 5 -Compress（与 TS worker 端管道语义一致）。
/// </summary>
public class RunspacePoolLiveTests
{
    [Fact]
    /// <summary>[Source: 新增 - 对应 powershellPool.test.ts#单命令 JSON 模式的真实执行路径（TS 用伪造 worker；此处跑真 Runspace）]</summary>
    public async Task JsonMode_RealRunspace_ReturnsConvertToJsonResult()
    {
        var pool = new PowerShellPool(new PsPoolOptions { Size = 1 });
        try
        {
            string json = await pool.ExecuteAsync("[PSCustomObject]@{name='share1'}", PsPoolMode.Json, 30000);
            using JsonDocument doc = JsonDocument.Parse(json.Trim());
            Assert.Equal("share1", doc.RootElement.GetProperty("name").GetString());
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: 新增 - 对应 powershellPool.test.ts#多行 JSON 输出（进程内版：多对象结果 → JSON 数组）]</summary>
    public async Task JsonMode_MultiObjectResult_SerializesAsArray()
    {
        var pool = new PowerShellPool(new PsPoolOptions { Size = 1 });
        try
        {
            string json = await pool.ExecuteAsync("'a','b'", PsPoolMode.Json, 30000);
            using JsonDocument doc = JsonDocument.Parse(json.Trim());
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            string[] items = doc.RootElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Equal(new[] { "a", "b" }, items);
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: 新增 - 对应 powershellPool.test.ts#VOID 模式：空输出，返回空串（真实执行 1+1）]</summary>
    public async Task VoidMode_RealExecution_ReturnsEmptyString()
    {
        var pool = new PowerShellPool(new PsPoolOptions { Size = 1 });
        try
        {
            string r = await pool.ExecuteAsync("1+1", PsPoolMode.Void, 30000);
            Assert.Equal("", r);
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: 新增 - 对应 powershellPool.ts B-1 语义：$ErrorActionPreference='Stop' 下非终止性错误必须暴露为失败（不得假成功）]</summary>
    public async Task NonTerminatingError_SurfacesAsFailure()
    {
        var pool = new PowerShellPool(new PsPoolOptions { Size = 1 });
        try
        {
            PsCommandFailedException ex = await Assert.ThrowsAsync<PsCommandFailedException>(
                async () => await pool.ExecuteAsync("Write-Error 'boom'", PsPoolMode.Json, 30000));
            Assert.Contains("boom", ex.Stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            pool.Shutdown();
        }
    }

    [Fact]
    /// <summary>[Source: 新增 - 对应 stress 场景1 的 worker 复用语义：4 命令 / 2 worker 经排队全部成功且结果唯一]</summary>
    public async Task ConcurrentReuse_FourCommands_TwoWorkers_AllSucceed()
    {
        var pool = new PowerShellPool(new PsPoolOptions { Size = 2 });
        try
        {
            Task<string>[] tasks = Enumerable.Range(0, 4)
                .Select(i => pool.ExecuteAsync("'" + "c" + i + "'", PsPoolMode.Json, 30000))
                .ToArray();
            string[] results = await Task.WhenAll(tasks);
            for (int i = 0; i < 4; i++)
            {
                using JsonDocument doc = JsonDocument.Parse(results[i].Trim());
                Assert.Equal("c" + i, doc.RootElement.GetString());
            }
        }
        finally
        {
            pool.Shutdown();
        }
    }
}

