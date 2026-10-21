using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WinShare.App;
using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Settings;

/// <summary>
/// 「应用设置」页（route /app-settings，替换 PlaceholderPage；对齐 src/pages/AppSettings.tsx +
/// AppSettingsCards.tsx + UpdateChecker.tsx + SettingsTransferCard.tsx + LogPanels.tsx）。
/// 页面结构（与 TS 版一致）：关于 → 检查更新 → 备份与恢复（设置导入导出）→ 偏好与运维 → 日志。
/// G2「UAC 重启入口」落在关于卡；G5「协议已装/未装判定」的数据面在 /settings 页各协议 Tab。
/// 纯代码构建（无 XAML），设计期/单测可直接 new。
/// </summary>
public sealed class AppSettingsPageView : UserControl
{
    readonly AppSettingsViewModel _vm;
    StackPanel? _usageRadios;
    StackPanel? _autostartRow;
    TextBlock? _diskHint;
    StackPanel? _firewallRows;
    StackPanel? _securityPanel;
    StackPanel? _auditPanel;
    TextBox? _appLogBox;
    ComboBox? _logFilter;

    public AppSettingsPageView()
    {
        var deps = new SettingsPageDependencies
        {
            // 设计期/单测兜底：组合根未装配时用默认目录 Store（不触文件写入即实例化）
            Store = AppServices.SettingsStore ?? new WinShare.App.State.AppSettingsStore(),
            ApplyTheme = m => AppServices.Theme?.Apply(m, persist: false),
            PublishThemeChanged = () =>
            {
                var s = AppServices.SettingsStore.Load().theme;
                AppServices.Bus?.Publish(new Events.ThemeChanged(s));
            },
        };
        _vm = new AppSettingsViewModel(deps, openExternal: OpenExternal);
        DataContext = _vm;
        Content = BuildRoot();
        Loaded += async (_, _) => await _vm.RefreshAsync();
        // 全局刷新透传（F5 / 菜单 / 托盘 → 事件总线；对齐 TS refreshTick）
        AppServices.Bus?.Subscribe<WinShare.App.Events.RefreshRequested>(evt => { _ = _vm.RefreshAsync(); });
    }

    static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 浏览器打不开 → 视图卡片显示 URL 文本（用户可手动复制）
        }
    }

    FrameworkElement BuildRoot()
    {
        var p = new StackPanel { Margin = new Thickness(16) };
        p.Children.Add(new TextBlock
        {
            Text = "应用设置",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = SettingsUi.Token("BrushText", "#1F2733"),
            Margin = new Thickness(0, 0, 0, 8),
        });
        p.Children.Add(BuildAboutCard());
        p.Children.Add(BuildUpdateCard());
        p.Children.Add(BuildTransferCard());

        p.Children.Add(SectionHeader("偏好与运维"));
        p.Children.Add(BuildUsageModeCard());
        p.Children.Add(BuildAutoStartCard());
        p.Children.Add(BuildAlertRulesCard());
        p.Children.Add(BuildFirewallCard());
        p.Children.Add(BuildSecurityCard());

        p.Children.Add(SectionHeader("日志"));
        p.Children.Add(BuildAuditCard());
        p.Children.Add(BuildAppLogCard());
        return new ScrollViewer { Content = p };
    }

    static TextBlock SectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = SettingsUi.Token("BrushText", "#1F2733"),
            Margin = new Thickness(0, 8, 0, 8),
        };
    }

    // ===== 关于卡（含 G2：UAC 重启入口——系统服务缺席时禁用并写明原因） =====

    Border BuildAboutCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("关于",
            SettingsUi.Button("查看 releases", (_, _) => OpenExternal(_vm.ReleasesUrl))));
        var rows = new StackPanel();
        p.Children.Add(rows);
        RebuildAboutRows(rows);
        return SettingsUi.Card(p);
    }

    void RebuildAboutRows(StackPanel rows)
    {
        rows.Children.Clear();
        // 版本号（.NET 桌面版替代 TS 的 v{version} 构建于 platform/arch；运行时行替代 Electron 三值）
        var arch = Environment.Is64BitProcess ? "x64" : "x86";
        rows.Children.Add(AboutRow("版本号",
            $"v{_vm.Version}　构建于 Windows / {arch}"));
        rows.Children.Add(AboutRow("项目地址", _vm.ProjectUrl, copyable: true));
        rows.Children.Add(AboutRow("问题反馈", _vm.IssuesUrl, open: true));
        rows.Children.Add(AboutRow("下载页", _vm.ReleasesUrl, open: true));
        rows.Children.Add(AboutRow("运行时", _vm.RuntimeDescription));

        // G2：UAC 重启入口（T3a 遗留缺口落点；ISystemService 缺席 → 禁用 + 写明原因）
        var sys = SettingsServiceSlots.SystemService;
        var relaunch = SettingsUi.Button("以管理员身份重启", async (_, _) =>
        {
            if (sys is null) return;
            try { await sys.RelaunchAsAdminAsync(); }
            catch (Exception e) { MessageBox.Show($"UAC 重启失败：{e.Message}"); }
        });
        if (sys is null)
        {
            relaunch.IsEnabled = false;
            relaunch.ToolTip = "系统服务未注册（等待 Core 服务层实现）：UAC 重启暂不可用";
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = "UAC 重启",
            Width = 110,
            Foreground = SettingsUi.Token("BrushText", "#1F2733"),
        });
        row.Children.Add(relaunch);
        rows.Children.Add(row);

        rows.Children.Add(SettingsUi.FogText("本项目为 Windows 文件夹共享控制面板，需以管理员身份运行；不会自动下载或安装更新。", 6, 0));
    }

    StackPanel AboutRow(string label, string value, bool copyable = false, bool open = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 110,
            Foreground = SettingsUi.Token("BrushText", "#1F2733"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top,
        });
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = SettingsUi.Token("BrushPrimary", "#1677FF"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 380,
            ToolTip = value,
        });
        if (copyable)
        {
            right.Children.Add(SettingsUi.Button("复制", (_, _) =>
            {
                try
                {
                    Clipboard.SetText(value);
                }
                catch
                {
                    // 剪贴板被占用时忽略（与 TS execCommand 兜底等价的最小面）
                }
            }));
        }
        if (open)
        {
            right.Children.Add(SettingsUi.Button("在浏览器打开", (_, _) => OpenExternal(value)));
        }
        row.Children.Add(right);
        return row;
    }

    // ===== 检查更新卡（对齐 UpdateChecker.tsx：失败不得显示成「已是最新」） =====

    Border BuildUpdateCard()
    {
        var p = new StackPanel();
        var strip = SettingsUi.MessageStrip(_vm.Update);
        p.Children.Add(SettingsUi.HeaderRow("检查更新",
            SettingsUi.Button("打开下载页", async (_, _) => await _vm.Update.OpenReleaseAsync()),
            SettingsUi.Button("检查更新", async (_, _) => await _vm.Update.CheckAsync(), primary: true)));
        var status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SettingsUi.Token("BrushFog", "#731F2733"),
        };
        void Update()
        {
            var r = _vm.Update.Result;
            var err = _vm.Update.ErrorText;
            if (err is not null)
            {
                status.Text = $"操作失败：{err}";
                status.Foreground = SettingsUi.TagColor("red");
            }
            else if (r is null)
            {
                status.Text = "尚未检查。点击「检查更新」查询 GitHub Releases 上的最新版本；只检查，不自动下载安装。";
                status.Foreground = SettingsUi.Token("BrushFog", "#731F2733");
            }
            else if (r.unavailable)
            {
                status.Text = $"检查更新失败：{r.reason}。可直接打开下载页手动查看最新版本；或在能访问 GitHub 的网络/代理下重试。";
                status.Foreground = SettingsUi.TagColor("orange");
            }
            else
            {
                status.Text = $"当前版本 {r.current}　最新版本 {r.latest}　{(r.hasUpdate ? "有新版本" : "已是最新")}"
                    + (r.hasUpdate ? "。本应用不会自动下载或安装更新，请从下载页获取安装包后手动覆盖安装。" : "");
                status.Foreground = SettingsUi.TagColor(r.hasUpdate ? "green" : "gray");
            }
        }
        p.Children.Add(status);
        p.Children.Add(strip);
        _vm.Update.PropertyChanged += (_, _) => Update();
        Update();
        return SettingsUi.Card(p);
    }

    // ===== 设置整体导入导出卡（对齐 SettingsTransferCard.tsx + transfer.ts 边界文案） =====

    Border BuildTransferCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("设置整体导入导出",
            SettingsUi.Button("导入设置", (_, _) => DoImportSettings()),
            SettingsUi.Button("导出设置", (_, _) => DoExportSettings(), primary: true)));
        var msg = SettingsUi.FogText("", 0, 4);
        p.Children.Add(msg);
        p.Children.Add(SettingsUi.FogText(
            "导出内容：主题、新手/专家模式、置顶共享、告警规则、开机自启意图。\n" +
            "不含共享配置、权限模板、操作台账与连接趋势——那几类请在各自页面单独导出。导入会覆盖上述偏好项。"));
        _vm.Transfer.PropertyChanged += (_, _) =>
        {
            msg.Text = _vm.Transfer.Success ?? _vm.Transfer.Error ?? "";
            msg.Foreground = _vm.Transfer.Success is not null
                ? SettingsUi.TagColor("green")
                : SettingsUi.TagColor("red");
        };
        return SettingsUi.Card(p);
    }

    void DoExportSettings()
    {
        try
        {
            var json = _vm.Transfer.Export();
            var dlg = new SaveFileDialog
            {
                FileName = $"winshare-settings-{DateTime.Now:yyyy-MM-dd}.json",
                Filter = "JSON 文件|*.json",
            };
            if (dlg.ShowDialog() == true)
            {
                System.IO.File.WriteAllText(dlg.FileName, json);
            }
        }
        catch (Exception e)
        {
            MessageBox.Show($"导出失败：{e.Message}");
        }
    }

    void DoImportSettings()
    {
        var dlg = new OpenFileDialog { Filter = "JSON 文件|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var text = System.IO.File.ReadAllText(dlg.FileName);
            var applied = _vm.Transfer.Import(text);
            // 真源在 store：导入后刷新各卡（视图面重建绑定值）
            _ = Task.Run(async () => await _vm.RefreshAsync());
            MessageBox.Show($"已应用 {applied.Count} 项设置：{string.Join("、", applied)}");
        }
        catch (Exception e)
        {
            MessageBox.Show($"导入失败：{e.Message}", "导入设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===== 使用模式与外观卡 =====

    Border BuildUsageModeCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("使用模式与外观"));
        var mode = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        mode.Children.Add(SettingsUi.FogText("使用模式", 0, 4));
        var novice = new RadioButton { Content = "新手模式", GroupName = "usage-mode", Margin = new Thickness(0, 0, 12, 0) };
        var expert = new RadioButton { Content = "专家模式", GroupName = "usage-mode", Margin = new Thickness(0, 0, 12, 0) };
        novice.IsChecked = !_vm.Usage.AdvancedMode;
        expert.IsChecked = _vm.Usage.AdvancedMode;
        novice.Checked += async (_, _) => await _vm.Usage.ApplyModeAsync(false);
        expert.Checked += async (_, _) => await _vm.Usage.ApplyModeAsync(true);
        mode.Children.Add(novice);
        mode.Children.Add(expert);
        p.Children.Add(mode);
        _usageRadios = mode;

        var theme = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        theme.Children.Add(SettingsUi.FogText("外观主题", 0, 4));
        var light = new RadioButton { Content = "浅色", GroupName = "theme", Margin = new Thickness(0, 0, 12, 0) };
        var dark = new RadioButton { Content = "深色", GroupName = "theme", Margin = new Thickness(0, 0, 12, 0) };
        light.IsChecked = _vm.Usage.Theme == WinShare.Core.Contracts.ThemeMode.Light;
        dark.IsChecked = _vm.Usage.Theme == WinShare.Core.Contracts.ThemeMode.Dark;
        light.Checked += async (_, _) => await _vm.Usage.ApplyThemeAsync(WinShare.Core.Contracts.ThemeMode.Light);
        dark.Checked += async (_, _) => await _vm.Usage.ApplyThemeAsync(WinShare.Core.Contracts.ThemeMode.Dark);
        theme.Children.Add(light);
        theme.Children.Add(dark);
        p.Children.Add(theme);
        p.Children.Add(SettingsUi.FogText(
            "专家模式会展开高级参数（未认证超时 / 会话超时 / 每连接最大会话 / 请求压缩等），适合排障与调优；" +
            "新手模式只显示常用项，避免误改底层参数。选择结果写入应用状态，重启后保持。"));
        p.Children.Add(SettingsUi.MessageStrip(_vm.Usage));
        return SettingsUi.Card(p);
    }

    // ===== 开机自启卡（平台不支持 → 整卡降级说明，不给改不动的开关） =====

    Border BuildAutoStartCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("开机自启"));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _autostartRow = row;
        p.Children.Add(row);
        p.Children.Add(SettingsUi.FogText(
            "面向常驻服务器 / NAS 托管：开启后当前 Windows 用户登录时自动启动本面板（仅写用户登录项，不改动共享服务与账号凭据）。"));
        RebuildAutoStartRow();
        _vm.AutoStart.PropertyChanged += (_, _) => RebuildAutoStartRow();
        return SettingsUi.Card(p);
    }

    void RebuildAutoStartRow()
    {
        var row = _autostartRow;
        if (row is null) return;
        row.Children.Clear();
        var vm = _vm.AutoStart;
        if (vm.Unavailable)
        {
            row.Children.Add(SettingsUi.FogText(vm.UnavailableNote ?? "开机自启管理不可用"));
            return;
        }
        var sw = new CheckBox { Content = vm.Pending is not null ? "写入中…" : vm.StateText, Margin = new Thickness(0, 0, 12, 0) };
        sw.Checked += async (_, _) => { await vm.WriteAsync(true); };
        sw.Unchecked += async (_, _) => { await vm.WriteAsync(false); };
        sw.IsChecked = vm.Pending is not null ? vm.Pending : vm.Actual;
        row.Children.Add(sw);
        row.Children.Add(SettingsUi.FogText(vm.Pending is not null ? "写入中…" : vm.StateText));
    }

    // ===== 告警规则卡（四项各自即时提交；磁盘阈值草稿 + 失焦保存） =====

    Border BuildAlertRulesCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("告警规则"));
        var grid = new WrapPanel();
        var vm = _vm.Alerts;

        var idleCombo = SettingsUi.ComboField("空闲会话提醒", "SelectedIdle", vm,
            ["关闭", "15 分钟", "30 分钟", "60 分钟"], width: 110);
        // 下拉选项→数值提交（label 顺序对齐 AlertRulesCardViewModel.IdleOptions）
        var idleMap = new[] { 0, 15, 30, 60 };
        var idleCb = (ComboBox)((StackPanel)idleCombo).Children[1];
        idleCb.SelectionChanged += (_, _) =>
        {
            var idx = idleCb.SelectedIndex;
            if (idx >= 0 && idx < idleMap.Length) _ = vm.SaveIdleAsync(idleMap[idx]);
        };
        grid.Children.Add(idleCombo);
        grid.Children.Add(SettingsUi.FogText("会话空闲超过该时长时在监控页提示（关闭=不提示）"));

        var row = new StackPanel();
        row.Children.Add(SettingsUi.Toggle("SMB1 接入告警", nameof(AlertRulesCardViewModel.Smb1Alert), vm));
        row.Children.Add(SettingsUi.Toggle("弱口令 / 空口令告警", nameof(AlertRulesCardViewModel.WeakPasswordAlert), vm));
        _ = row;
        grid.Children.Add(row);

        var diskRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var tb = new TextBox { Width = 100 };
        // DiskDraft 为只读展示属性（经 SetDiskDraft 写）：TextBox 显示走 OneWay，输入经 TextChanged 走 SetDiskDraft，失焦保存
        tb.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("DiskDraft")
        {
            Mode = System.Windows.Data.BindingMode.OneWay,
            // null 安全由 StringIntConverter.Convert（value?.ToString() ?? ""）内建兜底，WPF Binding 无 ConverterFallback
            Converter = new StringIntConverter(),
        });
        tb.TextChanged += (_, _) =>
        {
            if (int.TryParse(tb.Text?.Trim(), out var n)) vm.SetDiskDraft(n);
        };
        tb.LostFocus += (_, _) => { _ = vm.SaveDiskAsync(); };
        diskRow.Children.Add(SettingsUi.FogText("磁盘低水位阈值（GB）", 0, 4));
        diskRow.Children.Add(tb);
        _diskHint = SettingsUi.FogText("共享所在盘剩余空间低于该值时标黄提醒（失焦即保存）");
        diskRow.Children.Add(_diskHint);
        grid.Children.Add(diskRow);

        p.Children.Add(grid);
        p.Children.Add(SettingsUi.FogText("改动即时写入应用状态：只覆盖所改的那一项，其余规则保持不变。"));
        p.Children.Add(SettingsUi.MessageStrip(vm));
        return SettingsUi.Card(p);
    }

    // ===== 防火墙卡（组内规则：列表/新增/删除只作用 WinShare Panel 组） =====

    Border BuildFirewallCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("防火墙规则（WinShare Panel 组）",
            SettingsUi.Button("刷新", async (_, _) => await _vm.Firewall.ReloadAsync())));
        p.Children.Add(SettingsUi.FogText(
            "只管理本应用「WinShare Panel」显示组内创建的规则：列表只列该组，新增与删除也只作用于该组，" +
            "不会读取或改动系统自带、以及其他软件创建的防火墙规则。"));

        var vm = _vm.Firewall;
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var combo = new ComboBox { Width = 300, VerticalAlignment = VerticalAlignment.Center };
        combo.ItemsSource = FirewallCardViewModel.Presets.ToList();
        combo.DisplayMemberPath = nameof(FirewallPresetDef.Label);
        vm.PropertyChanged += (_, _) =>
        {
            combo.SelectedValue = vm.SelectedKind;
        };
        combo.SelectionChanged += async (_, _) =>
        {
            if (combo.SelectedItem is FirewallPresetDef def)
            {
                vm.SelectPreset(def.Kind);
            }
        };
        combo.SelectedValue = vm.SelectedKind;
        pickRow.Children.Add(SettingsUi.FogText("预设", 0, 0));
        pickRow.Children.Add(combo);

        var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        var from = new TextBox { Width = 100, Text = "50000" };
        var to = new TextBox { Width = 100, Text = "51000" };
        void SyncRange()
        {
            _ = int.TryParse(from.Text.Trim(), out var f);
            _ = int.TryParse(to.Text.Trim(), out var t2);
            vm.SetPassiveRange(f, t2);
        }
        from.LostFocus += (_, _) => SyncRange();
        to.LostFocus += (_, _) => SyncRange();
        rangeRow.Children.Add(SettingsUi.FogText("起始", 0, 0));
        rangeRow.Children.Add(from);
        rangeRow.Children.Add(SettingsUi.FogText("结束", 0, 0));
        rangeRow.Children.Add(to);
        SyncRange();

        var add = SettingsUi.Button("添加预设规则", async (_, _) => await vm.AddAsync(), primary: true);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(pickRow);
        controls.Children.Add(rangeRow);
        controls.Children.Add(add);
        _firewallRows = controls;
        p.Children.Add(controls);

        var rules = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        _firewallRows.Children.Add(rules);
        _firewallRows.Children.Add(SettingsUi.MessageStrip(vm));
        void RebuildRules()
        {
            rules.Children.Clear();
            var list = vm.Rules;
            if (list.Count == 0)
            {
                rules.Children.Add(SettingsUi.FogText("「WinShare Panel」组内暂无规则"));
                return;
            }
            foreach (var r in list)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
                row.Children.Add(new TextBlock
                {
                    Text = r.name,
                    Width = 260,
                    Foreground = SettingsUi.Token("BrushText", "#1F2733"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                row.Children.Add(new TextBlock
                {
                    Text = r.ports,
                    Width = 120,
                    Foreground = SettingsUi.Token("BrushFog", "#731F2733"),
                });
                row.Children.Add(new TextBlock
                {
                    Text = r.enabled ? "启用" : "已禁用",
                    Foreground = SettingsUi.TagColor(r.enabled ? "green" : "orange"),
                    FontWeight = FontWeights.SemiBold,
                });
                var del = SettingsUi.Button("删除", async (_, _) => await vm.RemoveAsync(r.name));
                del.ToolTip = "只删除 WinShare Panel 组内这一条规则，不影响系统与其他软件的规则。";
                row.Children.Add(del);
                rules.Children.Add(row);
            }
        }
        RebuildRules();
        vm.PropertyChanged += (_, _) => RebuildRules();
        return SettingsUi.Card(p);
    }

    // ===== 账号安全体检卡 =====

    Border BuildSecurityCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("账号安全体检",
            SettingsUi.Button("重新体检", async (_, _) => await _vm.Security.RunAsync())));
        _securityPanel = new StackPanel();
        p.Children.Add(_securityPanel);
        p.Children.Add(SettingsUi.MessageStrip(_vm.Security));
        RebuildSecurity();
        _vm.Security.PropertyChanged += (_, _) => RebuildSecurity();
        return SettingsUi.Card(p);
    }

    void RebuildSecurity()
    {
        var panel = _securityPanel;
        if (panel is null) return;
        panel.Children.Clear();
        var vm = _vm.Security;
        if (vm.Running)
        {
            panel.Children.Add(SettingsUi.FogText("正在枚举本地账号（Get-LocalUser）…"));
            return;
        }
        if (vm.ReportError is { } err)
        {
            panel.Children.Add(new TextBlock { Text = err, Foreground = SettingsUi.TagColor("red"), TextWrapping = TextWrapping.Wrap });
            return;
        }
        var report = vm.Report;
        if (report is null)
        {
            panel.Children.Add(SettingsUi.FogText("尚未体检（点击「重新体检」枚举本地账号）"));
            return;
        }
        var at = report.at > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(report.at).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "";
        var checkedLine = $"已检查 {report.Checked} 个启用账号" + (at.Length > 0 ? $" · {at} 体检" : "");
        panel.Children.Add(SettingsUi.FogText(checkedLine));
        if (vm.NoIssuesText is { } ok)
        {
            panel.Children.Add(new TextBlock
            {
                Text = ok,
                Foreground = SettingsUi.TagColor("green"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        else
        {
            foreach (var issue in report.issues)
            {
                var (label, color) = SecurityCardViewModel.LevelInfo(issue.level);
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
                    Text = $" {issue.user}：{issue.issue}",
                    FontSize = 12,
                    Foreground = SettingsUi.Token("BrushText", "#1F2733"),
                });
                panel.Children.Add(row);
            }
            panel.Children.Add(SettingsUi.FogText("建议：为共享账号设置口令并开启密码过期策略（本地用户页可改），不再使用的账号请禁用。", 4, 0));
        }
    }

    // ===== 审计日志（journal 台账最近 200 条 + 结果过滤；对齐 LogPanels.AuditLogPanel 语义） =====

    Border BuildAuditCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("审计日志",
            SettingsUi.Button("刷新", (_, _) => { _vm.Audit.Reload(); RebuildAudit(); }),
            SettingsUi.Button("复制", (_, _) =>
            {
                var vm = _vm.Audit;
                var text = string.Join(Environment.NewLine, vm.Rows.Select(r =>
                    $"{(r.Time ?? "")} {r.Action} {r.Target} {r.Result} {(r.Detail ?? "")}"));
                try { Clipboard.SetText(text); } catch { }
            })));
        p.Children.Add(SettingsUi.FogText("最近 200 条操作审计（谁做了什么、结果与说明）。运行日志见下方「应用日志」。"));
        _auditPanel = new StackPanel();
        p.Children.Add(_auditPanel);
        RebuildAudit();
        return SettingsUi.Card(p);
    }

    void RebuildAudit()
    {
        var panel = _auditPanel;
        if (panel is null) return;
        panel.Children.Clear();
        var vm = _vm.Audit;
        var rows = vm.Rows;
        if (rows.Count == 0)
        {
            panel.Children.Add(SettingsUi.FogText("暂无审计记录"));
            return;
        }
        // 结果过滤（对齐任务验收「级别过滤」在审计侧的落点）
        var filter = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 0, 4) };
        filter.ItemsSource = new List<string> { "全部", "可撤销", "已应用" };
        filter.SelectedIndex = 0;
        var list = new StackPanel();
        void Rebuild(string f)
        {
            list.Children.Clear();
            var shown = f is "全部" or "" ? rows : rows.Where(r => r.Result == f).ToList();
            foreach (var r in shown)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
                row.Children.Add(new TextBlock
                {
                    Text = r.Time ?? "",
                    Width = 170,
                    Foreground = SettingsUi.Token("BrushFog", "#731F2733"),
                    FontFamily = new FontFamily("Consolas"),
                });
                row.Children.Add(new TextBlock { Text = r.Action ?? "-", Width = 90, FontSize = 12 });
                row.Children.Add(new TextBlock
                {
                    Text = r.Target ?? "-",
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 300,
                });
                row.Children.Add(new TextBlock
                {
                    Text = r.Result,
                    Foreground = SettingsUi.TagColor(r.Result == "可撤销" ? "blue" : "green"),
                    FontSize = 12,
                    Width = 60,
                });
                row.Children.Add(new TextBlock
                {
                    Text = r.Detail ?? "",
                    Foreground = SettingsUi.TagColor("red"),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 200,
                });
                list.Children.Add(row);
            }
        }
        filter.SelectionChanged += (_, _) => Rebuild(filter.SelectedItem as string ?? "全部");
        panel.Children.Add(filter);
        panel.Children.Add(list);
        Rebuild("全部");
    }

    // ===== 应用日志（app.log 最近 300 行；等宽滚动 + 级别过滤） =====

    Border BuildAppLogCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("应用日志",
            SettingsUi.Button("打开日志文件夹", (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(WinShare.App.Shell.LogSink.LogDir()) { UseShellExecute = true });
                }
                catch { }
            }),
            SettingsUi.Button("刷新", (_, _) => { _vm.AppLog.Load(); RefreshAppLog(); }),
            SettingsUi.Button("复制", (_, _) =>
            {
                try { Clipboard.SetText(_vm.AppLog.RawText); } catch { }
            })));
        p.Children.Add(SettingsUi.FogText("显示 app.log 最近 300 行（%APPDATA%\\WinSharePanel\\logs\\）；含主进程运行日志，报错后可在此复制完整堆栈。"));
        _logFilter = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 0, 4) };
        _logFilter.ItemsSource = new List<string> { "全部", "info", "warn", "error" };
        _logFilter.SelectedIndex = 0;
        _logFilter.SelectionChanged += (_, _) => RefreshAppLog();
        _appLogBox = SettingsUi.MonospaceView(_vm.AppLog.Lines);
        p.Children.Add(_logFilter);
        p.Children.Add(_appLogBox);
        RefreshAppLog();
        return SettingsUi.Card(p);
    }

    void RefreshAppLog()
    {
        var box = _appLogBox;
        if (box is null) return;
        var level = _logFilter?.SelectedItem as string ?? "全部";
        box.Text = string.Join(Environment.NewLine, _vm.AppLog.Filtered(level));
    }
}
