using WinShare.App.Sessions;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Sessions;

/// <summary>ISessionService 可配置 stub。</summary>
public sealed class StubSessionService : ISessionService
{
    public IReadOnlyList<SmbSession> Sessions { get; set; } = Array.Empty<SmbSession>();
    public IReadOnlyList<SmbOpenFile> Files { get; set; } = Array.Empty<SmbOpenFile>();
    public List<string> CloseCalls { get; } = new();
    public HashSet<string> FailClose { get; } = new();
    public Exception? ThrowOnList { get; set; }

    public Task<IReadOnlyList<SmbSession>> ListSessionsAsync()
    {
        if (ThrowOnList is not null) throw ThrowOnList;
        return Task.FromResult(Sessions);
    }
    public Task<IReadOnlyList<SmbOpenFile>> ListOpenFilesAsync() => Task.FromResult(Files);
    public Task CloseSessionAsync(string clientUserName)
    {
        CloseCalls.Add(clientUserName);
        if (FailClose.Contains(clientUserName)) throw new InvalidOperationException("stub fail close " + clientUserName);
        return Task.CompletedTask;
    }
    public Task CloseFileAsync(string fileId) => Task.CompletedTask;
}

/// <summary>会话馈送 stub（可配置：每协议数据 / 失败注入）。</summary>
public sealed class StubSessionsDataFeed : ISessionsDataFeed
{
    public Dictionary<Protocol, SessionsData> Data { get; } = new();
    public HashSet<Protocol> ThrowProtos { get; } = new();
    public List<(Protocol proto, string id)> Closes { get; } = new();
    public HashSet<string> FailCloseIds { get; } = new();

    public Task<SessionsData> GetAsync(Protocol proto)
    {
        if (ThrowProtos.Contains(proto))
            return Task.FromException<SessionsData>(new InvalidOperationException($"stub fail {proto}"));
        return Task.FromResult(Data.TryGetValue(proto, out var d) ? d : new SessionsData());
    }
    public Task CloseSessionAsync(Protocol proto, string sessionId)
    {
        Closes.Add((proto, sessionId));
        if (FailCloseIds.Contains(sessionId))
            return Task.FromException(new InvalidOperationException("stub fail close"));
        return Task.CompletedTask;
    }
    public Task CloseFileAsync(string fileId) => Task.CompletedTask;
}

/// <summary>可捕获创建参数的 Timer 工厂（System.Threading.Timer 为 sealed，不能派生；记录周期即可）。</summary>
public sealed class TimerFactoryCapture
{
    public List<System.Threading.Timer> Created { get; } = new();
    public List<int> Periods { get; } = new();

    public System.Threading.Timer CreatePoll(int sec)
    {
        var t = new System.Threading.Timer(_ => { }, null, int.MaxValue, sec * 1000);
        Created.Add(t);
        Periods.Add(sec * 1000);
        return t;
    }

    public System.Threading.Timer CreateCount(int sec)
    {
        var t = new System.Threading.Timer(_ => { }, null, int.MaxValue, 1000);
        Created.Add(t);
        Periods.Add(1000);
        return t;
    }
}

/// <summary>
/// 会话监控 ViewModel（对齐 src/pages/Sessions.tsx）：
/// SmbToSession 映射 / 协议切换重置 / 间隔归一 / 倒计时回卷 / 强断确认语义（VM 结果文案）/
/// 新增会话检测（60s 去重 + 冷启动不轰炸）/ 首轮失败提示。
/// </summary>
public class SessionsViewModelTests
{
    static ProtocolSession PSession(string user, Protocol proto = Protocol.Smb, string sessionStart = "2024-01-01T08:00:00Z") => new()
    {
        protocol = proto,
        sessionId = user,
        clientUserName = user,
        clientComputerName = "PC-" + user,
        sessionStartTime = sessionStart,
        clientOpenFiles = 1,
        clientIdleTime = 10,
        bytesReceived = 100,
        bytesSent = 200,
    };

    // ===== SmbToSession 映射（对齐 smbToSession） =====

    [Source("[Source: src/pages/Sessions.tsx smbToSession]")]
    [Fact]
    public void SmbToSession_sessionId复用clientUserName()
    {
        var s = new SmbSession
        {
            clientId = "x",
            clientUserName = "Alice",
            clientComputerName = "PC1",
            sessionStartTime = "2024-06-01T00:00:00Z",
            clientOpenFiles = 3,
            clientIdleTime = 5,
            bytesReceived = 11,
            bytesSent = 22,
        };
        var p = ContractSessionsDataFeed.SmbToSession(s);
        Assert.Equal("Alice", p.sessionId);
        Assert.Equal(Protocol.Smb, p.protocol);
        Assert.Equal(3, p.clientOpenFiles);
        Assert.Equal(11, p.bytesReceived);
    }

    // ===== 馈送（缺席降级 / 协议分支） =====

    [Source("[Source: src/pages/Sessions.tsx queryFn 协议分支 + T3a 空馈送模式]")]
    [Fact]
    public async Task 馈送_smb取数_缺席与ftp_webdav空态()
    {
        var svc = new StubSessionService
        {
            Sessions = new[] { new SmbSession { clientUserName = "u1", clientComputerName = "c" } },
            Files = new[] { new SmbOpenFile { fileId = "7", path = @"C:\a", clientUserName = "u1" } },
        };
        var feed = new ContractSessionsDataFeed(() => svc);
        var smb = await feed.GetAsync(Protocol.Smb);
        Assert.Single(smb.Sessions);
        Assert.Equal("u1", smb.Sessions[0].sessionId);
        Assert.Single(smb.Files);

        var absent = new ContractSessionsDataFeed(() => null);
        Assert.Empty((await absent.GetAsync(Protocol.Smb)).Sessions);

        Assert.Empty((await feed.GetAsync(Protocol.Ftp)).Sessions);
        Assert.Empty((await feed.GetAsync(Protocol.Webdav)).Sessions);
    }

    [Source("[Source: src/pages/Sessions.tsx queryFn nfs 分支（api.adapter.sessions('nfs')）]")]
    [Fact]
    public async Task 馈送_nfs走适配器_缺席时空态()
    {
        var feedNoAdapter = new ContractSessionsDataFeed(() => null, () => null);
        Assert.Empty((await feedNoAdapter.GetAsync(Protocol.Nfs)).Sessions);
    }

    // ===== 间隔 / 倒计时 / 暂停（对齐 INTERVAL_OPTIONS + countdown + paused） =====

    [Source("[Source: src/pages/Sessions.tsx INTERVAL_OPTIONS / intervalSec 语义]")]
    [Fact]
    public void 间隔_非选项值收敛到5()
    {
        var vm = new SessionsViewModel(new StubSessionsDataFeed());
        vm.IntervalSec = 7;
        Assert.Equal(5, vm.IntervalSec);
        vm.IntervalSec = 30;
        Assert.Equal(30, vm.IntervalSec);
        vm.IntervalSec = 1;
        Assert.Equal(1, vm.IntervalSec);
    }

    [Source("[Source: src/pages/Sessions.tsx 倒计时 1s 归零回卷]")]
    [Fact]
    public void 倒计时_归零回卷到间隔值()
    {
        var vm = new SessionsViewModel(new StubSessionsDataFeed());
        vm.IntervalSec = 5;
        Assert.Equal(5, vm.Countdown);
        vm.TickCountdown();
        Assert.Equal(4, vm.Countdown);
        vm.TickCountdown();
        vm.TickCountdown();
        vm.TickCountdown();
        Assert.Equal(1, vm.Countdown);
        vm.TickCountdown();
        Assert.Equal(5, vm.Countdown); // 归零回卷（1s 归零即回卷，对齐 TS c<=1 ? intervalSec : c-1）
    }

    [Source("[Source: src/pages/Sessions.tsx paused=interval false（轮询停、倒计时照走）]")]
    [Fact]
    public void 暂停_停轮询计时器_倒计时保持()
    {
        var feed = new StubSessionsDataFeed();
        var cap = new TimerFactoryCapture();
        var vm = new SessionsViewModel(
            feed,
            pollTimerFactory: cap.CreatePoll,
            countTimerFactory: cap.CreateCount);
        vm.StartAsync().GetAwaiter().GetResult();
        Assert.Equal(2, cap.Created.Count); // 轮询 + 倒计时

        vm.TogglePauseCommand.Execute(null);
        Assert.True(vm.Paused);
        Assert.Equal(3, cap.Created.Count); // 暂停=轮询计时器不再新建（倒计时常驻，对齐 TS）

        vm.TogglePauseCommand.Execute(null);
        Assert.False(vm.Paused);
        Assert.Equal(5, cap.Created.Count); // 恢复 → 轮询 + 倒计时各新建
    }

    // ===== 协议切换（对齐 useEffect(activeProto) 重置基线与已选） =====

    [Source("[Source: src/pages/Sessions.tsx 切协议重置 selectedSessions 与气泡基线]")]
    [Fact]
    public async Task 协议切换_清已选与基线()
    {
        var feed = new StubSessionsDataFeed
        {
            Data =
            {
                [Protocol.Smb] = new SessionsData { Sessions = new[] { PSession("u1") } },
                [Protocol.Nfs] = new SessionsData { Sessions = new[] { PSession("n1", Protocol.Nfs) } },
            },
        };
        var vm = new SessionsViewModel(feed);
        await vm.RefreshAsync();
        vm.SetSelectedSessions(new[] { "u1" });
        Assert.Equal(1, vm.SelectedSessions.Count);

        vm.ActiveProto = Protocol.Nfs;
        Assert.Empty(vm.SelectedSessions); // 已选清空
        Assert.Single(vm.Sessions);
        Assert.Equal("n1", vm.Sessions[0].clientUserName);
    }

    // ===== 新增会话检测（对齐 detectNewSessions：冷启动不轰炸 + 60s 去重 + 气泡文案） =====

    [Source("[Source: src/pages/Sessions.tsx detectNewSessions + showBalloon]")]
    [Fact]
    public void 新会话检测_冷启动不轰炸_同用户60s去重()
    {
        var clock = 1_000_000L;
        var vm = new SessionsViewModel(new StubSessionsDataFeed(), nowMs: () => clock);
        List<(string, string)> balloons = new();
        vm.BalloonRequested += (title, body) => balloons.Add((title, body));

        vm.DetectNewSessions(new[] { PSession("alice"), PSession("bob") });
        Assert.Empty(balloons); // 首轮只建基线（冷启动不轰炸）

        vm.DetectNewSessions(new[] { PSession("alice"), PSession("carol") });
        Assert.Single(balloons);
        var (title, body) = balloons[0];
        Assert.Equal("新 SMB 会话", title);
        Assert.Equal("新增 1 个连接：carol", body);

        // 60s 内同用户不重复气泡
        clock += 30_000;
        vm.DetectNewSessions(new[] { PSession("alice"), PSession("carol") });
        Assert.Single(balloons); // 未新增
    }

    [Source("[Source: src/pages/Sessions.tsx 新会话气泡 title 随协议 + 超3加「等」]")]
    [Fact]
    public void 新会话检测_NFS标题与多条预览等号()
    {
        var clock = 0L;
        var vm = new SessionsViewModel(new StubSessionsDataFeed(), nowMs: () => clock);
        List<(string, string)> balloons = new();
        vm.BalloonRequested += (title, body) => balloons.Add((title, body));

        vm.ActiveProto = Protocol.Nfs;
        vm.DetectNewSessions(new[]
        {
            PSession("a", Protocol.Nfs), PSession("b", Protocol.Nfs),
            PSession("c", Protocol.Nfs), PSession("d", Protocol.Nfs),
        });
        Assert.Empty(balloons); // NFS 首轮基线
        clock += 61_000;
        vm.DetectNewSessions(new[]
        {
            PSession("a", Protocol.Nfs), PSession("b", Protocol.Nfs),
            PSession("c", Protocol.Nfs), PSession("d", Protocol.Nfs),
            PSession("e", Protocol.Nfs), PSession("f", Protocol.Nfs),
        });
        Assert.Single(balloons);
        Assert.Equal("新 NFS 会话", balloons[0].Item1);
        Assert.Equal("新增 2 个连接：e, f", balloons[0].Item2);
    }

    // ===== 强断 / 关闭（对齐 handleClose / doBatchClose 文案） =====

    [Source("[Source: src/pages/Sessions.tsx doBatchClose]")]
    [Fact]
    public async Task 批量强断_部分失败走error文案_全成走success_清选()
    {
        var feed = new StubSessionsDataFeed();
        feed.FailCloseIds.Add("bad");
        var vm = new SessionsViewModel(feed);
        List<(string, string)> notes = new();
        vm.Notify = (k, t) => notes.Add((k, t));
        vm.SetSelectedSessions(new[] { "ok1", "bad", "ok2" });

        var (ok, failed) = await vm.BatchCloseAsync(new[] { "ok1", "bad", "ok2" });
        Assert.Equal(2, ok);
        Assert.Equal(1, failed);
        Assert.Equal("2 个成功，1 个失败", notes.Single(n => n.Item1 == "error").Item2);
        Assert.Empty(vm.SelectedSessions);

        var vm2 = new SessionsViewModel(feed);
        List<(string, string)> notes2 = new();
        vm2.Notify = (k, t) => notes2.Add((k, t));
        vm2.SetSelectedSessions(new[] { "ok1", "ok2" });
        await vm2.BatchCloseAsync(new[] { "ok1", "ok2" });
        Assert.Equal("已断开 2 个会话", notes2.Single(n => n.Item1 == "success").Item2);
    }

    [Source("[Source: src/pages/Sessions.tsx handleClose / handleCloseFile]")]
    [Fact]
    public async Task 单条强断与关文件_成功文案()
    {
        var feed = new StubSessionsDataFeed();
        var vm = new SessionsViewModel(feed);
        List<(string, string)> notes = new();
        vm.Notify = (k, t) => notes.Add((k, t));

        Assert.True(await vm.CloseOneAsync("u1"));
        Assert.Equal("已断开", notes.Last().Item2);
        Assert.True(await vm.CloseFileAsync("7"));
        Assert.Equal("已关闭", notes.Last().Item2);
        Assert.Contains((Protocol.Smb, "u1"), feed.Closes);
    }

    // ===== 首轮失败提示（对齐 TS：有数据后轮询失败静默保留旧数据） =====

    [Source("[Source: src/pages/Sessions.tsx 首轮失败提示语义]")]
    [Fact]
    public async Task 首轮失败提示_后续静默()
    {
        var feed = new StubSessionsDataFeed();
        feed.ThrowProtos.Add(Protocol.Smb);
        var vm = new SessionsViewModel(feed);
        List<(string, string)> notes = new();
        vm.Notify = (k, t) => notes.Add((k, t));

        await vm.RefreshAsync();
        Assert.Equal("stub fail Smb", vm.ErrorText);
        Assert.Equal(1, notes.Count(n => n.Item1 == "error"));

        // 注入数据成功一轮 → 再失败静默
        feed.ThrowProtos.Clear();
        feed.Data[Protocol.Smb] = new SessionsData { Sessions = new[] { PSession("u") } };
        await vm.RefreshAsync();
        Assert.Single(vm.Sessions);
        feed.ThrowProtos.Add(Protocol.Smb);
        await vm.RefreshAsync();
        Assert.Single(vm.Sessions); // 保留旧数据
        Assert.Equal(1, notes.Count(n => n.Item1 == "error")); // 未新增错误提示
    }

    // ===== 刷新_now重置倒计时（对齐 refreshNow） =====

    [Source("[Source: src/pages/Sessions.tsx refreshNow]")]
    [Fact]
    public async Task 刷新_now重置倒计时到间隔值()
    {
        var vm = new SessionsViewModel(new StubSessionsDataFeed());
        vm.IntervalSec = 30; // IntervalSec 归一后倒计时重置为间隔值
        Assert.Equal(30, vm.Countdown);
        await vm.RefreshNowAsync();
        Assert.Equal(30, vm.Countdown); // refreshNow 重置倒计时到间隔值
    }
}
