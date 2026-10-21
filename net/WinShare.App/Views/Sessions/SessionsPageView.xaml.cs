using System.Windows;
using System.Windows.Controls;
using WinShare.App.Sessions;
using WinShare.App.Shell;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Sessions;

/// <summary>
/// 会话监控页（对齐 src/pages/Sessions.tsx）：协议 Tab + 刷新间隔/暂停/倒计时 +
/// 会话/打开文件表 + 单条/批量强断确认 + 新增会话托盘气泡（VM 事件 → AppShell.ShowBalloon）。
/// 构造无参（依赖从 SessionsModule 取）——设计期可实例化；挂 Loaded 后启动轮询。
/// </summary>
public partial class SessionsPageView : UserControl
{
    SessionsViewModel? _vm;

    public SessionsPageView()
    {
        InitializeComponent();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += async (_, _) =>
        {
            _vm = SessionsModule.CreateViewModel();
            _vm.Notify = (_, text) => ShowNotice(text);
            _vm.BalloonRequested += (title, body) => AppShell.ShowBalloon(title, body);
            _vm.PropertyChanged += (_, _) =>
            {
                if (Dispatcher.CheckAccess()) ApplyVm();
                else Dispatcher.BeginInvoke(ApplyVm);
            };
            await _vm.StartAsync();
            ApplyVm();
        };
        Unloaded += (_, _) => _vm?.Stop();
    }

    // =====================================================================
    // 数据到控件映射
    // =====================================================================

    void ApplyVm()
    {
        if (_vm is null) return;

        // 协议面板切换（对齐 protoTabs 分支）
        SmbPanel.Visibility = _vm.ActiveProto == Protocol.Smb ? Visibility.Visible : Visibility.Collapsed;
        NfsPanel.Visibility = _vm.ActiveProto == Protocol.Nfs ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = _vm.ActiveProto is Protocol.Ftp or Protocol.Webdav
            ? Visibility.Visible : Visibility.Collapsed;

        // SMB 子 Tab 计数（对齐「会话 (N)」/「打开文件 (N)」）
        SmbSessionsTab.Content = $"会话 ({_vm.Sessions.Count})";
        SmbFilesTab.Content = $"打开文件 ({_vm.Files.Count})";

        // 表格
        SessionGrid.ItemsSource = _vm.SessionRows;
        NfsSessionGrid.ItemsSource = _vm.SessionRows;
        FileGrid.ItemsSource = _vm.Files;
        SessionGrid.IsEnabled = !_vm.Spinning;
        FileGrid.IsEnabled = !_vm.Spinning;

        // 选中条（对齐 selectionBar）
        var sel = _vm.SelectedSessions.Count;
        SelectedCount.Text = $"已选 {sel}";
        SelectionBar.Visibility = sel > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 暂停/恢复 + 倒计时标签（TS：paused 时隐藏倒计时 Tag）
        BtnPause.Content = _vm.Paused ? "恢复" : "暂停";
        CountdownTag.Visibility = _vm.Paused ? Visibility.Collapsed : Visibility.Visible;
        CountdownText.Text = $"{_vm.Countdown}s";

        // 刷新间隔下拉同步
        SyncIntervalCombo();

        // 首轮失败提示
        var err = _vm.ErrorText;
        NoticeBar.Text = err;
        NoticeBar.Visibility = err.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SyncIntervalCombo()
    {
        if (_vm is null) return;
        for (int i = 0; i < IntervalCombo.Items.Count; i++)
        {
            if (int.TryParse((IntervalCombo.Items[i] as ComboBoxItem)!.Tag?.ToString(), out var v) && v == _vm.IntervalSec)
            {
                IntervalCombo.SelectedIndex = i;
                return;
            }
        }
    }

    // =====================================================================
    // 工具条 / Tab 交互
    // =====================================================================

    void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (IntervalCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            _vm.IntervalSec = int.TryParse(tag, out var v) ? v : 5;
        ApplyVm();
    }

    void OnPauseToggle(object sender, RoutedEventArgs e)
    {
        _vm?.TogglePauseCommand.Execute(null);
        ApplyVm();
    }

    async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.RefreshNowCommand.ExecuteAsync();
        ApplyVm();
    }

    void OnProto(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ActiveProto = ProtoNfs.IsChecked == true ? Protocol.Nfs
            : ProtoFtp.IsChecked == true ? Protocol.Ftp
            : ProtoWebdav.IsChecked == true ? Protocol.Webdav
            : Protocol.Smb;
        ApplyVm();
    }

    void OnSmbSub(object sender, RoutedEventArgs e)
    {
        // XAML 解析期（SmbSessionsTab IsChecked=True 触发）后续控件尚未创建 → 空守卫
        if (_vm is null || SmbSessionsTab is null || SessionGrid is null || FileGrid is null) return;
        var showSessions = SmbSessionsTab.IsChecked == true;
        SessionGrid.Visibility = showSessions ? Visibility.Visible : Visibility.Collapsed;
        FileGrid.Visibility = showSessions ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        // 两个协议面板共用选择（切协议时 VM 自动重置基线/已选）
        var ids = (sender as DataGrid)?.SelectedItems.Cast<object>()
            .OfType<SessionsViewModel.SessionRow>().Select(r => r.session.sessionId).ToList();
        _vm.SetSelectedSessions(ids ?? new List<string>());
        ApplyVm();
    }

    void OnClearSelection(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SetSelectedSessions(Array.Empty<string>());
        SessionGrid?.UnselectAll();
        NfsSessionGrid?.UnselectAll();
        ApplyVm();
    }

    // =====================================================================
    // 强断 / 关闭文件（确认框对齐 TS Popconfirm/modal.confirm）
    // =====================================================================

    async void OnCloseSession(object sender, RoutedEventArgs e)
    {
        if (_vm is null || GetTag(sender) is not SessionsViewModel.SessionRow row) return;
        // 对齐 Popconfirm title="断开该会话？"
        var r = MessageBox.Show("断开该会话？", "确认断开",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        await _vm.CloseOneAsync(row.session.sessionId);
        ApplyVm();
    }

    async void OnCloseFile(object sender, RoutedEventArgs e)
    {
        if (_vm is null || GetTag(sender) is not SmbOpenFile file) return;
        // 对齐 Popconfirm title="关闭该文件？"
        var r = MessageBox.Show("关闭该文件？", "确认关闭",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        await _vm.CloseFileAsync(file.fileId);
        ApplyVm();
    }

    async void OnBatchClose(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var ids = _vm.SelectedSessions.ToList();
        if (ids.Count == 0) return;
        // 对齐 modal.confirm：批量断开会话 / 将对 N 个会话执行强制断开。
        var r = MessageBox.Show($"将对 {ids.Count} 个会话执行强制断开。", "批量断开会话",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        await _vm.BatchCloseAsync(ids);
        ApplyVm();
    }

    void ShowNotice(string text)
    {
        NoticeBar.Text = text;
        NoticeBar.Visibility = Visibility.Visible;
    }

    static object? GetTag(object sender) => sender is Button b ? b.Tag : null;
}
