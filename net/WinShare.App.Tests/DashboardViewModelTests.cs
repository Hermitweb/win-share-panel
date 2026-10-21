using System.Text;
using WinShare.App.Dashboard;
using WinShare.App.Events;
using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests;

/// <summary>DashboardViewModel 界面逻辑测试（不依赖 WPF 控件，xUnit 直测）。</summary>
public class DashboardViewModelTests : IDisposable
{
    readonly string _dir;
    readonly List<string> _savedFiles = new();
    readonly List<byte[]> _savedBytes = new();

    public DashboardViewModelTests()
    {
        // 用测试 bin 目录（可写），避免系统 Temp 的 ACL/UAC 拒绝
        _dir = Path.Combine(AppContext.BaseDirectory, "wsp-vm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    sealed class FakeFeed : IDashboardDataFeed
    {
        public DashboardBundle? Bundle;
        public int Calls;
        public Task<DashboardBundle> GetAsync()
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Bundle ?? new DashboardBundle());
        }
    }

    sealed class FakePng : IChartPngCapture
    {
        public byte[]? Bytes;
        public int Calls;
        public Task<byte[]?> CaptureAsync(string key)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Bytes);
        }
    }

    AppSettingsStore _sharedStore;

    DashboardViewModel MakeVm(DashboardBundle bundle, FakePng png, out FakeFeed feed, out IAppEventBus bus)
    {
        feed = new FakeFeed { Bundle = bundle };
        bus = new AppEventBus();
        _sharedStore ??= new AppSettingsStore(_dir); // 同目录共享实例，避免进程内缓存导致的陈旧读
        var store = _sharedStore;
        var vm = new DashboardViewModel(
            feed,
            store,
            bus,
            async (name, bytes) => { _savedFiles.Add(name); _savedBytes.Add(bytes); await Task.CompletedTask; },
            png,
            nowMs: () => 1_000_000_000_000);
        return vm;
    }

    static DashboardBundle FullBundle() => new()
    {
        Stats = new DashboardStats
        {
            shareCount = 3,
            activeSessions = 2,
            openFiles = 9,
            serviceStatus = ServiceState.Running,
            topShares = new List<TopShare> { new() { name = "Docs", connections = 5, protocol = Protocol.Smb } },
            byProtocol = new Dictionary<Protocol, ProtocolStats>
            {
                [Protocol.Smb] = new() { shares = 3, sessions = 2 },
                [Protocol.Nfs] = new() { shares = 0, sessions = 0 },
                [Protocol.Ftp] = new() { shares = 0, sessions = 0 },
                [Protocol.Webdav] = new() { shares = 0, sessions = 0 },
            },
        },
        Disks = new List<DiskUsage> { new() { drive = "E:", freeGB = 1, totalGB = 100, freePct = 1 } },
        SmbConfig = new SmbServerConfig { enableSMB1Protocol = true },
        Security = new SecurityReport
        {
            Checked = 3,
            issues = new List<SecurityIssue> { new() { user = "u", level = SecurityLevel.Fail, issue = "空口令" } },
        },
    };



    [Fact]
    public async Task Refresh_PopulatesStatsCards()
    {
        var vm = MakeVm(FullBundle(), new FakePng(), out var feed, out _);
        await vm.RefreshAsync();

        Assert.Equal(3, vm.ShareCount);
        Assert.Equal(2, vm.ActiveSessions);
        Assert.Equal(9, vm.OpenFiles);
        Assert.True(vm.ServiceRunning);
        Assert.Equal("运行中", vm.ServiceStatusText);
        Assert.Single(vm.TopShares);
        Assert.Equal(ProtocolPalette.Order.Length, vm.ProtocolRows.Count);
        Assert.Equal(1, feed.Calls);
        Assert.True(vm.Hydrated);
    }

    [Fact]
    public async Task Refresh_NoStats_ShowsError()
    {
        var vm = MakeVm(new DashboardBundle { }, new FakePng(), out _, out _);
        await vm.RefreshAsync();

        Assert.False(vm.ServiceRunning);
        Assert.Equal("未知", vm.ServiceStatusText);
        Assert.Contains("获取失败", vm.ErrorText);
    }

    [Fact]
    public async Task Refresh_AttentionFiresAlertRaisedEvent()
    {
        var png = new FakePng();
        var vm = MakeVm(FullBundle(), png, out _, out var bus);
        var alerts = new List<AlertRaised>();
        bus.Subscribe<AlertRaised>(alerts.Add);

        await vm.RefreshAsync();

        Assert.NotEmpty(vm.AttentionItems);
        // 命中了 smb1 / guest(弱口令族) / disk 等 → 事件面有发布
        Assert.Equal(vm.AttentionItems.Count, alerts.Count);
        Assert.Contains(alerts, a => a.kind == "smb1");
        Assert.Contains(alerts, a => a.kind.StartsWith("disk:"));
    }

    [Fact]
    public async Task ExportCsv_WritesBomAndRows()
    {
        var vm = MakeVm(FullBundle(), new FakePng(), out _, out _);
        await vm.RefreshAsync();

        vm.ExportCsvCommand.Execute(null);

        Assert.Single(_savedFiles);
        Assert.EndsWith(".csv", _savedFiles[0]);
        var text = Encoding.UTF8.GetString(_savedBytes[0]);
        Assert.StartsWith("\uFEFF", text);
        Assert.Contains("\"共享总数\",\"3\"", text);
        Assert.Contains("\"协议:SMB\",\"共享 3 / 会话 2\"", text);
        Assert.Contains("\"热门:SMB:Docs\",\"5\"", text);
        Assert.Equal("已导出 CSV", vm.ErrorText);
    }

    [Fact]
    public async Task ExportPng_NoData_ShowsWarning()
    {
        var png = new FakePng();
        var vm = MakeVm(new DashboardBundle { }, png, out _, out _);
        await vm.RefreshAsync();

        await vm.ExportPngCommand.ExecuteAsync();

        Assert.Equal("暂无数据", vm.ErrorText);
        Assert.Equal(0, png.Calls); // 无数据时根本不调快照器
    }

    [Fact]
    public async Task ExportPng_Rendered_ReturnsFile()
    {
        var png = new FakePng();
        var vm = MakeVm(FullBundle(), png, out _, out _);
        png.Bytes = new byte[] { 1, 2, 3 };
        await vm.RefreshAsync();

        await vm.ExportPngCommand.ExecuteAsync();

        Assert.Equal(1, png.Calls);
        Assert.Single(_savedFiles);
        Assert.EndsWith(".png", _savedFiles[0]);
        Assert.Equal("已导出 PNG", vm.ErrorText);
    }

    [Fact]
    public async Task ExportPng_NotRendered_ShowsChartNotRendered()
    {
        var png = new FakePng(); // Bytes=null 模拟未渲染
        var vm = MakeVm(FullBundle(), png, out _, out _);
        await vm.RefreshAsync();

        await vm.ExportPngCommand.ExecuteAsync();

        Assert.Equal("图表尚未渲染", vm.ErrorText);
        Assert.Empty(_savedFiles);
    }

    [Fact]
    public void TrendModes_AreValidated()
    {
        var vm = MakeVm(FullBundle(), new FakePng(), out _, out _);
        vm.TrendMode = "sessions";
        Assert.Equal("sessions", vm.TrendMode);
    }

    [Fact]
    public void RefreshInterval_SegmentsAndPersists()
    {
        var png = new FakePng();
        var vm = MakeVm(FullBundle(), png, out _, out _);

        var store = new AppSettingsStore(_dir);
        vm.RefreshIntervalSeconds = 30;
        Assert.Equal(30, store.GetRefreshInterval());
        Assert.Equal(30, vm.RefreshIntervalSeconds);

        vm.RefreshIntervalSeconds = 7; // 非法 → 收敛默认 10
        Assert.Equal(10, vm.RefreshIntervalSeconds);
    }

    [Fact]
    public void ReadHistory_FillsTrendPointsFromStore()
    {
        var vm = MakeVm(FullBundle(), new FakePng(), out _, out var bus);
        var now = 1_000_000_000_000L;
        for (int i = 0; i < 5; i++)
        {
            _sharedStore.PushHistory(new HistoryPoint { ts = now - i * 60_000, sessions = i, openFiles = i });
        }

        vm.ReadHistory();

        Assert.Equal(5, vm.TrendRecent.Count);
        Assert.False(vm.TrendAllEmpty);
        Assert.False(vm.TrendWindowEmpty);
    }

    [Fact]
    public void ReadHistory_AllEmptyFlag()
    {
        var vm = MakeVm(FullBundle(), new FakePng(), out _, out _);
        vm.ReadHistory();
        Assert.True(vm.TrendAllEmpty);
        Assert.True(vm.TrendRecent.Count == 0);
    }
}
