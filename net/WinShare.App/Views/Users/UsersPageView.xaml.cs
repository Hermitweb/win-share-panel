using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinShare.App.Events;
using WinShare.App.Users;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Users;

/// <summary>
/// 用户与权限页（对齐 src/pages/Users.tsx）：
/// 本地用户/组 CRUD、批量启停/删除/分组、权限矩阵（Deny 优先 + 导出）、NTFS 权限查看。
/// 界面逻辑全部在 UsersViewModel；本文件只做装配 + 数据到控件映射 + 模态框（TS antd 组件的 WPF 对应面）。
/// 构造无参（依赖从 UsersModule 取）——设计期可实例化；挂 Loaded 后拉数据。
/// </summary>
public partial class UsersPageView : UserControl
{
    UsersViewModel? _vm;
    IUnsubscribe? _refreshSub;

    public UsersPageView()
    {
        InitializeComponent();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += async (_, _) =>
        {
            _vm = UsersModule.CreateViewModel();
            _vm.Notify = (_, text) => ShowNotice(text);
            _vm.PropertyChanged += (_, _) =>
            {
                if (Dispatcher.CheckAccess()) ApplyVm();
                else Dispatcher.BeginInvoke(ApplyVm);
            };
            await _vm.RefreshAsync();
            ApplyVm();

            // F5 全局刷新（对齐 TS useTickEffect(refreshTick, load)；总线缺席=设计期，跳过）
            if (AppServices.Bus is { } bus)
            {
                _refreshSub = bus.Subscribe<RefreshRequested>(async _ =>
                {
                    await _vm!.RefreshAsync();
                    if (Dispatcher.CheckAccess()) ApplyVm();
                    else Dispatcher.BeginInvoke(ApplyVm);
                });
            }
        };
        Unloaded += (_, _) => _refreshSub?.Unsubscribe();
    }

    // =====================================================================
    // 数据到控件映射
    // =====================================================================

    void ApplyVm()
    {
        if (_vm is null) return;

        UsersGrid.ItemsSource = _vm.UserRows;
        GroupsGrid.ItemsSource = _vm.GroupRows;
        NtfsGrid.ItemsSource = _vm.NtfsRows;

        // 批量条（TS：选中 >0 显示）
        var selCount = _vm.SelectedUsers.Count;
        BatchCount.Text = $"已选 {selCount} 个用户";
        BatchBar.Visibility = TabUsers.IsChecked == true && selCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 矩阵
        MatrixHint.Text = _vm.MatrixNote;
        BuildMatrixGrid();
        BtnMatrixCancel.Visibility = _vm.MatrixLoading ? Visibility.Visible : Visibility.Collapsed;
        BtnMatrixReload.IsEnabled = !_vm.MatrixLoading;
        bool canExport = !_vm.MatrixLoading && _vm.MatrixShares.Count > 0;
        BtnExportCsv.IsEnabled = canExport;
        BtnExportJson.IsEnabled = canExport;
        BtnExportHtml.IsEnabled = canExport;

        // 首轮失败/服务缺席提示
        var err = _vm.ErrorText;
        NoticeBar.Text = err;
        NoticeBar.Visibility = err.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>矩阵动态列：首列共享 + 每账号一列（宽权限列头标注；单元格=Deny 优先标签）。</summary>
    void BuildMatrixGrid()
    {
        if (_vm is null) return;
        var shares = _vm.MatrixShares;
        var accounts = _vm.MatrixAccounts;

        var rows = shares.Select(s =>
        {
            var m = _vm.MatrixData.TryGetValue(s.name, out var dict) ? dict : new Dictionary<string, MatrixAccess>();
            var cells = new Dictionary<string, MatrixAccess>();
            foreach (var a in accounts)
                cells[a.name] = m.TryGetValue(a.name, out var v) ? v : MatrixAccess.None;
            return new MatrixRowView(s.name, s.path, cells);
        }).ToList();
        MatrixGrid.ItemsSource = rows;

        var headers = new[] { "共享" }.Concat(accounts.Select(a =>
            PermissionMatrix.IsWideAccount(a.name) ? $"{a.name}（宽权限）" : a.name)).Cast<object>().ToArray();
        bool sameColumns =
            MatrixGrid.Columns.Count == accounts.Count + 1
            && MatrixGrid.Columns.Cast<DataGridColumn>().Select(c => c.Header).SequenceEqual(headers);
        if (!sameColumns)
        {
            MatrixGrid.Columns.Clear();
            MatrixGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "共享",
                Binding = new Binding("ShareName"),
                Width = new DataGridLength(160, DataGridLengthUnitType.Star),
                IsReadOnly = true,
            });
            foreach (var a in accounts)
            {
                MatrixGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = PermissionMatrix.IsWideAccount(a.name) ? $"{a.name}（宽权限）" : a.name,
                    Binding = new Binding($"Cells[\"{a.name}\"]")
                    { Converter = (IValueConverter)FindResource("MatrixCell") },
                    Width = new DataGridLength(90, DataGridLengthUnitType.Pixel),
                    IsReadOnly = true,
                });
            }
        }
    }

    // =====================================================================
    // Tab 切换 / 过滤（对齐 TS 交互）
    // =====================================================================

    void OnTab(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var tab = TabGroups.IsChecked == true ? "groups"
            : TabMatrix.IsChecked == true ? "matrix"
            : TabNtfs.IsChecked == true ? "ntfs" : "users";
        UsersPanel.Visibility = tab == "users" ? Visibility.Visible : Visibility.Collapsed;
        GroupsPanel.Visibility = tab == "groups" ? Visibility.Visible : Visibility.Collapsed;
        MatrixPanel.Visibility = tab == "matrix" ? Visibility.Visible : Visibility.Collapsed;
        NtfsPanel.Visibility = tab == "ntfs" ? Visibility.Visible : Visibility.Collapsed;
        if (tab != "users") BatchBar.Visibility = Visibility.Collapsed;
        else BatchBar.Visibility = _vm.SelectedUsers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (tab == "matrix" && _vm.MatrixShares.Count == 0)
            _ = _vm.LoadMatrixAsync(); // 首次进入矩阵 Tab 加载（对齐 TS 组件挂载即加载；后续走「重新加载」）
    }

    // 过滤（搜索框 / 仅启用开关共用）：同步 VM 状态 + 重算 + 刷新数据
    async void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Keyword = SearchBox.Text;
        _vm.OnlyEnabled = OnlyEnabledChk.IsChecked == true;
        await _vm.RefreshAsync();
    }

    void OnSearchFieldChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        // 0=仅用户名 1=全部字段（对齐 TS select options；setter 内部已重算过滤行）
        _vm.SearchField = SearchFieldCombo.SelectedIndex == 1 ? UserSearchField.All : UserSearchField.Name;
    }

    // =====================================================================
    // 用户行操作（DataGrid 单元格按钮 Tag = UserRow）
    // =====================================================================

    async void OnToggleUser(object sender, RoutedEventArgs e)
    {
        if (_vm is null || GetTag<UserRow>(sender) is not { } row) return;
        await _vm.ToggleUserAsync(row.user);
        ApplyVm();
    }

    void OnEditUser(object sender, RoutedEventArgs e)
    {
        if (_vm is null || GetTag<UserRow>(sender) is not { } row) return;
        var dlg = new UserEditDialog(row.user, _vm) { Owner = Window.GetWindow(this) };
        dlg.Closed += (_, _) =>
        {
            if (dlg.Operated)
                _ = RefreshGrids();
        };
        dlg.Show(); // 对齐 antd Drawer（非模态；保存经 VM 走服务）
    }

    void OnDeleteUser(object sender, RoutedEventArgs e)
    {
        if (_vm is null || GetTag<UserRow>(sender) is not { } row) return;
        // 对齐 TS modal.confirm：删除用户 {name}？此操作不可恢复，与该用户关联的共享权限将失效。
        var r = MessageBox.Show($"删除用户 {row.user.name}？\n此操作不可恢复，与该用户关联的共享权限将失效。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Yes)
            _ = _vm.DeleteUserAsync(row.user.name).ContinueWith(_ => RefreshGrids());
    }

    async void OnBatchEnable(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var r = MessageBox.Show($"批量启用 {_vm.SelectedUsers.Count} 个用户？", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        await _vm.BatchEnableAsync();
        ApplyVm();
    }

    async void OnBatchDisable(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var r = MessageBox.Show($"批量禁用 {_vm.SelectedUsers.Count} 个用户？", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        await _vm.BatchDisableAsync();
        ApplyVm();
    }

    void OnBatchDelete(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var r = MessageBox.Show($"批量删除 {_vm.SelectedUsers.Count} 个用户？\n此操作不可恢复，与该用户关联的共享权限将失效。",
            "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        _ = _vm.BatchDeleteAsync().ContinueWith(_ => RefreshGrids());
    }

    void OnBatchAssign(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.SelectedUsers.Count == 0) return;
        var dlg = new BatchAssignDialog(_vm.SelectedUsers.ToList(), _vm.Groups.ToList())
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.SelectedGroup.Length > 0)
            _ = _vm.BatchAssignGroupAsync(dlg.SelectedGroup).ContinueWith(_ => RefreshGrids());
    }

    void OnBatchClear(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SetSelectedUsers(Array.Empty<string>());
        UsersGrid?.UnselectAll();
        ApplyVm();
    }

    void OnUsersSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        var names = UsersGrid.SelectedItems.Cast<object>().OfType<UserRow>().Select(r => r.user.name).ToList();
        _vm.SetSelectedUsers(names);
        BatchCount.Text = $"已选 {names.Count} 个用户";
        BatchBar.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================
    // 组操作
    // =====================================================================

    void OnCreateUser(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        // 对话框内部完成创建 + 组分配（对齐 UserCreateModal）；成功 → 刷新
        var dlg = new UserCreateDialog(_vm) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            _ = RefreshGrids();
    }

    void OnCreateGroup(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        // 对话框内部已调用 vm.CreateGroupAsync 并置 Operated（成功才 DialogResult=true）
        var dlg = new GroupCreateDialog(_vm) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            _ = RefreshGrids();
    }

    void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        if (_vm is null || GetTag<GroupRow>(sender) is not { } grow) return;
        // 对齐 TS：删除组 {name}？此操作不可恢复。组成员将失去该组的所有权限。
        var r = MessageBox.Show($"删除组 {grow.group.name}？\n此操作不可恢复。组成员将失去该组的所有权限。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Yes)
            _ = _vm.DeleteGroupAsync(grow.group.name).ContinueWith(_ => RefreshGrids());
    }

    void OnManageGroup(object sender, RoutedEventArgs e)
    {
        if (_vm is null || GetTag<GroupRow>(sender) is not { } grow) return;
        var dlg = new GroupManageDialog(grow.group, _vm) { Owner = Window.GetWindow(this) };
        dlg.Closed += (_, _) =>
        {
            if (dlg.Operated)
                _ = RefreshGrids();
        };
        dlg.Show();
    }

    // =====================================================================
    // 矩阵 / NTFS / 导出 / 刷新
    // =====================================================================

    async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.RefreshAsync();
        ApplyVm();
    }

    async void OnMatrixReload(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.LoadMatrixCommand.ExecuteAsync();
    }

    void OnMatrixCancel(object sender, RoutedEventArgs e) => _vm?.CancelMatrix();

    async void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.ExportMatrixCsvCommand.ExecuteAsync();
        ApplyVm();
    }

    async void OnExportJson(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.ExportMatrixJsonCommand.ExecuteAsync();
        ApplyVm();
    }

    async void OnExportHtml(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.ExportMatrixHtmlCommand.ExecuteAsync();
        ApplyVm();
    }

    async void OnNtfsView(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var path = NtfsPathBox?.Text.Trim() ?? "";
        if (path.Length == 0)
        {
            ShowNotice("请输入要查看的 NTFS 路径");
            return;
        }
        await _vm.ViewNtfsAsync(path);
        ApplyVm();
    }

    // =====================================================================
    // 小工具
    // =====================================================================

    void ShowNotice(string text)
    {
        NoticeBar.Text = text;
        NoticeBar.Visibility = Visibility.Visible;
        StatusTip.Text = text;
        StatusTip.Visibility = Visibility.Visible;
    }

    /// <summary>VM 后台任务完成 → UI 线程刷新全部面板（Dispatcher 兜底）。</summary>
    async Task RefreshGrids()
    {
        if (Dispatcher.CheckAccess())
        {
            if (_vm is not null)
            {
                await _vm.RefreshAsync();
                ApplyVm();
            }
            return;
        }
        await Dispatcher.InvokeAsync(async () =>
        {
            if (_vm is not null)
            {
                await _vm.RefreshAsync();
                ApplyVm();
            }
        });
    }

    static T? GetTag<T>(object sender) where T : class =>
        sender is Button b && b.Tag is T t ? t : null;

    /// <summary>矩阵行视图（共享 + 每账号单元格；列绑定 Cells["账号"] + 标签转换器）。</summary>
    public sealed class MatrixRowView
    {
        public string ShareName { get; }
        public string Path { get; }
        public Dictionary<string, MatrixAccess> Cells { get; }
        public MatrixRowView(string shareName, string path, Dictionary<string, MatrixAccess> cells)
        {
            ShareName = shareName;
            Path = path;
            Cells = cells;
        }
    }
}

/// <summary>矩阵单元格 → 中文标签（MatrixAccess → 完全/更改/只读/拒绝/-）。</summary>
public sealed class MatrixCellConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is MatrixAccess a ? PermissionMatrix.AccessLabel(a) : "-";

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
