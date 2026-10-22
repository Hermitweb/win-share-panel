using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input; // T3d 最小修复（原缺失，MouseButtonEventArgs 未解析）
using WinShare.App.Shares;

namespace WinShare.App.Views.Shares;

/// <summary>
/// 共享管理页（对齐 src/pages/Shares.tsx）：界面逻辑全部在 SharesViewModel；
/// 本文件只做视图装配 + 数据到控件映射 + 拖拽/选择等控件事件转发。
/// 新建向导/编辑为模式窗口；详情/权限为页内抽屉（遮罩 + 右栏）。
/// 构造不触碰 AppServices（设计期实例化安全）；Loaded 后自装配。
/// </summary>
public partial class SharesPageView : UserControl
{
    SharesViewModel? _vm;
    Window? _wizard;
    Window? _edit;
    bool _wired;

    public SharesPageView()
    {
        InitializeComponent();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_wired) return;
        _wired = true;

        // 契约服务槽位（SharesServices；Core 服务实现落地后在组合根设置，缺席走占位降级）
        var feed = new ContractSharesFeed(
            share: SharesServices.Share,
            preset: SharesServices.Preset,
            user: SharesServices.User,
            disk: SharesServices.Disk,
            system: AppServices.ResolveSystemService(),
            protocol: SharesServices.Protocol);
        _vm = new SharesViewModel(
            feed,
            AppServices.SettingsStore,
            AppServices.Bus,
            SharesServices.Share,
            SharesServices.Preset,
            SharesServices.Protocol,
            AppServices.ResolveSystemService(),
            SharesServices.User,
            SharesServices.Disk,
            AppServices.SaveFileWithDialogAsync,
            OpenImportFileAsync);
        DataContext = _vm;
        Grid.DataContext = _vm;

        _vm.PropertyChanged += OnVmChanged;
        _vm.DetailOpened += OpenDetailDrawer;
        _vm.EditOpened += OpenEditWindow;
        _vm.PermOpened += OpenPermDrawer;

        // 跨页导航定位（对齐 TS navigate(to,{state:attention})：TakeAttention 一次性取走）
        var attention = AppServices.Navigation.TakeAttention();
        if (!string.IsNullOrWhiteSpace(attention)) _vm.Keyword = attention;

        await _vm.LoadAsync();
        ApplyVm();
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmChanged;
        _vm.Dispose();
        _vm = null;
        ClosePoppedWindows();
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ApplyVm);

    void ClosePoppedWindows()
    {
        if (_wizard is { IsLoaded: true }) _wizard.Close();
        if (_edit is { IsLoaded: true }) _edit.Close();
        _wizard = null;
        _edit = null;
    }

    // ===== VM → 控件映射 =====
    void ApplyVm()
    {
        if (_vm is null) return;
        Grid.ItemsSource = _vm.Rows;

        // 批量工具栏
        BatchBar.Visibility = _vm.HasSelection ? Visibility.Visible : Visibility.Collapsed;
        TxtSelectedCount.Text = $"已选 {_vm.SelectedCount}";

        // 协议 Tab 指示 + 未装橙点
        TabAll.IsChecked = _vm.ActiveProtocol == "all";
        TabSmb.IsChecked = _vm.ActiveProtocol == "smb";
        TabNfsRadio.IsChecked = _vm.ActiveProtocol == "nfs";
        TabFtpRadio.IsChecked = _vm.ActiveProtocol == "ftp";
        TabWebdavRadio.IsChecked = _vm.ActiveProtocol == "webdav";
        TabNfsDot.Visibility = TabDotVisible("nfs") ? Visibility.Visible : Visibility.Collapsed;
        TabFtpDot.Visibility = TabDotVisible("ftp") ? Visibility.Visible : Visibility.Collapsed;
        TabWebdavDot.Visibility = TabDotVisible("webdav") ? Visibility.Visible : Visibility.Collapsed;

        // 未安装空态（TS isCurrentProtoInstalled 为 false 时显示 Empty + 一键安装）
        var uninstalled = _vm.CurrentProtoUninstalled;
        Grid.Visibility = uninstalled ? Visibility.Collapsed : Visibility.Visible;
        EmptyUninstalled.Visibility = uninstalled ? Visibility.Visible : Visibility.Collapsed;
        if (uninstalled)
        {
            var proto = _vm.ActiveProtocol;
            EmptyTitle.Text = $"{proto.ToUpperInvariant()} 协议未安装";
            EmptyHint.Text = _vm.InstallHint;
            var badges = _vm.UninstalledBannerItems
                .FirstOrDefault(i => i.Protocol == ProtocolOf(proto))?.Badges
                ?? Array.Empty<(string, string)>();
            var badgePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            SharesViewsConverters.BuildBadges(badgePanel, badges);
            EmptyBadges.Children.Clear();
            EmptyBadges.Children.Add(badgePanel);
            BtnInstall.IsEnabled = !_vm.Installing;
        }

        // 未安装协议引导横幅（TS ProtocolCapabilityBanner：选中「全部」列全量未装项）
        var bannerItems = _vm.UninstalledBannerItems;
        BannerPanel.Visibility = bannerItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BannerPanel.Children.Clear();
        foreach (var item in bannerItems)
        {
            // 1 列纵向流：每个未安装协议项独立一列（协议名 / 提示 / 徽标 / 安装按钮自上而下）
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock
            {
                Text = item.Protocol.ToString().ToUpperInvariant(),
                FontWeight = FontWeights.SemiBold,
            });
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Hint) ? "未安装" : item.Hint,
                FontSize = 12,
                Foreground = Brushes.DimGray,
            });
            var bp = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            SharesViewsConverters.BuildBadges(bp, item.Badges);
            row.Children.Add(bp);
            if (item.HasInstallCommand)
            {
                var proto = item.Protocol;
                var btn = new Button
                {
                    Content = "安装",
                    FontSize = 12,
                    Padding = new Thickness(10, 2, 10, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                btn.Click += async (_, _) => { if (_vm is not null) await _vm.InstallProtocolAsync(proto); };
                row.Children.Add(btn);
            }
            BannerPanel.Children.Add(row);
        }

        // 状态提示（TS message.success/warning 的页面内等价面）
        var tip = _vm.StatusMsg;
        StatusTip.Text = tip;
        StatusTip.Visibility = string.IsNullOrEmpty(tip) ? Visibility.Collapsed : Visibility.Visible;
        StatusTip.Foreground = _vm.StatusIsWarning
            ? new SolidColorBrush(Color.FromRgb(0xD4, 0x8B, 0x0B))
            : new SolidColorBrush(Color.FromRgb(0x26, 0x75, 0x0F));

        LoadingTip.Visibility = _vm.Loading && _vm.Hydrated ? Visibility.Visible : Visibility.Collapsed;
    }

    static WinShare.Core.Contracts.Protocol ProtocolOf(string proto) =>
        proto switch
        {
            "nfs" => WinShare.Core.Contracts.Protocol.Nfs,
            "ftp" => WinShare.Core.Contracts.Protocol.Ftp,
            "webdav" => WinShare.Core.Contracts.Protocol.Webdav,
            _ => WinShare.Core.Contracts.Protocol.Smb,
        };

    bool TabDotVisible(string proto)
    {
        if (_vm is null) return false;
        var cap = _vm.CapOf(ProtocolOf(proto));
        return cap is { installed: false };
    }

    // ===== 工具栏按钮（转发 VM） =====
    async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.LoadAsync();
        ApplyVm();
    }

    async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.ExportConfigAsync();
        ApplyVm();
    }

    async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.ImportConfigAsync();
        ApplyVm();
    }

    async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.OpenCreateWizardAsync();
        if (_vm.CreateWizardOpen) OpenWizardWindow();
    }

    /// <summary>导入文件选择（OpenFileDialog；取消返回 null = 不导入）。</summary>
    async Task<string?> OpenImportFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON 文件|*.json",
            Title = "导入共享配置（仅 SMB）",
        };
        if (dlg.ShowDialog() != true) return null;
        try
        {
            return await System.IO.File.ReadAllTextAsync(dlg.FileName);
        }
        catch (System.Exception ex)
        {
            throw new System.IO.IOException($"读取导入文件失败：{ex.Message}", ex);
        }
    }

    // ===== 搜索/筛选 =====
    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Keyword = TxtSearch.Text;
        ApplyVm();
    }

    void OnOnlyConnected(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.OnlyConnected = ChkOnlyConnected.IsChecked == true;
        ApplyVm();
    }

    void OnProtocolTab(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var proto = TabSmb.IsChecked == true ? "smb"
            : TabNfsRadio.IsChecked == true ? "nfs"
            : TabFtpRadio.IsChecked == true ? "ftp"
            : TabWebdavRadio.IsChecked == true ? "webdav"
            : "all";
        if (proto == _vm.ActiveProtocol) return;
        _vm.ActiveProtocol = proto; // 内部 LoadAsync
        ApplyVm();
    }

    // ===== 批量工具栏（TS confirmBatchDelete / requestShareToggle 确认语义） =====
    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SetSelection(Grid.SelectedItems.OfType<ShareRowVm>().Select(r => r.Key));
        ApplyVm();
    }

    void OnClearSelection(object sender, RoutedEventArgs e)
    {
        _vm?.ClearSelection();
        Grid.UnselectAll();
        ApplyVm();
    }

    void OnBatchToggle(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var n = _vm.SelectedCount;
        var res = System.Windows.MessageBox.Show(
            $"将对 {n} 个共享切换启用/禁用状态。",
            "批量启停切换", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (res == MessageBoxResult.Cancel) return;
        _ = _vm.ExecuteBatchToggleAsync().ContinueWith(_ => Dispatcher.BeginInvoke(ApplyVm));
    }

    void OnBatchDelete(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var n = _vm.SelectedCount;
        var res = System.Windows.MessageBox.Show(
            $"将对 {n} 个共享执行删除。\n\n{_vm.BatchDeleteImpactText}\n\n{_vm.BatchDeleteUndoHint}",
            "批量删除共享", MessageBoxButton.OKCancel,
            _vm.BatchDeleteImpactDanger ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (res == MessageBoxResult.Cancel) return;
        _ = _vm.ExecuteBatchDeleteAsync().ContinueWith(_ => Dispatcher.BeginInvoke(ApplyVm));
    }

    // ===== 行内操作（TS actions 列按钮） =====
    void OnRowDetail(object sender, RoutedEventArgs e) => _vm?.DetailCommand.Execute(((Button)sender).Tag!);
    void OnRowToggle(object sender, RoutedEventArgs e) => _vm?.ToggleCommand.Execute(((Button)sender).Tag!);
    void OnRowEdit(object sender, RoutedEventArgs e) => _vm?.EditCommand.Execute(((Button)sender).Tag!);
    void OnRowPerm(object sender, RoutedEventArgs e) => _vm?.PermCommand.Execute(((Button)sender).Tag!);

    void OnRowDelete(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button b || b.Tag is not ShareRowVm row) return;
        // TS Popconfirm：影响文案 + 撤销提示（单共享版）
        var (impact, danger) = ShareDeleteImpact.DeleteImpactText(new[] { row.Share });
        var res = System.Windows.MessageBox.Show(
            $"确认删除该共享？\n\n{impact}\n\n{ShareDeleteImpact.UndoHintText(new[] { row.Share })}",
            "删除共享", MessageBoxButton.OKCancel,
            danger ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (res == MessageBoxResult.Cancel) return;
        _vm.DeleteCommand.Execute(row);
    }

    void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _ = _vm.InstallProtocolAsync(ProtocolOf(_vm.ActiveProtocol))
            .ContinueWith(_ => Dispatcher.BeginInvoke(ApplyVm));
    }

    // ===== 拖拽创建（TS handleDrop：拖入文件夹 → 填路径带名 → 开向导） =====
    void OnDragOver(object sender, DragEventArgs e)
    {
        // WPF IDataObject 无 GetDataFormats；用 GetDataPresent 判 FileDrop
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm is null) return;
        // 文件夹拖入（FileDrop）：取路径 → 填表单带名 → 开向导（TS handleDrop）
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            _vm.DropFolders(paths);
            if (_vm.CreateWizardOpen) OpenWizardWindow();
        }
    }

    // ===== 子视图（向导窗口 / 编辑窗口 / 详情与权限抽屉） =====
    void OpenWizardWindow()
    {
        if (_vm is null) return;
        if (_wizard is not null) { _wizard.Activate(); return; }
        _wizard = new ShareCreateWizardWindow(_vm)
        {
            Owner = Window.GetWindow(this) ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(),
            Title = "新建共享",
        };
        _wizard.Show();
    }

    void OpenEditWindow()
    {
        if (_vm is null || _vm.EditForm is not { } form) return;
        if (_edit is not null) { _edit.Activate(); return; }
        _edit = new ShareEditWindow(_vm, form) { Owner = Window.GetWindow(this) };
        _edit.Show();
    }

    void OpenDetailDrawer()
    {
        // 抽屉常驻宿主 Grid（XAML 静态声明），这里只换数据 + 打开遮罩：
        // 不 new 控件、不跨容器搬 Children（WPF 逻辑父唯一，见 R 模式 A/B）。
        if (_vm is null || _vm.DetailTarget is not { } row) return;
        DetailDrawer.SetShare(row.Share, _vm.MakeDetailVm(row.Share));
        DetailHost.Visibility = Visibility.Visible;
    }

    void OpenPermDrawer()
    {
        if (_vm is null || _vm.PermShare is not { } share) return;
        PermDrawer.SetShare(share, _vm.MakePermissionVm(share));
        PermHost.Visibility = Visibility.Visible;
    }

    // ===== 快捷键（对齐 Shares.tsx hotkey：Ctrl+N 新建 / Del 批量删除 / Space 批量启停） =====
    // 页面自身挂 PreviewKeyDown：焦点落在搜索框/表格上时隧道事件同样到达，
    // 因此输入中不吞字符（TextBox 自身会标记 Handled；这里只在非输入控件命中时动作）。
    void OnPageKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_vm is null) return;
        var shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)
            == System.Windows.Input.ModifierKeys.Control;
        if (e.Key == Key.N && shift)
        {
            OnCreate(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        // 只处理页面级快捷键；输入控件里的按键（含空格选字）不拦
        if (TxtSearch.IsKeyboardFocusWithin || TxtSearch.IsFocused) return;
        if (e.Key == Key.Delete && _vm.HasSelection)
        {
            OnBatchDelete(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Space && _vm.HasSelection)
        {
            OnBatchToggle(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    void OnDetailBackdrop(object sender, MouseButtonEventArgs e) => DetailHost.Visibility = Visibility.Collapsed;
    void OnPermBackdrop(object sender, MouseButtonEventArgs e) => PermHost.Visibility = Visibility.Collapsed;
}
