// 测试替身：等价 TS 测试的 vi.mock('../lib/powershell')。
// 只记录命令字符串 + 队列化结果/异常，不触发任何真实 PowerShell（写域干跑）。
using System.Text.Json;
using WinShare.Core.Lib;

namespace WinShare.Core.Tests.Services.Shares;

/// <summary>
/// 假 <see cref="IPsInvoker"/>：记录命令串 + 预置返回值/异常队列。
/// 对齐 TS share.test.ts / smb.test.ts 的 mockedRunPowerShell / mockedRunPowerShellVoid。
/// </summary>
internal sealed class FakePsInvoker : IPsInvoker
{
    public List<string> JsonCommands { get; } = new();
    public List<string> VoidCommands { get; } = new();

    private readonly Queue<JsonElement> _jsonResults = new();
    private readonly Queue<Exception> _jsonFailures = new();
    private readonly Queue<Exception?> _voidResults = new();

    /// <summary>JSON 对象字符串 → JsonElement（生产池 JSON 模式返回 ConvertTo-Json 结果）。</summary>
    public void WhenJsonObject(string json) =>
        _jsonResults.Enqueue(JsonDocument.Parse(json).RootElement.Clone());

    /// <summary>JSON 字符串值 → JsonElement（单值查询返回 JSON 串）。</summary>
    public void WhenJsonString(string value) =>
        _jsonResults.Enqueue(JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone());

    /// <summary>TS: mockedRunPowerShell.mockResolvedValue(null) —— 空输出归一化为 []。</summary>
    public void WhenJsonEmptyArray() => _jsonResults.Enqueue(JsonDocument.Parse("[]").RootElement.Clone());

    public void WhenJsonFails(Exception ex) => _jsonFailures.Enqueue(ex);
    public void WhenVoidOk() => _voidResults.Enqueue(null);
    public void WhenVoidFails(Exception ex) => _voidResults.Enqueue(ex);

    /// <summary>最后一次 VOID 命令（TS: mockedRunPowerShellVoid.mock.calls.at(-1)?.[0]）。</summary>
    public string? LastVoid => VoidCommands.Count > 0 ? VoidCommands[^1] : null;

    public Task<JsonElement> RunJsonAsync(string command, int timeoutMs, int retries)
    {
        JsonCommands.Add(command);
        if (_jsonFailures.Count > 0) return Task.FromException<JsonElement>(_jsonFailures.Dequeue());
        if (_jsonResults.Count > 0) return Task.FromResult(_jsonResults.Dequeue());
        // TS: mockedRunPowerShell.mockResolvedValue(null) 缺省（单值期望查询无结果）
        return Task.FromResult(JsonDocument.Parse("[]").RootElement.Clone());
    }

    public Task RunVoidAsync(string command, int timeoutMs, int retries)
    {
        VoidCommands.Add(command);
        if (_voidResults.Count > 0)
        {
            Exception? fail = _voidResults.Dequeue();
            return fail is null ? Task.CompletedTask : Task.FromException(fail);
        }
        // TS: mockedRunPowerShellVoid.mockResolvedValue(undefined) 缺省
        return Task.CompletedTask;
    }
}
