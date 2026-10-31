using System.Windows;
using System.Windows.Controls;
using WinShare.App;
using WinShare.App.Events;
using WinShare.App.Settings;
using WinShare.App.Shell;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Settings;

/// <summary>
/// 「服务器配置」页（route /settings，替换 PlaceholderPage；对齐 src/pages/Settings.tsx）。
/// 纵向单列流：页头（一键诊断 + 刷新按钮行保留横向右对齐）→ 版本适配卡 → 一键诊断卡
/// → 6 个板块卡（SMB/NFS/FTP/WebDAV/权限模板/快照历史）自上而下全展开，无页签、无折叠。
/// 纯代码构建（无 XAML），设计期/单测可直接 new。
/// </summary>
public sealed class SettingsPageView : UserControl
{
    readonly SettingsViewModel _vm;
    StackPanel? _diagnoseItems;
    StackPanel? _osDetails;
    IUnsubscribe? _refreshSub;

    public SettingsPageView()
    {
        var store = AppServices.SettingsStore;
        var advanced = store is null ? true : store.Load().advancedMode;
        _vm = new SettingsViewModel(advanced);
        DataContext = _vm;
        WinShare.Core.Lib.Logger.Info("main", "[perf] SettingsPageView 构造开始");
        Content = BuildRoot();
        WinShare.Core.Lib.Logger.Info("main", "[perf] SettingsPageView 构造完成");
        Loaded += async (_, _) =>
        {
            if (!PageRefreshThrottle.ShouldRefresh("settings")) return;
            WinShare.Core.Lib.Logger.Info("main", "[perf] SettingsPageView Loaded → RefreshAsync 开始");
            await _vm.RefreshAsync();
            WinShare.Core.Lib.Logger.Info("main", "[perf] SettingsPageView RefreshAsync 完成");
        };
        // 全局刷新透传（F5 / 菜单 / 托盘 → 事件总线；对齐 TS refreshTick 失效重取）
        _refreshSub = AppServices.Bus?.Subscribe<WinShare.App.Events.RefreshRequested>(evt => { _ = _vm.RefreshAsync(); });
        // 页面卸载必须退订：否则切走后这个实例仍在响应全局刷新 → 攒下「幽灵页面」拖慢 UI
        Unloaded += (_, _) => _refreshSub?.Unsubscribe();
    }

    FrameworkElement BuildRoot()
    {
        var outer = new DockPanel { Margin = new Thickness(20) };
        // 页头必须显式停靠 Top：DockPanel 里未指定 Dock 的子元素默认按 Left 处理，
        // 页头会被压成左侧窄列 → 标题竖排换行（「服务配置」四个字竖着显示）。
        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        outer.Children.Add(header);
        var content = new StackPanel();
        content.Children.Add(BuildOsCompatCard());
        content.Children.Add(BuildDiagnoseCard());
        content.Children.Add(BuildProtocolSections());
        // 外层不再套 ScrollViewer：页签自己要拿到有限高度才能内部滚动，
        // 套在 ScrollViewer 里会得到无限高度 → 页签不滚动且无限拉长。
        outer.Children.Add(content);
        return outer;
    }

    DockPanel BuildHeader()
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var title = SettingsUi.PageTitle("Nav.Settings");
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
        // 异步 LoadAsync 完成后必须重绘：否则界面永远停在「OS 信息读取中…」（契约服务已接但仍显示占位）
        _vm.OsCompat.PropertyChanged += (_, _) => RebuildOsCompat();
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
        // 纵向单列：每个能力位独立一行（原为空格拼接的横排单行）
        foreach (var bit in new[]
                 {
                     Bit(os.features.smbShareModule, "SMB 共享模块"),
                     Bit(os.features.nfsServerCmdlets, "NFS 服务器 Cmdlets"),
                     Bit(os.features.iisAvailable, "IIS"),
                     Bit(os.features.smbQuicConfig, "SMB QUIC 配置"),
                 })
        {
            target.Children.Add(SettingsUi.FogText(bit));
        }
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
            // 纵向单列：状态词 / 明细 / 修复按钮各自独立成行
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = SettingsUi.TagColor(color),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{item.label}：{item.detail}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SettingsUi.Token("BrushText", "#1F2733"),
                Margin = new Thickness(0, 2, 0, 2),
            });
            if (item.fix is { } fix)
            {
                var repair = SettingsUi.Button("修复", async (_, _) =>
                {
                    await _vm.Diagnose.ApplyFixAsync(fix, null);
                });
                repair.HorizontalAlignment = HorizontalAlignment.Left;
                row.Children.Add(repair);
            }
            panel.Children.Add(row);
        }
    }

    /// <summary>
    /// 6 个协议板块纵向全展开成一列（原 TabControl 取消）：SMB → NFS → FTP → WebDAV →
    /// 权限模板 → 快照历史）。
    /// </summary>
    FrameworkElement BuildProtocolSections()
    {
        // 六页签：原先 4 个协议的表单全部纵向堆出来 —— 实测 329 个控件、页面高 5415px（约 5 屏），
        // 绝大部分内容在折叠线以下看不到。改页签后同一时刻只有当前页签的内容进入视觉树。
        // GlassTabItem 样式已在「应用设置」页验证过，安全。
        var tabs = new TabControl { Margin = new Thickness(0, 0, 0, 12) };
        var tabItemStyle = SettingsUi.Style("GlassTabItem");
        TabItem MakeTab(string headerText, UserControl section)
        {
            var inner = new StackPanel();
            inner.Children.Add(SettingsUi.CardTitle(headerText));
            inner.Children.Add(section);
            var t = new TabItem
            {
                Header = headerText,
                Content = new ScrollViewer
                {
                    Content = SettingsUi.Card(inner),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            };
            if (tabItemStyle is not null) t.Style = tabItemStyle;
            return t;
        }

        tabs.Items.Add(MakeTab("SMB", new SmbTabView(_vm.Smb, _vm.Detection)));
        tabs.Items.Add(MakeTab("NFS", new NfsTabView(_vm.Nfs, _vm.Detection)));
        tabs.Items.Add(MakeTab("FTP", new FtpTabView(_vm.Ftp, _vm.Detection)));
        tabs.Items.Add(MakeTab("WebDAV", new WebdavTabView(_vm.Webdav, _vm.Detection)));
        tabs.Items.Add(MakeTab("权限模板", new PresetTabView()));
        tabs.Items.Add(MakeTab("快照历史", new SnapshotTabView()));
        return tabs;
    }

    /// <summary>板块卡：SettingsUi.Card 卡壳 + 板块名卡标题 + 板块内容。</summary>
    Border ProtocolSectionCard(string sectionTitle, UserControl section)
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.CardTitle(sectionTitle));
        p.Children.Add(section);
        return SettingsUi.Card(p);
    }
}
