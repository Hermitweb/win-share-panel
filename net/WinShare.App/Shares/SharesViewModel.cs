using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using WinShare.App.Dashboard;
using WinShare.App.Events;
using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Shares;

/// <summary>
/// 共享列表行（对齐 Shares.tsx 表格列 + 行内操作面；纯数据 + INPC，视图只做映射）：
/// 空单元格统一落 "-"（对齐 antd 列 render 的 text-fog 占位），协议专有列按 Tab 显隐。
/// </summary>
public sealed class ShareRowVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ShareRowVm(Share share, Dictionary<string, DiskUsage> diskIndex, int diskLowGb,
        ProtocolCapabilities? caps, bool showProtocolColumn)
    {
        Share = share;
        ShowProtocolColumn = showProtocolColumn;
        var level = ShareNameUtils.WaterLevelOf(share.path, diskIndex, diskLowGb);
        if (level is { } w)
        {
            WaterLevel = w.level;
            WaterTagText = $"剩 {w.usage.freeGB} GB";
            WaterTip = $"{w.usage.drive} 剩余 {w.usage.freeGB} GB / 共 {w.usage.totalGB} GB（告警线 {diskLowGb} GB）";
        }
        switch (share.protocol)
        {
            case Protocol.Ftp:
                FtpPortText = share.port is int p ? p.ToString() : "-";
                FtpSslText = share.sslPolicy is { } ssl ? ssl.ToString() : "-";
                FtpAuthText = share.authMode is { } am ? am.ToString() : "-";
                break;
            case Protocol.Webdav:
                WebdavPortText = share.port is int wp ? wp.ToString() : "-";
                WebdavAnonText = share.anonymousEnabled is true ? "是" : "-";
                WebdavAuthoringText = share.authoringEnabled is true ? "启用" : "-";
                break;
            case Protocol.Nfs:
                NfsAuthText = share.authentication is { Count: > 0 } a ? string.Join(" ", a) : "-";
                NfsPermText = share.nfsPermission is { } np ? np.ToString() : "-";
                break;
            case Protocol.Smb:
                EncryptedText = share.encrypted ? "是" : "-";
                break;
        }
        CanToggle = caps?.supportsToggle ?? false;
        SupportsPermissions = caps?.supportsPermissions ?? false;
    }

    public Share Share { get; }
    /// <summary>复合 key（TS toKey：${protocol}:${name}，协议小写）。</summary>
    public string Key => $"{Share.protocol.ToString().ToLowerInvariant()}:{Share.name}";

    public string Name => Share.name;
    public string PathText => Share.path;
    public string DescriptionText => string.IsNullOrEmpty(Share.description) ? "-" : Share.description;
    public string ProtocolTagText => Share.protocol.ToString().ToUpperInvariant();
    public string StatusText => Share.status == ShareStatus.Enabled ? "启用" : "禁用";
    /// <summary>启停按钮动词（TS：已启用→「禁用」，已禁用→「启用」）。</summary>
    public string ToggleVerb => Share.status == ShareStatus.Enabled ? "禁用" : "启用";
    public int ConcurrentUsers => Share.concurrentUsers;

    // 协议专有列（非当前协议时为空 → 列本身隐藏）
    public string FtpPortText { get; } = "";
    public string FtpSslText { get; } = "";
    public string FtpAuthText { get; } = "";
    public string NfsAuthText { get; } = "";
    public string NfsPermText { get; } = "";
    public string WebdavPortText { get; } = "";
    public string WebdavAnonText { get; } = "";
    public string WebdavAuthoringText { get; } = "";
    public string EncryptedText { get; } = "";

    // 磁盘水位（warn/danger 才打标；TS waterLevelOf）
    public string WaterTagText { get; } = "";
    public string WaterTip { get; } = "";
    public string WaterLevel { get; } = "";
    public bool ShowProtocolColumn { get; }

    // 能力位（TS caps.supportsToggle / supportsPermissions）
    public bool CanToggle { get; }
    public bool SupportsPermissions { get; }

    // 置顶（TS pinnedSet；行内星标 + 置顶恒排最前）
    bool _pinned;
    public bool Pinned
    {
        get => _pinned;
        set { if (_pinned == value) return; _pinned = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pinned))); }
    }
}

/// <summary>协议未安装引导项（TS ProtocolCapabilityBanner renderSingle/renderStatusBadges）。</summary>
public sealed record ProtocolBannerItem
{
    public Protocol Protocol { get; init; }
    public string Hint { get; init; } = "";
    /// <summary>细分状态徽标（IIS 已装/角色未装/IIS 未装/服务已停；kind=green|orange|red|volcano）。</summary>
    public IReadOnlyList<(string text, string kind)> Badges { get; init; } = Array.Empty<(string, string)>();
    public bool HasInstallCommand { get; init; }
}

/// <summary>
/// 共享管理页主 ViewModel（对齐 src/pages/Shares.tsx 全量界面逻辑；不依赖 WPF 控件，xUnit 直测）：
/// - 列表/搜索/「只看有人连接」/置顶排序（TS filtered memo 逐条）
/// - 批量选择逻辑：Del 批量删除 / Space 批量启停（TS confirmBatchDelete/shareToggleTick）
/// - 新建向导表单（场景预设 + 新手模式高级项收敛 + 路径带名）
/// - 详情/权限抽屉子 VM 工厂
/// - 配置导入导出（护栏 + 文案逐字对齐）
/// - 协议能力横幅（未安装引导 + 安装）
/// 服务缺席走占位降级：如实报"未接入"，不崩不臆造。
/// </summary>
public sealed class SharesViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    readonly ISharesDataFeed _feed;
    readonly AppSettingsStore _store;
    readonly IAppEventBus _bus;
    readonly IUnsubscribe? _refreshSub;
    readonly IShareService? _share;
    readonly IPresetService? _preset;
    readonly IProtocolService? _protocol;
    readonly ISystemService? _system;
    readonly IUserService? _user;
    readonly IDiskService? _disk;
    readonly Func<string, byte[], Task> _fileSaver;
    readonly Func<Task<string?>> _importPicker;
    readonly Func<long> _nowMs;

    public SharesViewModel(
        ISharesDataFeed feed,
        AppSettingsStore store,
        IAppEventBus bus,
        IShareService? shareService,
        IPresetService? presetService,
        IProtocolService? protocolService,
        ISystemService? systemService,
        IUserService? userService,
        IDiskService? diskService,
        Func<string, byte[], Task> fileSaver,
        Func<Task<string?>> importPicker,
        Func<long>? nowMs = null)
    {
        _feed = feed;
        _store = store;
        _bus = bus;
        _share = shareService;
        _preset = presetService;
        _protocol = protocolService;
        _system = systemService;
        _user = userService;
        _disk = diskService;
        _fileSaver = fileSaver;
        _importPicker = importPicker;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        RefreshCommand = new RelayCommand(async () => await LoadAsync());
        ExportCommand = new RelayCommand(async () => await ExportConfigAsync());
        ImportCommand = new RelayCommand(async () => await ImportConfigAsync());
        OpenCreateCommand = new RelayCommand(() => OpenCreateWizard());
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        BatchToggleCommand = new RelayCommand(async () => await ExecuteBatchToggleAsync());
        BatchDeleteCommand = new RelayCommand(async () => await ExecuteBatchDeleteAsync());
        InstallCommand = new RelayCommand<Protocol>(async p => await InstallProtocolAsync(p));
        DetailCommand = new RelayCommand<ShareRowVm>(row => { DetailTarget = row; DetailOpened?.Invoke(); });
        ToggleCommand = new RelayCommand<ShareRowVm>(async row =>
        {
            if (!row.CanToggle) return; // 能力位门控（TS canToggle(protocol)）
            StatusMsg = await ToggleShareAsync(row);
        });
        EditCommand = new RelayCommand<ShareRowVm>(row => { EditForm = MakeEditForm(row.Share); EditOpened?.Invoke(); });
        PermCommand = new RelayCommand<ShareRowVm>(row =>
        {
            if (!row.SupportsPermissions) return; // 能力位门控（TS caps.supportsPermissions）
            PermShare = row.Share;
            PermOpened?.Invoke();
        });
        DeleteCommand = new RelayCommand<ShareRowVm>(async row =>
        {
            StatusMsg = await DeleteShareAsync(row);
            await LoadAsync();
        });
        PinCommand = new RelayCommand<ShareRowVm>(row => TogglePin(row.Key));
        RefreshAllCommand = new RelayCommand(async () => { await LoadAsync(); });

        // F5/托盘全局刷新（TS useTickEffect(refreshTick, load) 的事件总线等价面）
        _refreshSub = _bus.Subscribe<RefreshRequested>(evt => { _ = LoadAsync(); });
    }

    public void Dispose() => _refreshSub?.Unsubscribe();

    // ===== 命令（DataGrid 行按钮绑定） =====
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand OpenCreateCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand BatchToggleCommand { get; }
    public RelayCommand BatchDeleteCommand { get; }
    public RelayCommand<Protocol> InstallCommand { get; }
    public RelayCommand<ShareRowVm> DetailCommand { get; }
    public RelayCommand<ShareRowVm> ToggleCommand { get; }
    public RelayCommand<ShareRowVm> EditCommand { get; }
    public RelayCommand<ShareRowVm> PermCommand { get; }
    public RelayCommand<ShareRowVm> DeleteCommand { get; }
    public RelayCommand<ShareRowVm> PinCommand { get; }
    public RelayCommand RefreshAllCommand { get; }

    /// <summary>子视图打开请求（视图层代码后置消费：弹出对应窗口/抽屉）。</summary>
    public event Action? DetailOpened;
    public event Action? EditOpened;
    public event Action? PermOpened;

    public ShareRowVm? DetailTarget { get; private set; }
    public Share? PermShare { get; private set; }

    /// <summary>编辑表单（行内「编辑」按钮填充后弹出；对齐 TS editForm.setFieldsValue）。</summary>
    public ShareEditFormVm? EditForm { get; private set; }

    ShareEditFormVm MakeEditForm(Share s) => new()
    {
        Name = s.name,
        Path = s.path,
        Protocol = s.protocol,
        Description = s.description,
        NfsPermission = s.nfsPermission?.ToString().ToLowerInvariant() ?? "rw",
        AllowRootAccess = s.allowRootAccess ?? false,
        SslPolicy = s.sslPolicy?.ToString() ?? "SslAllow",
        AuthMode = s.authMode?.ToString().ToLowerInvariant() ?? "basic",
        AnonymousEnabled = s.anonymousEnabled ?? false,
    };

    /// <summary>详情抽屉子 VM 工厂（视图消费）。</summary>
    public ShareDetailViewModel MakeDetailVm(Share share) =>
        new(share, _share, _protocol, _system);

    /// <summary>权限抽屉子 VM 工厂（视图消费）。</summary>
    public SharePermissionsViewModel MakePermissionVm(Share share) =>
        new(share, _share, _user, _feed);

    // ===== 列表与筛选（TS filtered memo 逐条） =====
    public ObservableCollection<ShareRowVm> Rows { get; } = new();

    string _keyword = "";
    public string Keyword { get => _keyword; set { if (_keyword == value) return; _keyword = value; RecomputeRows(); RaiseRowProps(); } }

    bool _onlyConnected;
    public bool OnlyConnected { get => _onlyConnected; set { if (_onlyConnected == value) return; _onlyConnected = value; RecomputeRows(); RaiseRowProps(); } }

    /// <summary>TS activeProtocol（"all" | 协议小写）；切换 = 重取列表（queryKey 层筛选）。</summary>
    string _activeProtocol = "all";
    public string ActiveProtocol
    {
        get => _activeProtocol;
        set
        {
            if (_activeProtocol == value) return;
            _activeProtocol = value;
            _ = LoadAsync();
            RaiseRowProps();
        }
    }
    public static readonly string[] ProtocolTabs = ["all", "smb", "nfs", "ftp", "webdav"];
    public static string TabLabel(string tab) => tab switch
    {
        "all" => "全部",
        "smb" => "SMB",
        "nfs" => "NFS",
        "ftp" => "FTP",
        "webdav" => "WebDAV",
        _ => tab,
    };

    bool _loading, _hydrated;
    public bool Loading { get => _loading; private set => SetField(ref _loading, value, nameof(Loading)); }
    public bool Hydrated { get => _hydrated; private set => SetField(ref _hydrated, value, nameof(Hydrated)); }

    string _loadError = "";
    /// <summary>首轮取数失败提示（TS：error && !data → message.error；已有数据时保留列表不闪）。</summary>
    public string LoadError { get => _loadError; private set => SetField(ref _loadError, value, nameof(LoadError)); }

    SharesBundle? _bundle;
    IReadOnlyList<Share> _shares = Array.Empty<Share>();
    Dictionary<string, DiskUsage> _diskIndex = new();
    int _diskLowGb = 20;
    HashSet<string> _pinnedSet = new();

    /// <summary>新手/专家模式（appstate advancedMode）：决定高级项渲染与提交。</summary>
    public bool AdvancedMode => _store.Load().advancedMode;

    // ===== 列显隐（TS columns 展开条件逐条） =====
    public bool ShowProtocolColumn => _activeProtocol == "all";
    public bool ShowFtpColumns => _activeProtocol == "ftp";
    public bool ShowNfsColumns => _activeProtocol is "all" or "nfs";
    public bool ShowWebdavColumns => _activeProtocol == "webdav";
    public bool ShowEncryptedColumn => _activeProtocol is "all" or "smb";

    // ===== 协议能力（TS protocolCaps/osInfo → 选项禁用与安装引导） =====
    public ProtocolFeatureState? CapOf(Protocol p) =>
        _bundle?.ProtocolCaps is { } caps && caps.TryGetValue(p, out var c) ? c : null;

    /// <summary>TS nfsBlocked：本机为 NFS 客户端系统（client-only）→ 创建选项禁用。</summary>
    public bool NfsBlocked =>
        _bundle?.ProtocolCaps is { } caps && caps.TryGetValue(Protocol.Nfs, out var nfs) &&
        nfs.installType == ProtocolInstallType.ClientOnly;

    /// <summary>TS iisBlocked：Win10/11 家庭版等无 IIS → FTP/WebDAV 创建选项禁用。</summary>
    public bool IisBlocked => _bundle?.OsInfo is { } o ? !o.features.iisAvailable : false;

    /// <summary>TS isCurrentProtoInstalled：cap 未加载时默认视为已安装（避免误显安装引导）。</summary>
    public bool CurrentProtoUninstalled =>
        _activeProtocol != "all" &&
        CapOf(ProtocolExtForm.Parse(_activeProtocol)) is { installed: false };

    public string InstallHint =>
        _activeProtocol == "all" ? "" :
        CapOf(ProtocolExtForm.Parse(_activeProtocol))?.installHint ?? "";

    /// <summary>TS 选中「全部」时列出所有未安装协议（nfs/ftp/webdav）。</summary>
    public IReadOnlyList<ProtocolBannerItem> UninstalledBannerItems
    {
        get
        {
            if (_activeProtocol != "all" || _bundle?.ProtocolCaps is not { } caps) return Array.Empty<ProtocolBannerItem>();
            return new[] { Protocol.Nfs, Protocol.Ftp, Protocol.Webdav }
                .Where(p => caps.TryGetValue(p, out var c) && !c.installed)
                .Select(p => BannerItem(p, caps[p]))
                .ToList();
        }
    }

    ProtocolBannerItem BannerItem(Protocol p, ProtocolFeatureState cap)
    {
        var hint = cap.installHint ?? "";
        var badges = new List<(string, string)>();
        // 通过 installHint 内容推断细分状态（TS renderStatusBadges 正则逐条对齐）
        if (Regex.IsMatch(hint, @"IIS.*已安装|IIS 基础.*已安装"))
        {
            badges.Add(("IIS 已装", "green"));
            badges.Add((p.ToString().ToUpperInvariant() + " 角色未装", "orange"));
        }
        else if (Regex.IsMatch(hint, @"IIS.*未安装|IIS.*均未安装"))
        {
            badges.Add(("IIS 未装", "red"));
        }
        if (Regex.IsMatch(hint, @"服务未运行|ftpsvc.*未运行|W3SVC.*未运行"))
        {
            badges.Add(("服务已停", "volcano"));
        }
        return new ProtocolBannerItem
        {
            Protocol = p,
            Hint = hint,
            Badges = badges,
            HasInstallCommand = !string.IsNullOrEmpty(cap.installCommand),
        };
    }

    bool _installing;
    Protocol? _installingProto;
    public bool Installing { get => _installing; private set => SetField(ref _installing, value, nameof(Installing)); }
    public Protocol? InstallingProto { get => _installingProto; private set => SetField(ref _installingProto, value, nameof(InstallingProto)); }

    /// <summary>TS handleInstall：安装成功提示 + 重探能力 + 重取列表。</summary>
    public async Task<string> InstallProtocolAsync(Protocol protocol)
    {
        Installing = true;
        InstallingProto = protocol;
        try
        {
            await _feed.InstallProtocolAsync(protocol);
            StatusMsg = "安装成功，可能需要重启系统";
            var caps = await _feed.DetectProtocolsAsync();
            if (caps is not null && _bundle is not null)
                _bundle = _bundle with { ProtocolCaps = caps };
            await LoadAsync(keepErrorState: true);
            return "";
        }
        catch (Exception e)
        {
            StatusMsg = e.Message;
            return e.Message;
        }
        finally
        {
            Installing = false;
            InstallingProto = null;
            RaiseRowProps();
        }
    }

    // ===== 加载（TS load = invalidateQueries(['shares-page', activeProtocol])） =====
    public async Task LoadAsync(bool keepErrorState = false)
    {
        Loading = true;
        RaiseRowProps();

        IReadOnlyList<Share> shares = Array.Empty<Share>();
        try
        {
            var proto = _activeProtocol == "all" ? null : _activeProtocol;
            var list = await _feed.ListSharesAsync(proto);
            if (list is not null)
            {
                _shares = list;
                LoadError = "";
            }
            else if (!keepErrorState || !_hydrated)
            {
                // 源不可用（服务未接入）：首轮如实提示，后续刷新不闪
                LoadError = string.IsNullOrEmpty(LoadError) ? "共享源不可用（契约服务未接入或查询失败）" : LoadError;
            }
        }
        catch (Exception e)
        {
            if (!_hydrated) LoadError = e.Message;
        }

        var presetsTask = _feed.ListPresetsAsync();
        var usersTask = _feed.ListUsersGroupsAsync();
        var disksTask = _feed.ListDiskUsagesAsync();
        var osTask = _feed.GetOsInfoAsync();
        var capsTask = _feed.DetectProtocolsAsync();
        var capMapTask = _feed.GetCapabilitiesMapAsync();
        await Task.WhenAll(presetsTask, usersTask, disksTask, osTask, capsTask, capMapTask);

        // 各源独立兜底（馈送自身已 catch；此处防测试假馈送直接抛）
        IReadOnlyList<PermissionPreset> presets = Array.Empty<PermissionPreset>();
        try { presets = await presetsTask ?? presets; } catch { }
        IReadOnlyList<LocalUser> users;
        IReadOnlyList<LocalGroup> groups;
        try { (users, groups) = await usersTask; } catch { users = []; groups = []; }
        IReadOnlyList<DiskUsage>? disks;
        try { disks = await disksTask; } catch { disks = null; }
        OsInfo? os;
        try { os = await osTask; } catch { os = null; }
        Dictionary<Protocol, ProtocolFeatureState>? caps;
        try { caps = await capsTask; } catch { caps = null; }

        Dictionary<Protocol, ProtocolCapabilities?>? capMap;
        try { capMap = await capMapTask; } catch { capMap = null; }

        _bundle = new SharesBundle
        {
            Shares = _shares,
            Presets = presets,
            Users = users,
            Groups = groups,
            Disks = disks,
            ProtocolCaps = caps ?? _bundle?.ProtocolCaps,
            ProtocolCapabilitiesMap = capMap ?? _bundle?.ProtocolCapabilitiesMap,
            OsInfo = os ?? _bundle?.OsInfo,
        };

        // 置顶（appstate pinned；复合 key）
        _pinnedSet = new HashSet<string>(_store.Load().pinned ?? Array.Empty<string>());
        _diskIndex = ShareNameUtils.IndexDiskUsages(_bundle.Disks);
        _diskLowGb = _store.Load().alertRules?.diskLowGb ?? 20;

        Hydrated = true;
        Loading = false;
        RecomputeRows();
        ClearSelection();
        RaiseRowProps();
    }


    /// <summary>TS filtered：关键字（名称/路径/描述）→「只看有人连接」→ 置顶恒排最前（稳定排序）。</summary>
    void RecomputeRows()
    {
        var q = Keyword.Trim().ToLowerInvariant();
        var list = _shares;
        if (q.Length > 0)
        {
            list = list.Where(s =>
                (s.name ?? "").ToLowerInvariant().Contains(q) ||
                (s.path ?? "").ToLowerInvariant().Contains(q) ||
                (s.description ?? "").ToLowerInvariant().Contains(q)).ToList();
        }
        if (OnlyConnected)
            list = list.Where(s => s.concurrentUsers > 0).ToList();

        // 置顶恒排最前，其余保持原有顺序（OrderBy 稳定排序，不引入二次抖动）
        var sorted = list
            .OrderBy(s => !_pinnedSet.Contains(ToKey(s)))
            .ToList();

        Rows.Clear();
        foreach (var s in sorted)
        {
            var capsMap = _bundle?.ProtocolCapabilitiesMap;
            var row = new ShareRowVm(s, _diskIndex, _diskLowGb,
                capsMap is not null && capsMap.TryGetValue(s.protocol, out var caps2) ? caps2 : null, ShowProtocolColumn);
            row.Pinned = _pinnedSet.Contains(row.Key);
            Rows.Add(row);
        }
    }

    /// <summary>TS toKey / parseKey（复合 key 双向）。</summary>
    public static string ToKey(Share s) => $"{s.protocol.ToString().ToLowerInvariant()}:{s.name}";

    public static (Protocol protocol, string name) ParseKey(string key)
    {
        var idx = key.IndexOf(':');
        if (idx < 0) return (Protocol.Smb, key);
        return (ProtocolExtForm.Parse(key[..idx]), key[(idx + 1)..]);
    }

    // ===== 置顶（TS togglePin：appstate.json 持久化，重启仍在） =====
    public void TogglePin(string key)
    {
        var pinned = _store.Load().pinned?.ToList() ?? new List<string>();
        if (pinned.Contains(key)) pinned.Remove(key);
        else pinned.Add(key);
        _store.SaveState(new AppStatePatch { pinned = pinned });
        _pinnedSet = new HashSet<string>(pinned);
        RecomputeRows();
        RaiseRowProps();
    }

    // ===== 批量选择逻辑（TS selectedShares + Del/Space hotkey 语义） =====
    HashSet<string> _selected = new();

    public void SetSelection(IEnumerable<string> keys)
    {
        var next = new HashSet<string>(keys);
        if (_selected.SetEquals(next)) return;
        _selected = next;
        RaiseRowProps();
    }

    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected = new HashSet<string>();
        RaiseRowProps();
    }

    public bool HasSelection => _selected.Count > 0;
    public int SelectedCount => _selected.Count;

    /// <summary>选中目标（∩ 当前列表；TS targets 解析语义）。</summary>
    public IReadOnlyList<Share> SelectedShares =>
        _selected.Select(ParseKey)
                  .Select(t => _shares.FirstOrDefault(s => s.protocol == t.protocol && s.name == t.name))
                  .Where(s => s is not null)
                  .Select(s => s!)
                  .ToList();

    // ===== 批量删除（TS confirmBatchDelete：allSettled + 聚合提示） =====
    /// <summary>TS confirmBatchDelete 确认框影响文案（targets=选中 ∩ 当前列表）。</summary>
    public string BatchDeleteImpactText =>
        ShareDeleteImpact.DeleteImpactText(SelectedShares).text;
    public bool BatchDeleteImpactDanger =>
        ShareDeleteImpact.DeleteImpactText(SelectedShares).danger;
    public string BatchDeleteUndoHint =>
        ShareDeleteImpact.UndoHintText(SelectedShares);

    public async Task<string> ExecuteBatchDeleteAsync()
    {
        if (_selected.Count == 0) return "";
        var keys = _selected.OrderBy(k => k).ToList();
        var results = await Task.WhenAll(keys.Select(k => DeleteOneAsync(k)));
        var failed = results.Count(r => !r.ok);
        var msg = failed > 0
            ? $"{keys.Count - failed} 个成功，{failed} 个失败"
            : $"已删除 {keys.Count} 个共享";
        if (failed > 0) StatusIsWarning = true;
        StatusMsg = msg;
        ClearSelection();
        await LoadAsync();
        return msg;
    }

    /// <summary>单共享删除路由：协议走 IProtocolService；SMB 回退 IShareService。返回 (成功, 原因)。</summary>
    async Task<(bool ok, string error)> DeleteOneAsync(string key)
    {
        var (proto, name) = ParseKey(key);
        try
        {
            if (proto == Protocol.Smb && _protocol is null && _share is not null)
            {
                await _share.DeleteShareAsync(name);
            }
            else if (_protocol is { } ps)
            {
                await ps.AdapterDeleteAsync(proto, name);
            }
            else
            {
                return (false, "契约服务未接入");
            }
            return (true, "");
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }   // ===== 批量启停（TS shareToggleTick：未找到=not found 失败项） =====
    public async Task<string> ExecuteBatchToggleAsync()
    {
        var keys = _selected.ToList();
        if (keys.Count == 0) return "";
        var byKey = _shares.ToDictionary(ToKey, s => s);
        var results = await Task.WhenAll(keys.Select(async k =>
        {
            if (!byKey.TryGetValue(k, out var cur))
                return (false, "not found");
            var (proto, name) = ParseKey(k);
            var enabled = cur.status != ShareStatus.Enabled;
            try
            {
                if (proto == Protocol.Smb && _protocol is null && _share is not null)
                {
                    await _share.ToggleShareAsync(name, enabled);
                }
                else
                {
                    if (_protocol is null) return (false, "契约服务未接入");
                    await _protocol.AdapterToggleAsync(proto, name, enabled);
                }
                return (true, "");
            }
            catch (Exception e)
            {
                return (false, e.Message);
            }
        }));
        var failed = results.Count(r => !r.Item1);
        var msg = failed > 0
            ? $"{keys.Count - failed} 个成功，{failed} 个失败"
            : $"已切换 {keys.Count} 个共享";
        if (failed > 0) StatusIsWarning = true;
        StatusMsg = msg;
        ClearSelection();
        await LoadAsync();
        return msg;
    }

    // ===== 行内单操作（TS handleDelete/handleToggle：成功/失败文案逐字） =====
    public async Task<string> DeleteShareAsync(ShareRowVm row)
    {
        var (ok, err) = await DeleteOneAsync(row.Key);
        if (!ok)
        {
            StatusIsWarning = true;
            return err;
        }
        StatusMsg = "已删除";
        return "已删除";
    }

    public async Task<string> ToggleShareAsync(ShareRowVm row)
    {
        var enabled = row.Share.status != ShareStatus.Enabled;
        try
        {
            if (row.Share.protocol == Protocol.Smb && _protocol is null && _share is not null)
            {
                await _share.ToggleShareAsync(row.Share.name, enabled);
            }
            else
            {
                if (_protocol is null) { StatusIsWarning = true; return "契约服务未接入"; }
                await _protocol.AdapterToggleAsync(row.Share.protocol, row.Share.name, enabled);
            }
            StatusMsg = row.Share.status == ShareStatus.Enabled ? "已禁用" : "已启用";
            return StatusMsg;
        }
        catch (Exception e)
        {
            StatusIsWarning = true;
            return e.Message;
        }
    }

    // ===== 编辑保存（TS handleEdit） =====
    public async Task<string> SaveEditAsync()
    {
        if (EditForm is not { } form) return "";
        try
        {
            var input = form.BuildUpdateInput(AdvancedMode);
            if (_protocol is { } ps)
            {
                await ps.AdapterUpdateAsync(form.Name, input);
            }
            else if (form.Protocol == Protocol.Smb && _share is not null)
            {
                // SMB 回退通道：UpdateShareOpts（仅 SMB 可改子集）
                await _share.UpdateShareAsync(form.Name, new UpdateShareOpts
                {
                    description = form.Description,
                    hidden = null,
                });
            }
            else
            {
                StatusIsWarning = true;
                return "编辑保存需要契约服务（未接入）";
            }
            StatusMsg = "已保存";
            await LoadAsync();
            return "已保存";
        }
        catch (Exception e)
        {
            StatusIsWarning = true;
            return e.Message;
        }
    }

    // ===== 新建向导（TS 新建 Modal + hotkey/Ctrl+N + 拖拽 + 文件夹浏览） =====
    public ShareCreateFormVm CreateForm { get; } = new();

    /// <summary>权限模板下拉（TS 新建表单 presetId 选择；服务缺席时为空列表 = 不渲染）。</summary>
    public IReadOnlyList<WinShare.Core.Contracts.PermissionPreset> CreatePresets =>
        _bundle?.Presets ?? System.Array.Empty<WinShare.Core.Contracts.PermissionPreset>();

    /// <summary>访问控制候选账号（TS accountOptions：本地用户 + 组（组带「组」标记））。</summary>
    public IReadOnlyList<string> CreateAccountOptions
    {
        get
        {
            var users = _bundle?.Users ?? System.Array.Empty<WinShare.Core.Contracts.LocalUser>();
            var groups = _bundle?.Groups ?? System.Array.Empty<WinShare.Core.Contracts.LocalGroup>();
            return users.Select(u => u.name).Concat(groups.Select(g => $"{g.name}（组）")).ToList();
        }
    }

    bool _createWizardOpen;
    public bool CreateWizardOpen { get => _createWizardOpen; private set => SetField(ref _createWizardOpen, value, nameof(CreateWizardOpen)); }

    /// <summary>TS：打开向导前 nameTouched=已有名字；已有路径不覆盖；建议路径拿不到不挡新建。</summary>
    public async Task OpenCreateWizardAsync()
    {
        CreateForm.NameTouched = CreateForm.Name.Length > 0;
        if (CreateForm.Path.Length == 0 && _disk is not null)
        {
            try
            {
                var p = await _disk.SuggestRootAsync();
                if (!string.IsNullOrEmpty(p))
                {
                    CreateForm.FillPathAndName(p);
                }
            }
            catch
            {
                // 建议路径拿不到不该挡住新建：保持空表单，用户自己填
            }
        }
        OpenCreateWizard();
    }

    public void OpenCreateWizard()
    {
        CreateWizardOpen = true;
        RaiseRowProps();
    }

    public void CloseCreateWizard()
    {
        CreateWizardOpen = false;
        RaiseRowProps();
    }

    /// <summary>TS handleDrop：拖入文件夹 → 取第一个路径填表单（FillPathAndName 语义）。</summary>
    public void DropFolders(IReadOnlyList<string> paths)
    {
        var first = paths.FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (first is null) return;
        CreateForm.FillPathAndName(first);
        OpenCreateWizard();
    }

    /// <summary>TS handlePickFolder（浏览器按钮走 OpenFolderDialog）：选到即填表单。</summary>
    public void PickedFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        CreateForm.FillPathAndName(path);
    }

    /// <summary>当前协议可选场景预设（新手模式收敛高级预设）。</summary>
    public IReadOnlyList<ScenarioPreset> CreateScenarios =>
        CreateScenarioPresets.ForProtocol(CreateForm.Protocol, AdvancedMode);

    /// <summary>TS handleCreate（含创建后 preset 应用 + 表单复位）。</summary>
    public async Task<string> CreateShareAsync()
    {
        if (!CreateForm.Validate(out var err))
        {
            StatusMsg = err ?? "表单校验失败";
            return StatusMsg;
        }
        var protocol = ProtocolExtForm.Parse(CreateForm.Protocol);
        try
        {
            if (_protocol is { } ps)
            {
                await ps.AdapterCreateAsync(CreateForm.BuildCreateInput(AdvancedMode));
            }
            else if (protocol == Protocol.Smb && _share is not null)
            {
                await _share.CreateShareAsync(CreateForm.BuildSmbCreateOpts(AdvancedMode));
            }
            else
            {
                StatusIsWarning = true;
                return "创建共享需要契约服务（未接入）";
            }

            // TS：if (v.presetId && protocol === 'smb') → getShare + applyPreset(overwrite)
            if (CreateForm.PresetId is { Length: > 0 } presetId && protocol == Protocol.Smb)
            {
                await ApplyPresetToCreatedAsync(CreateForm.Name, presetId);
            }

            StatusMsg = "创建成功";
            CreateForm.Reset();
            await LoadAsync();
            CloseCreateWizard();
            return "";
        }
        catch (Exception e)
        {
            StatusIsWarning = true;
            StatusMsg = e.Message;
            return e.Message;
        }
    }

    async Task ApplyPresetToCreatedAsync(string name, string presetId)
    {
        if (_preset is null)
        {
            StatusMsg = "创建成功（权限模板未应用：预设契约服务未接入）";
            return;
        }
        try
        {
            var created = _share is not null ? await _share.GetShareAsync(name) : new Share { name = name, path = CreateForm.Path, description = CreateForm.Description, protocol = Protocol.Smb, type = ShareType.Disk, concurrentUsers = 0, status = ShareStatus.Enabled, cached = false, encrypted = false };
            await _preset.ApplyPresetAsync(created.name, presetId, ApplyPresetMode.Overwrite);
        }
        catch (Exception e)
        {
            StatusMsg = $"创建成功（权限模板应用失败：{e.Message}）";
        }
    }

    // ===== 配置导入导出（TS handleExport/importProps：护栏 + 文案逐字） =====
    public async Task<string> ExportConfigAsync()
    {
        if (_share is null)
        {
            StatusMsg = "导出配置需要共享契约服务（未接入）";
            return StatusMsg;
        }
        try
        {
            var json = await _share.ExportConfigAsync();
            await _fileSaver($"winshare-export-{_nowMs()}.json", Encoding.UTF8.GetBytes(json));
            StatusMsg = "已导出（仅 SMB）";
            return "";
        }
        catch (Exception e)
        {
            StatusIsWarning = true;
            StatusMsg = e.Message;
            return e.Message;
        }
    }

    public async Task<string> ImportConfigAsync()
    {
        string? text;
        try
        {
            text = await _importPicker();
        }
        catch (Exception e)
        {
            StatusIsWarning = true;
            StatusMsg = e.Message;
            return e.Message;
        }
        if (text is null) return ""; // 用户取消选择
        if (_share is null)
        {
            StatusMsg = "导入配置需要共享契约服务（未接入）";
            return StatusMsg;
        }
        try
        {
            ShareImportGuard.GuardImportJson(text); // 护栏先行（2MB/JSON 结构/500 条目）
            var report = await _share.ImportConfigAsync(text);
            StatusIsWarning = report.skipped > 0;
            StatusMsg = ShareImportGuard.BuildImportReportText(report);
            await LoadAsync();
            return "";
        }
        catch (Exception e)
        {
            StatusIsWarning = true;
            StatusMsg = e.Message;
            return e.Message;
        }
    }

    // ===== 状态提示（视图层 StatusTip 消费；对齐 TS message.success/warning/error） =====
    string _statusMsg = "";
    public string StatusMsg { get => _statusMsg; private set => SetField(ref _statusMsg, value, nameof(StatusMsg)); }
    bool _statusIsWarning;
    public bool StatusIsWarning { get => _statusIsWarning; private set => SetField(ref _statusIsWarning, value, nameof(StatusIsWarning)); }
    public void ClearStatus()
    {
        StatusMsg = "";
        StatusIsWarning = false;
    }

    // ===== INPC =====
    void SetField<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    void RaiseRowProps()
    {
        foreach (var name in new[]
        {
            nameof(Rows), nameof(Loading), nameof(Hydrated), nameof(LoadError),
            nameof(ShowProtocolColumn), nameof(ShowFtpColumns), nameof(ShowNfsColumns),
            nameof(ShowWebdavColumns), nameof(ShowEncryptedColumn),
            nameof(NfsBlocked), nameof(IisBlocked), nameof(CurrentProtoUninstalled),
            nameof(InstallHint), nameof(UninstalledBannerItems),
            nameof(Installing), nameof(InstallingProto),
            nameof(HasSelection), nameof(SelectedCount),
            nameof(StatusMsg), nameof(StatusIsWarning),
            nameof(CreateWizardOpen), nameof(CreateScenarios),
            nameof(EditForm),
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}


/// <summary>
/// 参数化命令（强类型参数；DataGrid 行按钮绑定，Parameter=行 VM 或协议枚举）。
/// CanExecute 恒真——能力门控（CanToggle/SupportsPermissions）由行属性驱动视图显隐 +
/// 命令处理器内再查一次（双重保险，对齐 TS 能力位语义）。
/// </summary>
public sealed class RelayCommand<T> : System.Windows.Input.ICommand
{
    readonly Action<T>? _sync;
    readonly Func<T, Task>? _async;

    public RelayCommand(Action<T> action)
    {
        _sync = action ?? throw new ArgumentNullException(nameof(action));
    }

    public RelayCommand(Func<T, Task> action)
    {
        _async = action ?? throw new ArgumentNullException(nameof(action));
    }

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        if (parameter is not T t) return;
        if (_async is not null)
        {
            Task result;
            try
            {
                result = _async(t);
            }
            catch
            {
                // 同步段异常（VM 状态校验类）：界面文案语义由各命令自管
                return;
            }
            return; // fire-and-forget；异步错误经 VM StatusMsg 语义回报
        }
        _sync!(t);
    }
}
