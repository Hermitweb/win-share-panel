using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WinShare.Core.Contracts;
using WinShare.App.Shares;

namespace WinShare.App.Views.Shares;

/// <summary>
/// 共享详情抽屉（对齐 src/components/ShareDetailDrawer.tsx；逻辑在 ShareDetailViewModel）：
/// 基本信息（网络路径）/ 高级属性（SMB）/ 连接 / 打开文件 / 站点配置（非 SMB）。
/// 抽屉由 SharesPageView 实例化并以 SetShare 打开；遮罩点击关闭由宿主控制。
/// </summary>
public partial class ShareDetailDrawer : UserControl
{
    ShareDetailViewModel? _vm;

    public ShareDetailDrawer()
    {
        InitializeComponent();
    }

    /// <summary>打开抽屉：填表 + 订阅 VM 刷新 + 触发各取数路径（对齐 TS 打开 effect）。</summary>
    public void SetShare(Share share, ShareDetailViewModel vm)
    {
        var old = _vm;
        if (old is not null) old.PropertyChanged -= OnVmChanged;
        _vm = vm;
        vm.PropertyChanged += OnVmChanged;

        DrawerTitle.Text = $"共享详情：{share.name}";

        // ===== 基本信息 =====
        VName.Text = share.name;
        VPath.Text = share.path;
        VDesc.Text = string.IsNullOrEmpty(share.description) ? "（无）" : share.description;
        VProto.Text = share.protocol.ToString().ToUpperInvariant();
        VType.Text = share.type.ToString();
        VStatus.Text = share.status == ShareStatus.Enabled ? "已启用" : "已禁用";
        VConns.Text = ((object)share.concurrentUsers).ToString()!;
        var smb = share.protocol == Protocol.Smb;
        RowEncrypted.Visibility = smb ? Visibility.Visible : Visibility.Collapsed;
        RowCached.Visibility = smb ? Visibility.Visible : Visibility.Collapsed;
        if (smb)
        {
            VEncrypted.Text = share.encrypted ? "是" : "否";
            VCached.Text = share.cached ? "是" : "否";
        }

        // ===== Tab 显隐（TS items 按协议组装） =====
        RbProps.Visibility = RbConns.Visibility = RbFiles.Visibility =
            smb ? Visibility.Visible : Visibility.Collapsed;
        RbSite.Visibility = !smb ? Visibility.Visible : Visibility.Collapsed;
        FooterNote.Visibility = !smb ? Visibility.Visible : Visibility.Collapsed;
        if (!smb) FooterProto.Text = share.protocol.ToString().ToUpperInvariant();
        RbInfo.IsChecked = true;
        TabContent.SelectedIndex = 0;

        // ===== 高级属性预填（TS form 按协议预填） =====
        EDescription.Text = vm.Description;
        ELimit.Text = vm.ConcurrentUserLimit > 0 ? vm.ConcurrentUserLimit.ToString() : "";
        SetTag(CFem, vm.FolderEnumerationModeText, "Unrestricted");
        SetTag(CCaching, vm.CachingModeText, "Manual");
        ChkEncryptData.IsChecked = vm.EncryptData;
        ChkHidden.IsChecked = vm.Hidden;

        // ===== 站点配置预填 =====
        SiteNfs.Visibility = share.protocol == Protocol.Nfs ? Visibility.Visible : Visibility.Collapsed;
        SiteFtp.Visibility = share.protocol == Protocol.Ftp ? Visibility.Visible : Visibility.Collapsed;
        SiteWebdav.Visibility = share.protocol == Protocol.Webdav ? Visibility.Visible : Visibility.Collapsed;
        SetTag(CNfsPerm, vm.NfsPermissionText, "rw");
        ChkSiteRoot.IsChecked = vm.AllowRootAccess;
        ChkSiteUnmapped.IsChecked = vm.EnableUnmappedAccess;
        SetTag(CFtpSsl, vm.SslPolicyText, "SslAllow");
        SetTag(CFtpAuth, vm.AuthModeText, "basic");
        TSitePath.Text = vm.PhysicalPath;
        ChkWdavAnonSite.IsChecked = vm.WebdavAnonymousEnabled;
        ChkWdavAuthoring.IsChecked = vm.WebdavAuthoringEnabled;

        // ===== 取数路径（连接/打开文件各恰好一次 IPC；主机名静默失败） =====
        _ = vm.LoadHostNameAsync();
        if (smb)
        {
            _ = vm.RefreshConnectionsAsync();
            _ = vm.RefreshOpenFilesAsync();
        }
        RefreshDynamicVm();
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(RefreshDynamicVm);
    }

    void RefreshDynamicVm()
    {
        if (_vm is not { } vm) return;
        // 网络路径（主机名到位后更新）
        VNetPath.Text = string.IsNullOrEmpty(vm.NetPath) ? "—" : vm.NetPath;

        // 连接
        var conns = vm.Connections;
        ConnCountText.Text = $"当前 {conns?.concurrentUsers ?? 0} 个连接";
        ConnsGrid.ItemsSource = conns?.clientConnections?.ToList();
        ConnsEmpty.Visibility = conns?.clientConnections is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

        // 打开文件
        FilesCountText.Text = $"{vm.OpenFiles.Count} 个打开文件";
        FilesGrid.ItemsSource = vm.OpenFiles.ToList();
        FilesEmpty.Visibility = vm.OpenFiles.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        BtnCloseAll.Visibility = vm.OpenFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== Tab 切换 =====
    void OnTabChanged(object sender, RoutedEventArgs e)
    {
        TabContent.SelectedIndex = RbProps.IsChecked == true ? 1
            : RbConns.IsChecked == true ? 2
            : RbFiles.IsChecked == true ? 3
            : RbSite.IsChecked == true ? 4
            : 0;
    }

    // ===== 高级属性保存（TS handleSave） =====
    async void OnSaveSmbProps(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        vm.Description = EDescription.Text;
        vm.ConcurrentUserLimit = int.TryParse(ELimit.Text, out var limit) ? limit : 0;
        vm.CachingModeText = (CCaching.SelectedItem as ComboBoxItem)?.Tag as string ?? "Manual";
        vm.FolderEnumerationModeText = (CFem.SelectedItem as ComboBoxItem)?.Tag as string ?? "Unrestricted";
        vm.EncryptData = ChkEncryptData.IsChecked == true;
        vm.Hidden = ChkHidden.IsChecked == true;
        LSaveSmbMsg.Text = await vm.SaveSmbPropsAsync();
    }

    // ===== 连接/打开文件刷新（TS connNonce/filesNonce 独立） =====
    async void OnRefreshConns(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        await vm.RefreshConnectionsAsync();
        RefreshDynamicVm();
    }

    async void OnRefreshFiles(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        await vm.RefreshOpenFilesAsync();
        RefreshDynamicVm();
    }

    /// <summary>TS 全部关闭 Popconfirm：确认"可能导致客户端数据丢失"。</summary>
    async void OnCloseAll(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        var res = MessageBox.Show(
            $"关闭共享 {vm.Share.name} 上的全部 {vm.OpenFiles.Count} 个打开文件？\n\n可能导致客户端数据丢失",
            "确认关闭", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;
        var msg = await vm.CloseAllFilesAsync();
        if (msg.Length > 0) LSaveSmbMsg.Text = msg;
        RefreshDynamicVm();
    }

    // ===== 站点配置（TS handleProtoSave） =====
    void OnSiteBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择本地物理路径" };
        if (dlg.ShowDialog() == true && _vm is not null)
        {
            _vm.PhysicalPath = dlg.FolderName;
            TSitePath.Text = _vm.PhysicalPath;
        }
    }

    async void OnSaveSite(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        vm.NfsPermissionText = (CNfsPerm.SelectedItem as ComboBoxItem)?.Tag as string ?? "rw";
        vm.AllowRootAccess = ChkSiteRoot.IsChecked == true;
        vm.EnableUnmappedAccess = ChkSiteUnmapped.IsChecked == true;
        vm.SslPolicyText = (CFtpSsl.SelectedItem as ComboBoxItem)?.Tag as string ?? "SslAllow";
        vm.AuthModeText = (CFtpAuth.SelectedItem as ComboBoxItem)?.Tag as string ?? "basic";
        vm.PhysicalPath = TSitePath.Text;
        vm.WebdavAnonymousEnabled = ChkWdavAnonSite.IsChecked == true;
        vm.WebdavAuthoringEnabled = ChkWdavAuthoring.IsChecked == true;
        LSaveSiteMsg.Text = await vm.SaveSiteConfigAsync();
    }

    /// <summary>按 Tag 选 ComboBox 项（枚举字符串大小写归一，宽松匹配）。</summary>
    static void SetTag(ComboBox combo, string value, string fallback)
    {
        var target = (value ?? fallback).Trim().ToLowerInvariant();
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => ((string?)i.Tag ?? "").Trim().ToLowerInvariant() == target);
        if (combo.SelectedItem is null) combo.SelectedIndex = 0;
    }
}
