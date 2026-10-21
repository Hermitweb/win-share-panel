using System.Windows;
using System.Windows.Controls;
using WinShare.App;
using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Settings;

/// <summary>
/// 「服务器配置」页（route /settings，替换 PlaceholderPage；对齐 src/pages/Settings.tsx）。
/// 结构：页头（一键诊断 + 刷新）→ 版本适配卡 → 一键诊断卡 → 4 协议 Tab（SMB/NFS/FTP/WebDAV，
/// 各 Tab = 能力门控 + 配置预设 + 表单 + 服务控制）。
/// 纯代码构建（无 XAML），设计期/单测可直接 new。
/// </summary>
public sealed class SettingsPageView : UserControl
{
    readonly SettingsViewModel _vm;
    StackPanel? _diagnoseItems;
    StackPanel? _osDetails;

    public SettingsPageView()
    {
        var store = AppServices.SettingsStore;
        var advanced = store is null ? true : store.Load().advancedMode;
        _vm = new SettingsViewModel(advanced);
        DataContext = _vm;
        Content = BuildRoot();
        Loaded += async (_, _) => await _vm.RefreshAsync();
        // 全局刷新透传（F5 / 菜单 / 托盘 → 事件总线；对齐 TS refreshTick 失效重取）
        AppServices.Bus?.Subscribe<WinShare.App.Events.RefreshRequested>(evt => { _ = _vm.RefreshAsync(); });
    }

    FrameworkElement BuildRoot()
    {
        var outer = new DockPanel { Margin = new Thickness(16) };
        outer.Children.Add(BuildHeader());
        var content = new StackPanel();
        content.Children.Add(BuildOsCompatCard());
        content.Children.Add(BuildDiagnoseCard());
        content.Children.Add(BuildProtocolTabs());
        outer.Children.Add(new ScrollViewer { Content = content });
        return outer;
    }

    DockPanel BuildHeader()
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock
        {
            Text = "服务器配置",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = SettingsUi.Token("BrushText", "#1F2733"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(title, Dock.Left);
        row.Children.Add(title);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        // 一键诊断入口（排障时第一眼可见；服务缺席时禁用并写明原因——不留「点了没反应」的入口）
        var diagnose = SettingsUi.Button("一键诊断", async (_, _) =>
        {
            await _vm.Diagnose.RunAsync(null);
        }, primary: true);
        if (!_vm.Diagnose.Available)
        {
            diagnose.IsEnabled = false;
            diagnose.ToolTip = _vm.Diagnose.DegradedNote;
            actions.Children.Add(SettingsUi.FogText("诊断面板尚未就绪（诊断服务未注册），服务实现后此入口自动可用"));
        }
        actions.Children.Add(diagnose);
        actions.Children.Add(SettingsUi.Button("刷新", async (_, _) => await _vm.RefreshAsync()));
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        return row;
    }

    Border BuildOsCompatCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("版本适配（运行时功能探测）"));
        if (_vm.OsCompat.DegradedNote is { } note)
        {
            p.Children.Add(SettingsUi.FogText(note));
        }
        else
        {
            p.Children.Add(SettingsUi.FogText("配置页按探测结果降级（如 SMB QUIC 不支持时保存自动跳过）。"));
        }
        _osDetails = new StackPanel();
        p.Children.Add(_osDetails);
        RebuildOsCompat();
        return SettingsUi.Card(p);
    }

    void RebuildOsCompat()
    {
        var target = _osDetails;
        if (target is null) return;
        target.Children.Clear();
        var os = _vm.OsCompat.Os;
        if (os is null)
        {
            target.Children.Add(SettingsUi.FogText("OS 信息读取中…（服务缺席时保持此占位）"));
            return;
        }
        static string Bit(bool on, string name) => $"{name}：{(on ? "支持" : "不支持")}";
        target.Children.Add(SettingsUi.FogText($"{os.caption}（build {os.buildNumber}）", 0, 4));
        target.Children.Add(SettingsUi.FogText(string.Join("    ",
            Bit(os.features.smbShareModule, "SMB 共享模块"),
            Bit(os.features.nfsServerCmdlets, "NFS 服务器 Cmdlets"),
            Bit(os.features.iisAvailable, "IIS"),
            Bit(os.features.smbQuicConfig, "SMB QUIC 配置"))));
        if (_vm.OsCompat.QuicHint is { } hint)
        {
            target.Children.Add(SettingsUi.FogText(hint));
        }
    }

    Border BuildDiagnoseCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("一键诊断"));
        if (_vm.Diagnose.DegradedNote is { } note)
        {
            p.Children.Add(SettingsUi.FogText(note));
        }
        _diagnoseItems = new StackPanel();
        p.Children.Add(_diagnoseItems);
        RebuildDiagnoseItems();
        _vm.Diagnose.PropertyChanged += (_, _) => RebuildDiagnoseItems();
        _vm.Diagnose.Changed += RebuildDiagnoseItems;
        return SettingsUi.Card(p);
    }

    void RebuildDiagnoseItems()
    {
        var panel = _diagnoseItems;
        if (panel is null) return;
        panel.Children.Clear();
        var items = _vm.Diagnose.Items;
        if (items.Count == 0)
        {
            panel.Children.Add(SettingsUi.FogText(_vm.Diagnose.Running
                ? "诊断中…"
                : "尚未运行诊断（逐项容错：单项探测失败记警告，不崩整体）"));
            return;
        }
        foreach (var item in items)
        {
            var (label, color) = DiagnoseViewModel.StatusInfo(item.status);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = SettingsUi.TagColor(color),
                FontWeight = FontWeights.SemiBold,
                Width = 44,
                FontSize = 12,
            });
            row.Children.Add(new TextBlock
            {
                Text = $" {item.label}：{item.detail}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SettingsUi.Token("BrushText", "#1F2733"),
            });
            if (item.fix is { } fix)
            {
                row.Children.Add(SettingsUi.Button("修复", async (_, _) =>
                {
                    await _vm.Diagnose.ApplyFixAsync(fix, null);
                }));
            }
            panel.Children.Add(row);
        }
    }

    TabControl BuildProtocolTabs()
    {
        var tabs = new TabControl { Margin = new Thickness(0, 0, 0, 12) };
        void Add(string header, TabItem item)
        {
            item.Header = header;
            tabs.Items.Add(item);
        }
        Add("SMB", new TabItem { Content = new SmbTabView(_vm.Smb, _vm.Detection) });
        Add("NFS", new TabItem { Content = new NfsTabView(_vm.Nfs, _vm.Detection) });
        Add("FTP", new TabItem { Content = new FtpTabView(_vm.Ftp, _vm.Detection) });
        Add("WebDAV", new TabItem { Content = new WebdavTabView(_vm.Webdav, _vm.Detection) });
        return tabs;
    }
}
