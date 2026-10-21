using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinShare.Core.Contracts;

namespace WinShare.App.Shares;

/// <summary>
/// 共享详情抽屉 ViewModel（对齐 src/components/ShareDetailDrawer.tsx）：
/// - 基本信息（含网络路径：SMB→UNC；FTP/WebDAV→访问 URL；主机名取 system.currentUser）
/// - 高级属性（SMB）：经 IShareService.UpdateShareAsync（UpdateShareOpts）
/// - 连接 / 打开文件：各恰好一次取数，独立刷新序号（TS connNonce/filesNonce 语义）
/// - 站点配置（NFS/FTP/WebDAV）：经 IProtocolService.AdapterUpdateAsync（UpdateShareInput）
/// 服务缺席时走占位降级路径（如实报"未接入"，不崩不臆造）。
/// </summary>
public sealed class ShareDetailViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    readonly IShareService? _share;
    readonly IProtocolService? _protocol;
    readonly ISystemService? _system;

    public ShareDetailViewModel(Share share, IShareService? shareService, IProtocolService? protocolService, ISystemService? systemService)
    {
        Share = share;
        _share = shareService;
        _protocol = protocolService;
        _system = systemService;
        IsSmb = share.protocol == Protocol.Smb;
        ActiveTab = "info";
        SslPolicyText = Share.sslPolicy?.ToString() ?? "SslAllow";
        AuthModeText = Share.authMode?.ToString() ?? "basic";
        NfsPermissionText = Share.nfsPermission?.ToString() ?? "rw";
        AllowRootAccess = Share.allowRootAccess ?? false;
        EnableUnmappedAccess = Share.enableUnmappedAccess ?? false;
        PhysicalPath = Share.path;
        WebdavAnonymousEnabled = Share.anonymousEnabled ?? false;
        WebdavAuthoringEnabled = Share.authoringEnabled ?? false;
        if (IsSmb)
        {
            Description = Share.description;
            Hidden = Share.hidden;
        }
    }

    public Share Share { get; }
    public bool IsSmb { get; }
    public bool HasSiteConfigTab => !IsSmb;

    /// <summary>当前 Tab（TS useState tab；打开/换共享回 "info"）。</summary>
    string _activeTab = "info";
    public string ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab == value) return;
            _activeTab = value;
            OnPc();
        }
    }

    // ===== 基本信息 =====
    public string HostName { get; private set; } = "";

    /// <summary>TS netPath：SMB→\\host\name；FTP→ftp://host[:port]；WebDAV→http://host[:port]；NFS→本地路径。</summary>
    public string NetPath
    {
        get
        {
            if (Share.protocol == Protocol.Smb)
                return string.IsNullOrEmpty(HostName) ? "" : $@"\\{HostName}\{Share.name}";
            if (Share.protocol == Protocol.Ftp)
                return $"ftp://{HostName}{(Share.port is > 0 ? ":" + Share.port : "")}";
            if (Share.protocol == Protocol.Webdav)
                return $"http://{HostName}{(Share.port is > 0 ? ":" + Share.port : "")}";
            return Share.path;
        }
    }

    /// <summary>TS api.system.currentUser().computerName（失败静默 → 主机名留空）。</summary>
    public async Task LoadHostNameAsync()
    {
        if (_system is null) { HostName = ""; OnPc(); return; }
        try
        {
            HostName = (await _system.GetCurrentUserAsync()).computerName;
            OnPc();
        }
        catch
        {
            HostName = "";
            OnPc();
        }
    }

    // ===== 高级属性（SMB；TS form 预填 + handleSave → updateShare(UpdateShareOpts)） =====
    public string Description { get; set; } = "";
    public int ConcurrentUserLimit { get; set; }
    public string CachingModeText { get; set; } = "Manual";
    public string FolderEnumerationModeText { get; set; } = "Unrestricted";
    public bool EncryptData { get; set; }
    public bool Hidden { get; set; }

    /// <summary>TS handleSave：SMB 高级属性保存；服务缺席/失败如实回报。</summary>
    public async Task<string> SaveSmbPropsAsync()
    {
        if (_share is null) return "保存属性需要共享契约服务（未接入）";
        try
        {
            await _share.UpdateShareAsync(Share.name, new UpdateShareOpts
            {
                description = Description,
                concurrentUserLimit = ConcurrentUserLimit > 0 ? ConcurrentUserLimit : null,
                cachingMode = CachingModeExt.Parse(CachingModeText),
                folderEnumerationMode = FemExt.Parse(FolderEnumerationModeText),
                encryptData = EncryptData,
                hidden = Hidden,
            });
            return "已保存";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    // ===== 连接（TS connNonce 独立刷新；失败走错误提示） =====
    public ShareConnections? Connections { get; private set; }
    public bool ConnectionsLoading { get; private set; }
    public string ConnectionsError { get; private set; } = "";

    public async Task RefreshConnectionsAsync()
    {
        if (!IsSmb) return;
        ConnectionsLoading = true;
        OnPc();
        if (_share is null)
        {
            ConnectionsLoading = false;
            ConnectionsError = "连接信息需要共享契约服务（未接入）";
            OnPc();
            return;
        }
        try
        {
            Connections = await _share.GetShareConnectionsAsync(Share.name);
            ConnectionsError = "";
        }
        catch (Exception e)
        {
            ConnectionsError = e.Message;
        }
        finally
        {
            ConnectionsLoading = false;
            OnPc();
        }
    }

    // ===== 打开文件（TS filesNonce 独立刷新；失败静默置空——既有语义） =====
    public IReadOnlyList<ShareOpenFile> OpenFiles { get; private set; } = Array.Empty<ShareOpenFile>();

    public async Task RefreshOpenFilesAsync()
    {
        if (!IsSmb) return;
        if (_share is null) { OpenFiles = Array.Empty<ShareOpenFile>(); OnPc(); return; }
        try
        {
            OpenFiles = await _share.GetShareOpenFilesAsync(Share.name);
        }
        catch
        {
            OpenFiles = Array.Empty<ShareOpenFile>();
        }
        OnPc();
    }

    /// <summary>
    /// TS handleCloseAllFiles：关闭成功/失败计数分别提示（failed&gt;0 走 warning），
    /// 成功后连带重读连接与打开文件（两个 nonce 一起 bump）。
    /// </summary>
    public async Task<string> CloseAllFilesAsync()
    {
        if (_share is null) return "关闭打开文件需要共享契约服务（未接入）";
        try
        {
            var r = await _share.CloseShareOpenFilesAsync(Share.name);
            await RefreshConnectionsAsync();
            await RefreshOpenFilesAsync();
            return r.failed > 0
                ? $"已关闭 {r.closed} 个，失败 {r.failed} 个"
                : $"已关闭 {r.closed} 个打开文件";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    // ===== 站点配置（非 SMB；TS handleProtoSave → adapter.update(UpdateShareInput)） =====
    public string SslPolicyText { get; set; }
    public string AuthModeText { get; set; }
    public string NfsPermissionText { get; set; }
    public bool AllowRootAccess { get; set; }
    public bool EnableUnmappedAccess { get; set; }
    public string PhysicalPath { get; set; } = "";
    public bool WebdavAnonymousEnabled { get; set; }
    public bool WebdavAuthoringEnabled { get; set; }

    /// <summary>TS handleProtoSave 的 UpdateShareInput 组装（按协议取对应字段）。</summary>
    public UpdateShareInput BuildSiteConfigInput() => new()
    {
        protocol = Share.protocol,
        nfsPermission = Share.protocol == Protocol.Nfs ? NfsPermissionExt.Parse(NfsPermissionText) : null,
        allowRootAccess = Share.protocol == Protocol.Nfs ? AllowRootAccess : null,
        enableUnmappedAccess = Share.protocol == Protocol.Nfs ? EnableUnmappedAccess : null,
        sslPolicy = Share.protocol == Protocol.Ftp ? SslPolicyExt.Parse(SslPolicyText) : null,
        authMode = Share.protocol == Protocol.Ftp ? AuthModeExt.Parse(AuthModeText) : null,
        physicalPath = Share.protocol == Protocol.Ftp ? PhysicalPath : null,
        anonymousEnabled = Share.protocol == Protocol.Webdav ? WebdavAnonymousEnabled : null,
        authoringEnabled = Share.protocol == Protocol.Webdav ? WebdavAuthoringEnabled : null,
    };

    /// <summary>站点配置保存（非 SMB 协议；SMB 走 SaveSmbPropsAsync，TS 同分支）。</summary>
    public async Task<string> SaveSiteConfigAsync()
    {
        if (IsSmb) return "保存属性";
        if (_protocol is null) return "站点配置保存需要协议路由契约服务（未接入）";
        try
        {
            await _protocol.AdapterUpdateAsync(Share.name, BuildSiteConfigInput());
            return "站点配置已保存";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    void OnPc() => RaiseAll();

    public void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(ActiveTab), nameof(HostName), nameof(NetPath),
            nameof(Connections), nameof(ConnectionsLoading), nameof(ConnectionsError),
            nameof(OpenFiles),
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
