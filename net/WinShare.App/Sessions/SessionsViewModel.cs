using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinShare.Core.Contracts;

namespace WinShare.App.Sessions;

/// <summary>
/// 会话监控数据束（对齐 Sessions.tsx SessionsData { sessions, files }）：
/// 会话=统一 ProtocolSession；文件=SmbOpenFile（仅 SMB 有打开文件表）。
/// </summary>
public sealed record SessionsData
{
    public IReadOnlyList<ProtocolSession> Sessions { get; init; } = Array.Empty<ProtocolSession>();
    public IReadOnlyList<SmbOpenFile> Files { get; init; } = Array.Empty<SmbOpenFile>();
}

/// <summary>
/// 会话数据馈送（可测试接缝；缺席源走空馈送降级——对齐 T3a 空馈送模式：
/// FTP/WebDAV 无原生会话 API，NFS 经协议适配器槽位，缺席=空列表不崩）。
/// </summary>
public interface ISessionsDataFeed
{
    Task<SessionsData> GetAsync(Protocol proto);
    Task CloseSessionAsync(Protocol proto, string sessionId);
    Task CloseFileAsync(string fileId);
}

/// <summary>
/// 契约服务馈送：
/// - smb：ISessionService ListSessionsAsync + ListOpenFilesAsync（TS Promise.all 并行）
/// - nfs：IProtocolAdapter(Nfs) ListSessionsAsync（TS api.adapter.sessions('nfs')；缺席=空）
/// - ftp/webdav：无原生会话 API → 空（对齐 TS queryFn 的 ftp/webdav 分支）
/// </summary>
public sealed class ContractSessionsDataFeed : ISessionsDataFeed
{
    readonly Func<ISessionService?> _smb;
    readonly Func<IReadOnlyDictionary<Protocol, IProtocolAdapter>?> _adapters;

    public ContractSessionsDataFeed(
        Func<ISessionService?> smb,
        Func<IReadOnlyDictionary<Protocol, IProtocolAdapter>?>? adapters = null)
    {
        _smb = smb ?? throw new ArgumentNullException(nameof(smb));
        _adapters = adapters;
    }

    public async Task<SessionsData> GetAsync(Protocol proto)
    {
        switch (proto)
        {
            case Protocol.Smb:
                var svc = _smb();
                if (svc is null) return new SessionsData();
                try
                {
                    // 并行启动（对齐 TS Promise.all）
                    var sTask = svc.ListSessionsAsync();
                    var fTask = svc.ListOpenFilesAsync();
                    await Task.WhenAll(sTask, fTask);
                    var sessions = await sTask;
                    var files = await fTask;
                    return new SessionsData
                    {
                        Sessions = sessions.Select(SmbToSession).ToList(),
                        Files = files,
                    };
                }
                catch { return new SessionsData(); } // 取数失败 → 空馈送（页面保留旧数据语义由 VM 承载）

            case Protocol.Nfs:
                var adapter = _adapters()?.TryGetValue(Protocol.Nfs, out var nfs) == true ? nfs : null;
                if (adapter is null) return new SessionsData();
                try
                {
                    var sessions = await adapter.ListSessionsAsync();
                    return new SessionsData { Sessions = sessions };
                }
                catch { return new SessionsData(); }

            default:
                // FTP/WebDAV 无原生会话 API（对齐 TS：返回 { sessions: [], files: [] }）
                return new SessionsData();
        }
    }

    public Task CloseSessionAsync(Protocol proto, string sessionId)
    {
        if (proto == Protocol.Smb)
        {
            var svc = _smb()
                ?? throw new InvalidOperationException("会话服务未注册（组合根未接线）");
            return svc.CloseSessionAsync(sessionId);
        }
        if (proto == Protocol.Nfs)
        {
            IProtocolAdapter? adapter = null;
            if (_adapters() is { } ad)
            {
                ad.TryGetValue(Protocol.Nfs, out adapter);
            }
            if (adapter is null)
                throw new InvalidOperationException("NFS 适配器未注册（组合根未接线）");
            return adapter.CloseSessionAsync(sessionId);
        }
        throw new NotSupportedException($"协议 {proto} 无原生会话 API");
    }

    public Task CloseFileAsync(string fileId)
    {
        var svc = _smb()
            ?? throw new InvalidOperationException("会话服务未注册（组合根未接线）");
        return svc.CloseFileAsync(fileId);
    }

    /// <summary>
    /// SmbSession → 统一 ProtocolSession（[Source: src/pages/Sessions.tsx smbToSession]：
    /// sessionId 复用 clientUserName，供断开调用）。
    /// </summary>
    public static ProtocolSession SmbToSession(SmbSession s) => new()
    {
        protocol = Protocol.Smb,
        sessionId = s.clientUserName,
        clientUserName = s.clientUserName,
        clientComputerName = s.clientComputerName,
        sessionStartTime = s.sessionStartTime,
        clientOpenFiles = s.clientOpenFiles,
        clientIdleTime = s.clientIdleTime,
        bytesReceived = s.bytesReceived,
        bytesSent = s.bytesSent,
    };
}

/// <summary>
/// 会话监控页 ViewModel（对齐 src/pages/Sessions.tsx 全量界面逻辑）：
/// 协议 Tab（SMB/NFS/FTP/WebDAV）/ 可配置刷新间隔（1|5|10|30s）/ 暂停恢复 / 倒计时 /
/// 选中强断（单条确认 + 批量确认）/ 打开文件展示与关闭 / 新增会话检测（同用户 60s 去重 + 托盘气泡）。
/// 不引用 WPF 控件类型（只 INotifyPropertyChanged + ICommand），xUnit 直测。
/// </summary>
public sealed class SessionsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    readonly ISessionsDataFeed _feed;
    readonly Func<long> _nowMs;
    readonly Func<int, System.Threading.Timer> _pollTimerFactory;
    readonly Func<int, System.Threading.Timer> _countTimerFactory;

    readonly object _gate = new();
    System.Threading.Timer? _pollTimer;
    System.Threading.Timer? _countTimer;

    public SessionsViewModel(
        ISessionsDataFeed feed,
        Func<long>? nowMs = null,
        Func<int, System.Threading.Timer>? pollTimerFactory = null,
        Func<int, System.Threading.Timer>? countTimerFactory = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // 轮询 Timer：首拍=interval 后、周期=interval（对齐 TS refetchInterval；测试可注入 stub）
        _pollTimerFactory = pollTimerFactory ?? (sec => new System.Threading.Timer(
            _ => { try { OnPollTick(); } catch { /* 后台轮询单拍失败不炸线程池 */ } },
            null, sec * 1000, sec * 1000));
        // 倒计时 1s（TS 解耦展示面；倒计时常驻，暂停仅隐藏标签）
        _countTimerFactory = countTimerFactory ?? (_ => new System.Threading.Timer(
            _ => { try { TickCountdown(); } catch { } },
            null, 1000, 1000));

        RefreshNowCommand = new RelayCommand(() => RefreshNowAsync());
        TogglePauseCommand = new RelayCommand(() => Paused = !Paused);
    }

    // ===== 命令 =====
    /// <summary>立即刷新（对齐 refreshNow：倒计时重置为间隔值 + refetch）。</summary>
    public RelayCommand RefreshNowCommand { get; }
    /// <summary>暂停/恢复轮询（对齐 paused 开关；倒计时照走，仅隐藏倒计时标签）。</summary>
    public RelayCommand TogglePauseCommand { get; }

    /// <summary>轻通知面（kind: success|error + 文案），对齐 TS message.*。</summary>
    public Action<string, string>? Notify { get; set; }

    /// <summary>新增会话气泡（title/body；视图接托盘 AppShell.ShowBalloon，对齐 api.window.showBalloon）。</summary>
    public event Action<string, string>? BalloonRequested;

    void Tell(string kind, string text) => Notify?.Invoke(kind, text);

    // ===== 状态 =====
    Protocol _activeProto = Protocol.Smb;
    int _intervalSec = 5;      // TS useState(5)；选项 1/5/10/30
    bool _paused;
    int _countdown = 5;
    List<string> _selected = new();
    IReadOnlyList<ProtocolSession> _sessions = Array.Empty<ProtocolSession>();
    IReadOnlyList<SmbOpenFile> _files = Array.Empty<SmbOpenFile>();
    bool _hasData;
    string _errorText = "";
    bool _spinning;

    // 新增会话检测基线（TS prevUsersRef / lastBalloonRef）
    HashSet<string>? _prevUsers;
    Dictionary<string, long> _lastBalloon = new();

    /// <summary>当前协议 Tab（对齐 activeProto）。</summary>
    public Protocol ActiveProto
    {
        get => _activeProto;
        set
        {
            if (_activeProto == value) return;
            _activeProto = value;
            Set(ref _activeProto, value);
            // 切协议=重置已选与气泡基线（对齐 TS useEffect(activeProto)）
            _selected.Clear();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSessions)));
            _prevUsers = null;
            RestartPolling();
            _ = RefreshAsync(); // 新 queryKey 语义：立即取数
        }
    }

    /// <summary>刷新间隔（秒；1|5|10|30，其余收敛 5——对齐 TS 固定选项 + 仪表板归一口径）。</summary>
    public int IntervalSec
    {
        get => _intervalSec;
        set
        {
            var v = value is 1 or 5 or 10 or 30 ? value : 5;
            if (v == _intervalSec) return;
            _intervalSec = v;
            Set(ref _intervalSec, v);
            _countdown = v;
            Set(ref _countdown, _countdown);
            RestartPolling();
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            Set(ref _paused, value);
            RestartPolling(); // 暂停=interval false（TS refetchInterval 语义）
        }
    }

    /// <summary>倒计时（1s 归零回卷；TS 解耦展示面）。</summary>
    public int Countdown { get => _countdown; private set => Set(ref _countdown, value); }
    /// <summary>已选会话 id（TS selectedSessions）。</summary>
    public IReadOnlyList<string> SelectedSessions => _selected;
    public void SetSelectedSessions(IReadOnlyList<string> ids)
    {
        _selected = ids.ToList();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSessions)));
    }

    public IReadOnlyList<ProtocolSession> Sessions { get => _sessions; private set => Set(ref _sessions, value); }
    public IReadOnlyList<SmbOpenFile> Files { get => _files; private set => Set(ref _files, value); }
    /// <summary>首轮加载失败提示（有数据后轮询失败静默，TS 同款语义）。</summary>
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    /// <summary>首个请求加载态（对齐 spinning = isFetching && !data）。</summary>
    public bool Spinning { get => _spinning; private set => Set(ref _spinning, value); }

    /// <summary>会话行（开始时间预格式化 YYYY-MM-DD HH:mm:ss，对齐 fmtTime）。</summary>
    public sealed record SessionRow(ProtocolSession session, string StartText);
    public List<SessionRow> SessionRows =>
        _sessions.Select(s => new SessionRow(s, FmtTime(s.sessionStartTime))).ToList();

    /// <summary>时间格式化（对齐 TS dayjs(v).format('YYYY-MM-DD HH:mm:ss')；空→'-'，无效→原值）。</summary>
    public static string FmtTime(string? ts)
    {
        if (string.IsNullOrEmpty(ts)) return "-";
        return DateTime.TryParse(ts, out var d) ? d.ToString("yyyy-MM-dd HH:mm:ss") : ts;
    }

    // =====================================================================
    // 轮询 / 倒计时（对齐 TS refetchInterval + 1s 倒计时解耦面）
    // =====================================================================

    void RestartPolling()
    {
        lock (_gate)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            if (!_paused)
                _pollTimer = _pollTimerFactory(_intervalSec); // 首拍=interval 后（Timer 无 immediate 重载位）
            _countTimer?.Dispose();
            _countTimer = _countTimerFactory(1); // 倒计时常驻（暂停仅隐藏标签，对齐 TS）
        }
    }

    /// <summary>轮询单拍（测试可直接驱动；= TS refetchInterval 触发 refetch）。</summary>
    public void OnPollTick()
    {
        _ = RefreshAsync();
    }

    /// <summary>倒计时一拍（1s 归零回卷到间隔值；测试可直接驱动）。</summary>
    public void TickCountdown()
    {
        _countdown = _countdown <= 1 ? _intervalSec : _countdown - 1;
        Set(ref _countdown, _countdown);
    }

    /// <summary>立即刷新（对齐 refreshNow：倒计时重置 + refetch + 失败提示）。</summary>
    public async Task RefreshNowAsync()
    {
        _countdown = _intervalSec;
        Set(ref _countdown, _countdown);
        await RefreshAsync();
    }

    /// <summary>取数（TS queryFn）：ftp/webdav 空态；失败保留旧数据（首轮才提示，后续静默）。</summary>
    public async Task RefreshAsync()
    {
        SessionsData data;
        try
        {
            if (_activeProto is Protocol.Ftp or Protocol.Webdav)
            {
                data = new SessionsData(); // 无原生会话 API（对齐 TS 分支）
                _hasData = true;
            }
            else
            {
                data = await _feed.GetAsync(_activeProto);
                _hasData = true; // 取数成功（哪怕空列表）→ 后续失败静默
            }
        }
        catch (Exception e)
        {
            // 首轮失败提示（TS: error && !data → message.error）；后续轮询失败静默保留旧数据
            if (!_hasData)
            {
                _errorText = e.Message;
                Set(ref _errorText, e.Message);
                Tell("error", e.Message);
            }
            Spinning = false;
            return;
        }

        _sessions = data.Sessions;
        _files = data.Files;
        Sessions = _sessions;
        Files = _files;
        Spinning = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SessionRows)));

        // 新增会话检测（仅 smb/nfs 取数路径；ftp/webdav 无数据可比）
        if (_activeProto is Protocol.Smb or Protocol.Nfs)
            DetectNewSessions(_sessions);
    }

    // =====================================================================
    // 新增会话检测（[Source: src/pages/Sessions.tsx detectNewSessions]）
    // =====================================================================

    /// <summary>
    /// 检测新增用户并触发气泡：仅非首次加载（冷启动不轰炸）；同用户 60s 去重；
    /// body="新增 N 个连接：前3 等"（>3 加「等」）；title=新 SMB/NFS 会话。
    /// 纯逻辑（nowMs 可注入），xUnit 直测。
    /// </summary>
    public void DetectNewSessions(IReadOnlyList<ProtocolSession> next)
    {
        var nextUsers = next.Where(s => !string.IsNullOrEmpty(s.clientUserName))
            .Select(s => s.clientUserName).ToHashSet();
        if (_prevUsers is null)
        {
            _prevUsers = nextUsers; // 首轮只建基线
            return;
        }
        var added = new List<string>();
        var now = _nowMs();
        foreach (var u in nextUsers)
        {
            if (_prevUsers.Contains(u)) continue;
            // 同用户 60s 内不重复气泡
            var last = _lastBalloon.TryGetValue(u, out var t) ? t : 0;
            if (now - last > 60_000)
            {
                added.Add(u);
                _lastBalloon[u] = now;
            }
        }
        _prevUsers = nextUsers;
        if (added.Count == 0) return;
        var preview = string.Join(", ", added.Take(3));
        var body = $"新增 {added.Count} 个连接：{preview}{(added.Count > 3 ? " 等" : "")}";
        var title = _activeProto == Protocol.Smb ? "新 SMB 会话" : "新 NFS 会话";
        BalloonRequested?.Invoke(title, body);
    }

    // =====================================================================
    // 断开 / 关闭文件（对齐 closeOne / handleClose / handleCloseFile / doBatchClose）
    // =====================================================================

    /// <summary>单条强断（按协议路由；成功 "已断开"）。确认框在视图层。</summary>
    public async Task<bool> CloseOneAsync(string sessionId)
    {
        try
        {
            await _feed.CloseSessionAsync(_activeProto, sessionId);
            Tell("success", "已断开");
            await RefreshAsync();
            return true;
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return false;
        }
    }

    /// <summary>关闭打开文件（成功 "已关闭"）。</summary>
    public async Task<bool> CloseFileAsync(string fileId)
    {
        try
        {
            await _feed.CloseFileAsync(fileId);
            Tell("success", "已关闭");
            await RefreshAsync();
            return true;
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return false;
        }
    }

    /// <summary>
    /// 批量强断（对齐 doBatchClose：allSettled 语义 → 逐个 try/catch 收集失败；
    /// 有失败 message.error "N 个成功，M 个失败"，全成 message.success "已断开 N 个会话"；清选 + 刷新）。
    /// 确认框（"将对 N 个会话执行强制断开"）在视图层。
    /// </summary>
    public async Task<(int ok, int failed)> BatchCloseAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return (0, 0);
        var failed = new List<string>();
        foreach (var id in ids)
        {
            try { await _feed.CloseSessionAsync(_activeProto, id); }
            catch { failed.Add(id); }
        }
        var ok = ids.Count - failed.Count;
        if (failed.Count > 0) Tell("error", $"{ok} 个成功，{failed.Count} 个失败");
        else Tell("success", $"已断开 {ids.Count} 个会话");
        _selected.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSessions)));
        await RefreshAsync();
        return (ok, failed.Count);
    }

    // =====================================================================
    // 生命周期（视图 Loaded/Unloaded 驱动）
    // =====================================================================

    /// <summary>启动轮询 + 首轮取数（视图 Loaded 调）。</summary>
    public Task StartAsync()
    {
        RestartPolling();
        return RefreshAsync();
    }

    /// <summary>停表（视图 Unloaded 调；防后台定时器泄漏）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            _countTimer?.Dispose();
            _countTimer = null;
        }
    }

    // ===== INotifyPropertyChanged =====

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
