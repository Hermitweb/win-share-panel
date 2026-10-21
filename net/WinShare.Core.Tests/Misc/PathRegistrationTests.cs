// 移植自 electron/services/pathRegistration.test.ts（TS: pathRegistration.ts → C#: PathRegistrationService）。
// 同一手法：vi.mock('../lib/powershell') → C# 注入 FakePs : IPsInvoker（只断言"命令拼对了没"，不触发任何 PS）。
// TS 的 electron app mock（isPackaged=true + resourcesPath）→ 构造参数 (appDir, isPackaged=true)。
using System.Text.Json;
using System.Text.Json.Nodes;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Misc;

namespace WinShare.Core.Tests.Misc;

public class PathRegistrationTests
{
    // 等价 TS vi.mock('../lib/powershell')：命令字符串记录 + 结果队列
    private sealed class FakePs : IPsInvoker
    {
        public List<string> JsonCommands { get; } = new();
        public List<string> VoidCommands { get; } = new();
        private readonly Queue<JsonElement> _jsonResults = new();
        private readonly Queue<Exception> _jsonFailures = new();
        private readonly Queue<Exception?> _voidResults = new();

        // JSON 字符串值 → JsonElement（生产池 JSON 模式对字符串结果输出 JSON 串，等价）
        public void WhenJson(string value) => _jsonResults.Enqueue(JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone());
        public void WhenJsonFails(Exception ex) => _jsonFailures.Enqueue(ex);
        public void WhenVoidOk() => _voidResults.Enqueue(null);
        public void WhenVoidFails(Exception ex) => _voidResults.Enqueue(ex);

        public Task<JsonElement> RunJsonAsync(string command, int timeoutMs, int retries)
        {
            JsonCommands.Add(command);
            if (_jsonFailures.Count > 0) return Task.FromException<JsonElement>(_jsonFailures.Dequeue());
            if (_jsonResults.Count > 0) return Task.FromResult(_jsonResults.Dequeue());
            return Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize("")).RootElement.Clone());
        }

        public Task RunVoidAsync(string command, int timeoutMs, int retries)
        {
            VoidCommands.Add(command);
            if (_voidResults.Count > 0)
            {
                Exception? fail = _voidResults.Dequeue();
                return fail is null
                    ? Task.CompletedTask
                    : Task.FromException(fail);
            }
            return Task.CompletedTask;
        }
    }

    // TS: Object.defineProperty(process, 'resourcesPath', 'C:\Program Files\WinShare Panel\resources')
    //    → 打包版 appDir = dirname(resourcesPath)
    private static readonly string AppDir = @"C:\Program Files\WinShare Panel";

    private static (PathRegistrationService Svc, FakePs Ps) Make()
    {
        var ps = new FakePs();
        return (new PathRegistrationService(AppDir, isPackaged: true, ps), ps);
    }

    // ===== isAppInUserPath =====

    // [Source: pathRegistration.test.ts#isAppInUserPath > 已含本应用目录（精确匹配）→ true]
    [Fact]
    public async Task IsAppInUserPath_ExactMatch()
    {
        var (svc, ps) = Make();
        ps.WhenJson(@"C:\other;C:\Program Files\WinShare Panel;D:\bin");
        Assert.True(await svc.IsAppInUserPathAsync());
    }

    // [Source: pathRegistration.test.ts#isAppInUserPath > 已含带引号的目录 → true]
    [Fact]
    public async Task IsAppInUserPath_QuotedDir()
    {
        var (svc, ps) = Make();
        ps.WhenJson(@"""C:\Program Files\WinShare Panel"";C:\other");
        Assert.True(await svc.IsAppInUserPathAsync());
    }

    // [Source: pathRegistration.test.ts#isAppInUserPath > PATH 缺失 → false]
    [Fact]
    public async Task IsAppInUserPath_MissingPath()
    {
        var (svc, ps) = Make();
        ps.WhenJson("");
        Assert.False(await svc.IsAppInUserPathAsync());
    }

    // [Source: pathRegistration.test.ts#isAppInUserPath > 相似但不精确匹配（子路径）→ false]
    [Fact]
    public async Task IsAppInUserPath_SimilarSubPath()
    {
        var (svc, ps) = Make();
        ps.WhenJson(@"C:\Program Files\WinShare Panel Sub");
        Assert.False(await svc.IsAppInUserPathAsync());
    }

    // ===== registerAppInUserPath =====

    // [Source: pathRegistration.test.ts#registerAppInUserPath > 未注册：追加并广播（PS 可用）]
    [Fact]
    public async Task Register_AppendsAndBroadcasts()
    {
        var (svc, ps) = Make();
        ps.WhenJson(@"C:\existing");
        ps.WhenVoidOk();   // setUserPath
        ps.WhenVoidOk();   // broadcast
        PathRegistration r = await svc.RegisterAppInUserPathAsync();
        Assert.True(r.registered);
        Assert.True(r.broadcast);
        // 追加后的 PATH 是 C:\existing;C:\Program Files\WinShare Panel
        Assert.Contains("Set-ItemProperty", ps.VoidCommands[0]);
        Assert.Contains(@"C:\existing", ps.VoidCommands[0]);
        Assert.Contains("WinShare Panel", ps.VoidCommands[0]);
    }

    // [Source: pathRegistration.test.ts#registerAppInUserPath > 已注册：幂等返回（不重写 PATH）]
    [Fact]
    public async Task Register_AlreadyRegisteredIsIdempotent()
    {
        var (svc, ps) = Make();
        ps.WhenJson(@"C:\existing;C:\Program Files\WinShare Panel");
        PathRegistration r = await svc.RegisterAppInUserPathAsync();
        Assert.True(r.registered);
        Assert.Contains("无需重复注册", r.message);
        // 只有 1 次读，没有写入
        Assert.Single(ps.JsonCommands);
        Assert.Empty(ps.VoidCommands);
    }

    // [Source: pathRegistration.test.ts#registerAppInUserPath > PATH 广播失败（PS 抛错）：registered 仍为 true，message 注明需重开终端]
    [Fact]
    public async Task Register_BroadcastFailureStillRegistered()
    {
        var (svc, ps) = Make();
        ps.WhenJson("");
        ps.WhenVoidOk();                    // setUserPath 成功
        ps.WhenVoidFails(new Exception("PS 不可用")); // broadcastPathChange 抛错
        PathRegistration r = await svc.RegisterAppInUserPathAsync();
        Assert.True(r.registered);
        Assert.False(r.broadcast);
        Assert.Contains("需重开终端", r.message);
    }

    // ===== unregisterAppFromUserPath =====

    // [Source: pathRegistration.test.ts#unregisterAppFromUserPath > 已注册：移除本应用目录，保留其他段]
    [Fact]
    public async Task Unregister_RemovesAppDirKeepsOtherSegments()
    {
        var (svc, ps) = Make();
        ps.WhenJson(@"C:\a;C:\Program Files\WinShare Panel;C:\b");
        ps.WhenVoidOk();
        ps.WhenVoidOk();
        PathRegistration r = await svc.UnregisterAppFromUserPathAsync();
        Assert.False(r.registered);
        // TS: writeCall toMatch(/C:\\a;C:\\b/) 且 not.toMatch(/WinShare Panel/)
        string writeCall = ps.VoidCommands[0];
        Assert.Contains(@"C:\a;C:\b", writeCall);
        Assert.DoesNotContain("WinShare Panel", writeCall);
    }

    // [Source: pathRegistration.test.ts#unregisterAppFromUserPath > 未注册：幂等返回，不重写]
    [Fact]
    public async Task Unregister_NotRegisteredIsNoOp()
    {
        var (svc, ps) = Make();
        ps.WhenJson(@"C:\a;C:\b");
        PathRegistration r = await svc.UnregisterAppFromUserPathAsync();
        Assert.False(r.registered);
        Assert.Contains("无需撤销", r.message);
        Assert.Empty(ps.VoidCommands);
    }

    // ===== setLoginShortcut =====

    // [Source: pathRegistration.test.ts#setLoginShortcut > enabled=true：写 HKCU Run 键]
    [Fact]
    public async Task SetLoginShortcut_EnabledWritesRunKey()
    {
        var (svc, ps) = Make();
        string r = await svc.SetLoginShortcutAsync(true);
        Assert.Contains("已写入登录快速启动", r);
        Assert.Contains("Set-ItemProperty", ps.VoidCommands[0]);
        Assert.Contains("Run", ps.VoidCommands[0]);
        Assert.Contains("WinSharePanel", ps.VoidCommands[0]);
    }

    // [Source: pathRegistration.test.ts#setLoginShortcut > enabled=false：删 HKCU Run 键]
    [Fact]
    public async Task SetLoginShortcut_DisabledRemovesRunKey()
    {
        var (svc, ps) = Make();
        string r = await svc.SetLoginShortcutAsync(false);
        Assert.Contains("已删除登录快速启动", r);
        Assert.Contains("Remove-ItemProperty", ps.VoidCommands[0]);
    }

    // ===== readServicePendingMarker =====

    // [Source: pathRegistration.test.ts#readServicePendingMarker > HKLM ServicePending=1 → true]
    [Fact]
    public async Task ServicePendingMarker_OneIsTrue()
    {
        var (svc, ps) = Make();
        ps.WhenJson("1");
        Assert.True(await svc.ReadServicePendingMarkerAsync());
    }

    // [Source: pathRegistration.test.ts#readServicePendingMarker > HKLM ServicePending 缺失 → false]
    [Fact]
    public async Task ServicePendingMarker_MissingIsFalse()
    {
        var (svc, ps) = Make();
        ps.WhenJson("");
        Assert.False(await svc.ReadServicePendingMarkerAsync());
    }

    // [Source: pathRegistration.test.ts#readServicePendingMarker > PS 调用抛错 → false（静默降级）]
    [Fact]
    public async Task ServicePendingMarker_PsFailureIsFalse()
    {
        var (svc, ps) = Make();
        ps.WhenJsonFails(new Exception("PS 不可用"));
        Assert.False(await svc.ReadServicePendingMarkerAsync());
    }

    // ===== 补冒烟（TS 无对应用例）=====

    // [Source: 新增] getUserPath：PS 返回值直透 / PS 抛错静默空串
    [Fact]
    public async Task GetUserPath_PassthroughAndSilentFailure()
    {
        var (svc, ps) = Make();
        ps.WhenJson(@"C:\a;C:\b");
        Assert.Equal(@"C:\a;C:\b", await svc.GetUserPathAsync());

        var (svc2, ps2) = Make();
        ps2.WhenJsonFails(new Exception("PS 不可用"));
        Assert.Equal("", await svc2.GetUserPathAsync());
    }

    // [Source: 新增] setUserPath：空串 → Remove-ItemProperty；非空 → Set-ItemProperty + 单引号转义（' → ''）
    [Fact]
    public async Task SetUserPath_ClearsOrWritesWithPsQuote()
    {
        var (svc, ps) = Make();
        await svc.SetUserPathAsync("");
        Assert.Contains("Remove-ItemProperty", ps.VoidCommands[0]);

        var (svc2, ps2) = Make();
        await svc2.SetUserPathAsync(@"C:\x\C's");
        string cmd = ps2.VoidCommands[0];
        Assert.Contains("Set-ItemProperty", cmd);
        Assert.Contains(@"C:\x\C''s", cmd);

    }

    // [Source: 新增] getLoginShortcut：非空值 true / 空值 false / PS 抛错 false
    [Fact]
    public async Task GetLoginShortcut_ValuePresence()
    {
        var (svc, ps) = Make();
        ps.WhenJson(@"C:\apps\WinShare Panel.exe --silent");
        Assert.True(await svc.GetLoginShortcutAsync());

        var (svc2, ps2) = Make();
        ps2.WhenJson("");
        Assert.False(await svc2.GetLoginShortcutAsync());

        var (svc3, ps3) = Make();
        ps3.WhenJsonFails(new Exception("PS 不可用"));
        Assert.False(await svc3.GetLoginShortcutAsync());
    }
}
