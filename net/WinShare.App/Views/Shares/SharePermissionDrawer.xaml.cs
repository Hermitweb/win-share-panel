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
    }

    // 1 列纵向流：区块选择器驱动内容区显隐（原 TabControl SelectedIndex 的等价面）
    void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (RbShare is null || RbNtfs is null || SecShare is null || SecNtfs is null) return;
        SecShare.Visibility = RbShare.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SecNtfs.Visibility = RbNtfs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
        RowMsg.Text = msg;
        RowMsg.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
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
        SaveMsg.Text = await vm.SaveAsync();
        RefreshDynamicVm();
    }

    /// <summary>TS loadNtfs：点了加载才读的只读 ACL（事件路径，与打开路径分离）。</summary>
    async void OnLoadNtfs(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        var msg = await vm.LoadNtfsAsync();
        NtfsGrid.ItemsSource = vm.Ntfs?.entries.ToList();
        var has = vm.Ntfs?.entries is { Count: > 0 };
        NtfsEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        NtfsEmpty.Text = msg.Length > 0 ? msg : "点击「加载」查看 NTFS ACL（只读）";
    }
}
