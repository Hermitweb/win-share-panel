using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using WinShare.App.Events;
using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Dashboard;

/// <summary>可复命命令（纯 .NET，不依赖 WPF 类型；界面侧直接绑定）。</summary>
public sealed class RelayCommand : ICommand
{
    readonly Action _sync;
    readonly Func<Task>? _async;

    public RelayCommand(Action action)
    {
        _sync = action ?? throw new ArgumentNullException(nameof(action));
    }

    public RelayCommand(Func<Task> action)
    {
        _async = action ?? throw new ArgumentNullException(nameof(action));
        _sync = () => { };
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        if (_async is not null)
        {
            _async().GetAwaiter().GetResult();
        }
        else
        {
            _sync();
        }
    }

    public async Task ExecuteAsync()
    {
        if (_async is not null) await _async();
        else _sync();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 仪表板 ViewModel（对齐 src/pages/Dashboard.tsx 全量界面逻辑）：
/// 统计卡 / 需要关注（告警规则评估 → 事件总线发布 AlertRaised）/ 24h 趋势 /
/// 协议分布 / 磁盘水位 / PNG+CSV 导出 / 自动刷新间隔（1|5|10|30s，持久化 appstate）。
/// 不引用任何 WPF 控件类型，xUnit 直测。
/// 命令：RefreshCommand / ExportPngCommand / ExportCsvCommand。
/// </summary>
public sealed class DashboardViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    readonly IDashboardDataFeed _feed;
    readonly AppSettingsStore _store;
    readonly IAppEventBus _bus;
    readonly Func<string, byte[], Task> _fileSaver;  // 视图层提供 SaveFileDialog；VM 保持 UI 无关
    readonly IChartPngCapture _pngCapture;             // 视图层 PrintVisual 实现；VM 保持 UI 无关
    readonly Func<long> _nowMs;
    readonly Func<int, System.Threading.Timer> _timerFactory; // 自动刷新定时器（测试可 stub 捕获）

    readonly object _timerGate = new();
    System.Threading.Timer? _timer;

    public DashboardViewModel(
        IDashboardDataFeed feed,
        AppSettingsStore store,
        IAppEventBus bus,
        Func<string, byte[], Task> fileSaver,
        IChartPngCapture pngCapture,
        Func<long>? nowMs = null,
        Func<int, System.Threading.Timer>? timerFactory = null)
    {
        _feed = feed;
        _store = store;
        _bus = bus;
        _fileSaver = fileSaver ?? throw new ArgumentNullException(nameof(fileSaver));
        _pngCapture = pngCapture ?? throw new ArgumentNullException(nameof(pngCapture));
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _timerFactory = timerFactory ?? (sec => new System.Threading.Timer(
            _ => { try { _ = RefreshAsync(); } catch { /* 后台轮询单轮失败不炸线程池 */ } },
            null, sec * 1000, sec * 1000));

        RefreshCommand = new RelayCommand(() => RefreshAsync());
        ExportPngCommand = new RelayCommand(() => ExportPngAsync());
        ExportCsvCommand = new RelayCommand(() => ExportCsv());
        _refreshInterval = NormalizeInterval(store.GetRefreshInterval());
    }

    // ===== 命令 =====
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportPngCommand { get; }
    public RelayCommand ExportCsvCommand { get; }

    static int NormalizeInterval(int? v) => v is 1 or 5 or 10 or 30 ? v!.Value : 10;

    // ===== 统计卡（Stats 缺失时保持 0 + 未知；对齐 TS stats ?? 0） =====
    int _shareCount, _activeSessions, _openFiles;
    string _serviceStatusText = "未知";
    bool _serviceRunning;

    public int ShareCount { get => _shareCount; private set => Set(ref _shareCount, value); }
    public int ActiveSessions { get => _activeSessions; private set => Set(ref _activeSessions, value); }
    public int OpenFiles { get => _openFiles; private set => Set(ref _openFiles, value); }
    public string ServiceStatusText { get => _serviceStatusText; private set => Set(ref _serviceStatusText, value); }
    public bool ServiceRunning { get => _serviceRunning; private set => Set(ref _serviceRunning, value); }

    // ===== 热门共享 / 协议分布 =====
    DashboardStats? _stats;
    public List<TopShare> TopShares { get; private set; } = new();
    public List<ProtocolRow> ProtocolRows { get; private set; } = new();

    // ===== 需要关注 =====
    public List<AlertEvaluator.AttentionItem> AttentionItems { get; private set; } = new();
    public string SkippedSourcesText { get; private set; } = "";

    // ===== 24h 趋势 =====
    string _trendMode = "both";
    public string TrendMode { get => _trendMode; set => Set(ref _trendMode, value); }
    public List<HistoryPoint> TrendRecent { get; private set; } = new();
    public bool TrendAllEmpty { get; private set; }
    public bool TrendWindowEmpty { get; private set; }

    // ===== 磁盘水位 =====
    public List<DiskRow> DiskRows { get; private set; } = new();

    // ===== 状态 =====
    bool _loading, _hydrated;
    string _errorText = "";
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }
    public bool Hydrated { get => _hydrated; private set => Set(ref _hydrated, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }

    // ===== 自动刷新间隔（1|5|10|30s；持久化到 appstate 扩展字段 refreshInterval） =====
    int _refreshInterval;
    public int RefreshIntervalSeconds
    {
        get => _refreshInterval;
        set
        {
            var next = NormalizeInterval(value);
            if (next == _refreshInterval) return;
            _refreshInterval = next;
            _store.SaveRefreshInterval(next);
            Set(ref _refreshInterval, next);
            RestartAutoRefresh();
        }
    }

    public void StartAutoRefresh() => RestartAutoRefresh();

    void RestartAutoRefresh()
    {
        lock (_timerGate)
        {
            _timer?.Dispose();
            _timer = _timerFactory(_refreshInterval);
        }
    }

    public void StopAutoRefresh()
    {
        lock (_timerGate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    // ===== 刷新（对齐 TS 4 源 + hydrate + skippedSources + 告警评估） =====
    public async Task RefreshAsync()
    {
        Loading = true;
        var bundle = await _feed.GetAsync();
        Loading = false;
        Hydrated = true;

        var stats = bundle.Stats;
        if (stats is not null)
        {
            _stats = stats;
            ShareCount = stats.shareCount;
            ActiveSessions = stats.activeSessions;
            OpenFiles = stats.openFiles;
            ServiceStatusText = stats.serviceStatus == ServiceState.Running ? "运行中"
                : stats.serviceStatus == ServiceState.Stopped ? "已停止" : "未知";
            ServiceRunning = stats.serviceStatus == ServiceState.Running;
            TopShares = (stats.topShares ?? new List<TopShare>()).ToList();
            ProtocolRows = ProtocolPalette.Order
                .Select(p => new ProtocolRow(p, stats.byProtocol.TryGetValue(p, out var s) ? s : default!))
                .ToList();
            ErrorText = "";
        }
        else
        {
            _stats = null;
            // 首轮失败提示（对齐 TS：error && !data → message.error）
            ErrorText = "仪表板统计获取失败（共享服务未启动？）";
            TopShares = new List<TopShare>();
            ProtocolRows = ProtocolPalette.Order.Select(p => new ProtocolRow(p, default!)).ToList();
        }

        // 需要关注：告警规则评估 → 命中项经事件总线发布（alert 触发事件面）
        var settings = _store.Load();
        AttentionItems = AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput
        {
            alertRules = settings.alertRules,
            smbConfig = bundle.SmbConfig,
            security = bundle.Security,
            disks = bundle.Disks ?? new List<DiskUsage>(),
        });
        foreach (var item in AttentionItems)
        {
            _bus.Publish(new AlertRaised { kind = item.key, level = item.level, text = item.text });
        }

        // skippedSources（对齐 TS：SMB 配置探测 / 账户安全体检 / 磁盘水位读取，逐源如实）
        var skipped = new List<string>();
        if (bundle.SmbConfig is null) skipped.Add("SMB 配置探测");
        if (bundle.Security is null) skipped.Add("账户安全体检");
        if (bundle.Disks is null) skipped.Add("磁盘水位读取");
        SkippedSourcesText = string.Join("、", skipped);

        // 磁盘水位（按等级排序 danger<warn<ok<unknown，同级按盘符；对齐 TS diskRows）
        var lowGb = settings.alertRules?.diskLowGb ?? 20;
        DiskRows = (bundle.Disks ?? new List<DiskUsage>())
            .Select(d => new DiskRow(d.drive, d.freeGB, d.totalGB, d.freePct,
                AlertEvaluator.DiskLevel(d, 10, lowGb)))
            .OrderBy(r => r.Level switch { "danger" => 0, "warn" => 1, "ok" => 2, _ => 3 })
            .ThenBy(r => r.Drive, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 趋势：重新水合 appstate（对齐 TS：进入仪表板 hydrate() 让新采样点可见）
        ReadHistory();

        RaiseAll();
    }

    /// <summary>从 appstate 重读 24h 窗口采样点（主进程采样器新写入后调用）。</summary>
    public void ReadHistory()
    {
        var history = _store.Load().dashboardHistory?.ToList() ?? new List<HistoryPoint>();
        TrendRecent = TrendSeries.RecentPoints(history, _nowMs());
        TrendAllEmpty = history.Count == 0;
        TrendWindowEmpty = history.Count > 0 && TrendRecent.Count == 0;
        RaiseAll();
    }

    // ===== 导出（对齐 TS exportPng/exportCsv） =====

    /// <summary>PNG：TS 语义 = 热门共享图 getDataURL(pixelRatio:2)；WPF 侧 = PrintVisual + PngBitmapEncoder。</summary>
    public async Task ExportPngAsync()
    {
        if (_stats is null)
        {
            ErrorText = "暂无数据";
            return;
        }
        var bytes = await _pngCapture.CaptureAsync("topshares");
        if (bytes is null)
        {
            ErrorText = "图表尚未渲染";
            return;
        }
        await _fileSaver($"dashboard-{_nowMs()}.png", bytes);
        ErrorText = "已导出 PNG";
    }

    /// <summary>CSV：逐字段对齐 TS exportCsv（BOM + 指标/值两列 + 全格引号）。</summary>
    public void ExportCsv()
    {
        if (_stats is null)
        {
            ErrorText = "暂无数据";
            return;
        }
        var csv = CsvExporter.BuildCsv(_stats);
        _fileSaver($"dashboard-{_nowMs()}.csv", Encoding.UTF8.GetBytes(csv)).GetAwaiter().GetResult();
        ErrorText = "已导出 CSV";
    }

    // ===== INPC =====
    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(ShareCount), nameof(ActiveSessions), nameof(OpenFiles),
            nameof(ServiceStatusText), nameof(ServiceRunning),
            nameof(TopShares), nameof(ProtocolRows),
            nameof(AttentionItems), nameof(SkippedSourcesText),
            nameof(TrendRecent), nameof(TrendAllEmpty), nameof(TrendWindowEmpty),
            nameof(TrendMode), nameof(DiskRows), nameof(Loading), nameof(Hydrated),
            nameof(ErrorText), nameof(RefreshIntervalSeconds),
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>协议分布行（VM 纯数据）。</summary>
public sealed record ProtocolRow(Protocol Protocol, ProtocolStats? Stats)
{
    public int Shares => Stats?.shares ?? 0;
    public int Sessions => Stats?.sessions ?? 0;
}

/// <summary>磁盘水位行（VM 纯数据；Level 取值 danger|warn|ok|unknown，对齐 TS DISK_LEVEL_META）。</summary>
public sealed record DiskRow(string Drive, double FreeGB, double TotalGB, double FreePct, string Level)
{
    public double UsedPct => Math.Clamp(100 - FreePct, 0, 100);
}

/// <summary>图表 PNG 快照抽象（视图层 RenderTargetBitmap + PngBitmapEncoder 实现，对齐 TS getDataURL）。</summary>
public interface IChartPngCapture
{
    /// <summary>按图 key（"topshares"）渲染快照；未渲染返回 null（对齐 TS "图表尚未渲染"）。</summary>
    Task<byte[]?> CaptureAsync(string chartKey);
}
