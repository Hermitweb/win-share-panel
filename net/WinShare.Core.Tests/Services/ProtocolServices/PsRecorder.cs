// 测试替身：等价 TS 测试的 vi.mock('../lib/powershell')。
// 与 Shares/FakePsInvoker 的差别：额外记录 (timeout, retries)——本域 TS 有
// `{ retries: 0 }` / `{ retries: 0, timeout: 30000 }` 这样的显式选项，必须可断言。
// 只记录命令串 + 队列化结果/异常，绝不触发真实 PowerShell（IIS 写域全部干跑）。
using System.Text.Json;
using WinShare.Core.Lib;

namespace WinShare.Core.Tests.Services.ProtocolServices;

/// <summary>一次 PS 调用的完整记录（命令串 + 超时 + 重试次数）。</summary>
internal sealed record PsCall(string Command, int TimeoutMs, int Retries);

/// <summary>假 <see cref="IPsInvoker"/>。</summary>
internal sealed class PsRecorder : IPsInvoker
{
    public List<PsCall> JsonCalls { get; } = new();
    public List<PsCall> VoidCalls { get; } = new();

    private readonly Queue<Result> _json = new();
    private readonly Queue<Exception?> _void = new();

    public PsRecorder WhenJson(string json) =>
        Enqueue(Result.Value(JsonDocument.Parse(json).RootElement.Clone()));

    public PsRecorder WhenJsonFails(Exception ex) => Enqueue(Result.Failure(ex));

    public PsRecorder WhenVoidOk() { _void.Enqueue(null); return this; }

    public PsRecorder WhenVoidFails(Exception ex) { _void.Enqueue(ex); return this; }

    /// <summary>最后一次 VOID 命令（TS: mockedRunPowerShellVoid.mock.calls.at(-1)?.[0]）。</summary>
    public PsCall LastVoid => VoidCalls[^1];

    public Task<JsonElement> RunJsonAsync(string command, int timeoutMs, int retries)
    {
        JsonCalls.Add(new PsCall(command, timeoutMs, retries));
        if (_json.Count > 0)
        {
            Result r = _json.Dequeue();
            return r.Error is null ? Task.FromResult(r.Element) : Task.FromException<JsonElement>(r.Error);
        }
        // 缺省：等价 TS mockedRunPowerShell.mockResolvedValue(null) —— 空输出归一化为 []
        return Task.FromResult(JsonDocument.Parse("[]").RootElement.Clone());
    }

    public Task RunVoidAsync(string command, int timeoutMs, int retries)
    {
        VoidCalls.Add(new PsCall(command, timeoutMs, retries));
        if (_void.Count > 0)
        {
            Exception? fail = _void.Dequeue();
            return fail is null ? Task.CompletedTask : Task.FromException(fail);
        }
        return Task.CompletedTask;
    }

    private PsRecorder Enqueue(Result r) { _json.Enqueue(r); return this; }

    private readonly record struct Result(JsonElement Element, Exception? Error)
    {
        public static Result Value(JsonElement e) => new(e, null);
        public static Result Failure(Exception ex) => new(default, ex);
    }
}

/// <summary>命令串断言助手。</summary>
internal static class PsCount
{
    /// <summary>子串出现次数（断言「命令段落数」而不复制生产侧字段表）。</summary>
    public static int Of(string haystack, string needle)
    {
        int count = 0;
        int at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }
}
