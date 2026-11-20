using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WinShare.App.Shares;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Shares;

/// <summary>
/// 权限抽屉（对齐 src/components/PermissionDrawer.tsx；逻辑在 SharePermissionsViewModel）：
/// 共享权限行编辑（添加三护栏/Deny 语义/保存确认）+ 常用账号提示 + NTFS 只读 ACL Tab。
/// </summary>
public partial class SharePermissionDrawer : UserControl
{
    SharePermissionsViewModel? _vm;

    public SharePermissionDrawer()
    {
        InitializeComponent();
    }

    /// <summary>打开抽屉：标题/候选/表单刷新；TS 打开 effect = 取数 + 候选。</summary>
    public void SetShare(Share share, SharePermissionsViewModel vm)
    {
        var old = _vm;
        if (old is not null) old.PropertyChanged -= OnVmChanged;
        _vm = vm;
        vm.PropertyChanged += OnVmChanged;

        DrawerTitle.Text = $"权限管理：{share.name}";
        NtfsPathText.Text = $"路径：{share.path}";
        NtfsGrid.ItemsSource = null;
        NtfsEmpty.Visibility = Visibility.Visible;
        _ = vm.LoadAsync();
        RefreshDynamicVm();
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshDynamicVm);

    void RefreshDynamicVm()
    {
        if (_vm is not { } vm) return;
        RowsGrid.ItemsSource = vm.Rows;
        CandTip.Text = vm.CandidatesTip;
        // v2.2.0：「添加账号」下拉候选（本地用户+组）；不变不重置，避免输入中清空
        var options = vm.Candidates;
        if (!ReferenceEquals(TxtNewAccount.Tag, options))
        {
            TxtNewAccount.ItemsSource = options;
            TxtNewAccount.Tag = options;
        }
    }

    /// <summary>v2.2.0：共享页遮罩关闭前读取——有未保存修改时先确认（防误触丢改动）。</summary>
    public bool IsDirty => _vm?.IsDirty ?? false;

    // 1 列纵向流：区块选择器驱动内容区显隐（原 TabControl SelectedIndex 的等价面）
    void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (RbShare is null || RbNtfs is null || SecShare is null || SecNtfs is null) return;
        SecShare.Visibility = RbShare.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SecNtfs.Visibility = RbNtfs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        // v2.2.0 第二轮：切到 NTFS 页签自动加载 ACL（首次 ItemsSource 为空时触发；
        // 此前必须手动点「加载」——多一步、且空态文案才提示要点按钮）
        if (RbNtfs.IsChecked == true && NtfsGrid.ItemsSource is null && _vm is not null)
        {
            _ = LoadNtfsIntoViewAsync();
        }
    }

    void OnReload(object sender, RoutedEventArgs e) => _ = _vm?.LoadAsync();

    /// <summary>TS handleAdd 三护栏：空账号/已存在 warning 文案逐字对齐。</summary>
    void OnAddRow(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        var type = (CmbNewType.SelectedItem as ComboBoxItem)?.Tag as string == "Group"
            ? AccountType.Group : AccountType.User;
        var access = (CmbNewAccess.SelectedItem as ComboBoxItem)?.Tag as string ?? "Read";
        var msg = vm.AddRow(TxtNewAccount.Text, type, access);
        TxtNewAccount.Text = "";
        ShowResultMsg(RowMsg, msg);
        if (string.IsNullOrEmpty(msg)) RefreshDynamicVm();
    }

    void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || sender is not Button b) return;
        if (b.DataContext is SharePermissionsViewModel.PermRow row)
        {
            vm.RemoveRow(row.account);
            RefreshDynamicVm();
        }
    }

    /// <summary>TS handleSave 前 Popconfirm："确认覆盖当前权限？"。</summary>
    async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        var res = MessageBox.Show("确认覆盖当前权限？", "保存权限",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (res != MessageBoxResult.OK) return;
        string msg = await vm.SaveAsync();
        ShowResultMsg(SaveMsg, msg, fallbackBrush: (System.Windows.Media.Brush?)TryFindResource("BrushFog"));
        if (msg.Length > 0 && !msg.Contains("失败") && !msg.StartsWith("请"))
        {
            Shell.ToastHost.Show(Shell.ToastLevel.Success, $"{DrawerTitle.Text.Split('：')[0]}：{msg}");
        }
        RefreshDynamicVm();
    }

    /// <summary>
    /// v2.2.0：结果文案三态着色（此前成功/失败同为灰字——「已保存」和异常长得一模一样）。
    /// 规则：含「失败」或以「请」开头 = 错误（红）；「已」开头 = 成功（绿）；其余 = 中性（fallback）。
    /// </summary>
    void ShowResultMsg(System.Windows.Controls.TextBlock target, string msg, System.Windows.Media.Brush? fallbackBrush = null)
    {
        target.Text = msg;
        target.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        if (string.IsNullOrEmpty(msg)) return;
        string? key = msg.Contains("失败") || msg.StartsWith("请") ? "BrushDangerStrong"
            : msg.StartsWith("已") ? "BrushSuccess"
            : null;
        target.Foreground = (key is not null ? TryFindResource(key) as System.Windows.Media.Brush : null)
            ?? fallbackBrush ?? target.Foreground;
    }

    /// <summary>TS loadNtfs：ACL 读取（自动加载与「重新加载」按钮共用）。</summary>
    async Task LoadNtfsIntoViewAsync()
    {
        if (_vm is not { } vm) return;
        var msg = await vm.LoadNtfsAsync();
        NtfsGrid.ItemsSource = vm.Ntfs?.entries.ToList();
        var has = vm.Ntfs?.entries is { Count: > 0 };
        NtfsEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        NtfsEmpty.Text = msg.Length > 0 ? msg : "暂无 ACL 条目";
        if (_vm is not null) FillNtfsAccountBox();
    }

    async void OnLoadNtfs(object sender, RoutedEventArgs e) => await LoadNtfsIntoViewAsync();

    // ===== NTFS ACL 编辑（v2.1 面板解锁：icacls 通道；结果写 NtfsMsg 并重读网格） =====

    /// <summary>账号下拉预填本地用户+组（保留用户手输文本；IsEditable 语义）。</summary>
    void FillNtfsAccountBox()
    {
        var feed = WinShare.App.Users.UsersModule.UserService;
        if (feed is null || NtfsAccountBox is null) return;
        string typed = NtfsAccountBox.Text;
        _ = Task.Run(async () =>
        {
            try
            {
                var users = await feed.ListUsersAsync();
                var groups = await feed.ListGroupsAsync();
                var names = users.Select(u => u.name).Concat(groups.Select(g => g.name))
                    .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Dispatcher.BeginInvoke(() =>
                {
                    NtfsAccountBox.ItemsSource = names;
                    if (!string.IsNullOrEmpty(typed)) NtfsAccountBox.Text = typed;
                });
            }
            catch { }
        });
    }

    async void OnNtfsGrant(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        string account = NtfsAccountBox?.Text?.Trim() ?? "";
        string rights = (NtfsRightsBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "RX";
        var msg = await vm.GrantNtfsAsync(account, rights, NtfsCiBox.IsChecked == true, NtfsOiBox.IsChecked == true);
        ShowNtfsMsg(msg);
        NtfsGrid.ItemsSource = vm.Ntfs?.entries.ToList();
    }

    async void OnNtfsRevoke(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        if ((sender as FrameworkElement)?.DataContext is not NtfsAclEntry entry) return;
        if (string.IsNullOrEmpty(entry.account)) return;
        var res = MessageBox.Show($"移除「{entry.account}」在该共享路径的全部授权？", "移除确认",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;
        var msg = await vm.RevokeNtfsAsync(entry.account);
        ShowNtfsMsg(msg);
        NtfsGrid.ItemsSource = vm.Ntfs?.entries.ToList();
    }

    async void OnNtfsInheritOn(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        ShowNtfsMsg(await vm.SetNtfsInheritanceAsync(enabled: true));
        NtfsGrid.ItemsSource = vm.Ntfs?.entries.ToList();
    }

    async void OnNtfsInheritOff(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        var res = MessageBox.Show(
            "禁用继承会把从父目录继承的权限复制为显式权限（之后可单独编辑），继续？",
            "禁用继承", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (res != MessageBoxResult.Yes) return;
        ShowNtfsMsg(await vm.SetNtfsInheritanceAsync(enabled: false));
        NtfsGrid.ItemsSource = vm.Ntfs?.entries.ToList();
    }

    void ShowNtfsMsg(string msg)
    {
        if (string.IsNullOrEmpty(msg)) { NtfsMsg.Visibility = Visibility.Collapsed; return; }
        ShowResultMsg(NtfsMsg, msg, fallbackBrush: (System.Windows.Media.Brush?)TryFindResource("BrushFog"));
    }
}
