using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WinShare.App;
using WinShare.App.Events;
using WinShare.App.Settings;
using WinShare.App.Shell;
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
    IUnsubscribe? _refreshSub;
    StackPanel? _usageRadios;
    StackPanel? _autostartRow;
    TextBlock? _diskHint;
    StackPanel? _firewallRows;
    StackPanel? _securityPanel;
    StackPanel? _auditPanel;
    TextBox? _appLogBox;
    StackPanel? _aboutRows;

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
        WinShare.Core.Lib.Logger.Info("main", "[perf] AppSettingsPageView 构造开始");
        Content = BuildRoot();
        WinShare.Core.Lib.Logger.Info("main", "[perf] AppSettingsPageView 构造完成");
        Loaded += async (_, _) =>
        {
            if (!PageRefreshThrottle.ShouldRefresh("app-settings")) return;
            WinShare.Core.Lib.Logger.Info("main", "[perf] AppSettingsPageView 构造开始");
            await _vm.RefreshAsync();
            // v2.1：全局刷新/页面加载时关于卡行重建（跟随 advanced.updateRepo）
            if (_aboutRows is not null) RebuildAboutRows(_aboutRows);
            WinShare.Core.Lib.Logger.Info("main", "[perf] AppSettingsPageView RefreshAsync 完成");
        };
        // 全局刷新透传（F5 / 菜单 / 托盘 → 事件总线；对齐 TS refreshTick）
        _refreshSub = AppServices.Bus?.Subscribe<WinShare.App.Events.RefreshRequested>(evt =>
        {
            _ = _vm.RefreshAsync();
            Dispatcher.BeginInvoke(() => { if (_aboutRows is not null) RebuildAboutRows(_aboutRows); });
        });
        // 页面卸载必须退订：否则切走后这个实例仍在响应全局刷新 → 攒下「幽灵页面」拖慢 UI
        Unloaded += (_, _) => _refreshSub?.Unsubscribe();
    }

    static void OpenExternal(string url)
    {
        // 只放行 http(s) 绝对 URI：任意 scheme 都会交给系统处理器——这里只服务
        // 「打开 releases 主页 / 项目地址」语义。
        // v2.1.0 审计加固：Process.Start(UseShellExecute=true) 被安全扫描判为命令注入高危，
        // 改走 WinRT Launcher（系统组件按默认浏览器打开，无进程参数注入面）。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return;
        }
        try
        {
            _ = LaunchUriAsync(uri);
        }
        catch
        {
            // 浏览器打不开 → 视图卡片显示 URL 文本（用户可手动复制）
        }
    }

    private static async Task LaunchUriAsync(Uri uri)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // 打开失败静默（对齐 TS：catch 不提示）
        }
    }

    FrameworkElement BuildRoot()
    {
        var outer = new DockPanel { Margin = new Thickness(20) };
        // 页头：标题 + 操作区（对齐 Electron PageHeader：标题与侧栏导航名一致，操作在右侧一行）
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16), LastChildFill = false };
        var title = SettingsUi.PageTitle("Nav.AppSettings");
        DockPanel.SetDock(title, Dock.Left);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(SettingsUi.Button("查看 releases", (_, _) => OpenExternal(_vm.ReleasesUrl)));
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(title);
        header.Children.Add(actions);
        DockPanel.SetDock(header, Dock.Top);
        outer.Children.Add(header);

        // 三页签分组：原先 10 张卡纵向堆叠（实测 92% 控件在折叠线以下，用户看到「只有标题」）。
        // 页签后同一时刻只有当前页签内容进入视觉树。
        // 页签包一层单 Star 行 Grid：Grid 把窗口剩余高度**实际约束**给 TabControl
        // （DockPanel fill 子元素只有排列约束，量测仍 ∞ → TabControl 内层 ScrollViewer
        //  拿到无限视口 → 页面无法滚动——实测 UIA 全部可滚动元素仅横向）
        var tabHost = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        tabHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
        Grid.SetRow(tabs, 0);
        tabHost.Children.Add(tabs);
        var tabItemStyle = SettingsUi.Style("GlassTabItem");
        TabItem MakeTab(string headerText, params UIElement[] cards)
        {
            var inner = new StackPanel();
            foreach (var c in cards) inner.Children.Add(c);
            var t = new TabItem
            {
                Header = headerText,
                Content = new ScrollViewer
                {
                    Content = inner,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            };
            if (tabItemStyle is not null) t.Style = tabItemStyle;
            return t;
        }

        tabs.Items.Add(MakeTab("关于与更新", BuildAboutCard(), BuildUpdateCard(), BuildTransferCard()));
        tabs.Items.Add(MakeTab("偏好与运维", BuildUsageModeCard(), BuildAutoStartCard(),
            BuildAlertRulesCard(), BuildFirewallCard(), BuildSecurityCard(), BuildAdvancedSettingsCard()));
        tabs.Items.Add(MakeTab("日志与回收站", BuildJournalCard(), BuildAuditCard(), BuildAppLogCard()));
        outer.Children.Add(tabHost); // 最后一个子元素填满剩余空间（内含 Star 行约束 TabControl）
        return outer;
    }

    static TextBlock SectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        }.Live(TextBlock.ForegroundProperty, "BrushText");

    }

    // ===== 高级设置（v2.1 面板解锁：隐藏常量面板化，存 appstate.json advanced 段，保存即时热生效） =====

    TextBox? _advRepoBox;
    TextBox? _advPwdDaysBox;
    TextBox? _advLogSizeBox;
    TextBox? _advLogFilesBox;
    TextBox? _advFtpFromBox;
    TextBox? _advFtpToBox;
    StackPanel? _advRows;

    Border BuildAdvancedSettingsCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("高级设置",
            SettingsUi.Button("保存", (_, _) => SaveAdvancedSettings(), primary: true),
            SettingsUi.Button("恢复默认", (_, _) =>
            {
                if (MessageBox.Show("恢复全部高级设置为默认值（保存后即时生效）？", "恢复默认",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                ApplyAdvancedToBoxes(new AdvancedSettings());
                SaveAdvancedSettings();
            })));
        p.Children.Add(SettingsUi.FogText(
            "以下取值原为程序内置常量，现可在部署后直接修改（无需改代码）：更新检查仓库、弱密码审计阈值、" +
            "应用日志轮转上限、FTP 被动模式防火墙预设段。保存即时生效：更新检查下次点击即用新仓库、" +
            "日志轮转下次写入即按新上限、安全体检下次扫描按新阈值。"));
        _advRows = new StackPanel();
        p.Children.Add(_advRows);
        // 设计期/单测（组合根未装配）回落默认值，不因 Store 缺席炸页面
        AdvancedSettings current;
        try { current = AppServices.SettingsStore.Load().advanced; }
        catch { current = new AdvancedSettings(); }
        ApplyAdvancedToBoxes(current);
        return SettingsUi.Card(p);
    }

    void ApplyAdvancedToBoxes(AdvancedSettings a)
    {
        var rows = _advRows;
        if (rows is null) return;
        rows.Children.Clear();
        _advRepoBox = AdvField(rows, "更新源仓库（owner/repo）", a.updateRepo,
            "检查更新与 releases 链接共用；非法形态保存时回落默认");
        _advPwdDaysBox = AdvField(rows, "弱密码审计阈值（天）", a.passwordAgeWarnDays.ToString(),
            "密码未修改超过该天数在安全体检中告警（默认 180）");
        _advLogSizeBox = AdvField(rows, "应用日志单文件上限（MB）", a.logMaxSizeMb.ToString(),
            "触顶轮转（默认 2，范围 1..1024）");
        _advLogFilesBox = AdvField(rows, "应用日志归档数", a.logMaxFiles.ToString(),
            "轮转保留的归档份数（默认 3，范围 1..50）");
        _advFtpFromBox = AdvField(rows, "FTP 被动段起始端口", a.ftpPassiveFrom.ToString(),
            "防火墙预设「FTP 被动段」默认起始端口（默认 50000）");
        _advFtpToBox = AdvField(rows, "FTP 被动段结束端口", a.ftpPassiveTo.ToString(),
            "防火墙预设「FTP 被动段」默认结束端口（默认 51000）");
    }

    TextBox AdvField(StackPanel rows, string label, string value, string tooltip)
    {
        rows.Children.Add(SettingsUi.FogText(label, 8, 2));
        var box = new TextBox
        {
            Style = (Style?)TryFindResource("InputBoxSoft"),
            Text = value,
            ToolTip = tooltip,
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 13,
        };
        rows.Children.Add(box);
        return box;
    }

    void SaveAdvancedSettings()
    {
        if (_advRepoBox is null) return;
        var adv = new AdvancedSettings
        {
            updateRepo = _advRepoBox.Text.Trim(),
            passwordAgeWarnDays = ParseAdvInt(_advPwdDaysBox?.Text, 180),
            logMaxSizeMb = ParseAdvInt(_advLogSizeBox?.Text, 2),
            logMaxFiles = ParseAdvInt(_advLogFilesBox?.Text, 3),
            ftpPassiveFrom = ParseAdvInt(_advFtpFromBox?.Text, 50000),
            ftpPassiveTo = ParseAdvInt(_advFtpToBox?.Text, 51000),
        };
        try
        {
            var merged = AppServices.SettingsStore.SaveState(new AppStatePatch { advanced = adv });
            // 日志轮转上限即时热生效（更新源/阈值/被动段由各消费点下次动作取用）
            WinShare.Core.Lib.Logger.ConfigureRotation(merged.advanced.logMaxSizeMb, merged.advanced.logMaxFiles);
            ApplyAdvancedToBoxes(merged.advanced);
            // v2.1：更新源变更 → 关于卡三链接立即重建（跟随新仓库）
            if (_aboutRows is not null) RebuildAboutRows(_aboutRows);
            MessageBox.Show("高级设置已保存（即时生效）", "高级设置");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "高级设置");
        }
    }

    static int ParseAdvInt(string? text, int fallback) =>
        int.TryParse(text?.Trim(), out int n) ? n : fallback;

    // ===== 关于卡（含 G2：UAC 重启入口——系统服务缺席时禁用并写明原因） =====

    Border BuildAboutCard()
    {
        var p = new StackPanel();
        // 「查看 releases」已上移到页头操作区（对齐 Electron PageHeader 的 extra）
        p.Children.Add(SettingsUi.HeaderRow("关于"));
        var rows = new StackPanel();
        p.Children.Add(rows);
        _aboutRows = rows; // v2.1：持有引用，高级设置改更新源后 RefreshAsync 重建链接行
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
        // 纵向单列：标签与按钮各占一行
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = "UAC 重启",
        }.Live(TextBlock.ForegroundProperty, "BrushText"));

        relaunch.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(relaunch);
        rows.Children.Add(row);

        rows.Children.Add(SettingsUi.FogText("本项目为 Windows 文件夹共享控制面板，需以管理员身份运行；不会自动下载或安装更新。", 6, 0));
    }

    StackPanel AboutRow(string label, string value, bool copyable = false, bool open = false)
    {
        // 纵向单列：标签一行、值（含复制/打开按钮）在下逐行排列
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = label,
        }.Live(TextBlock.ForegroundProperty, "BrushText"));

        var right = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        right.Children.Add(new TextBlock
        {
            Text = value,
        }.Live(TextBlock.ForegroundProperty, "BrushPrimary"));

        if (copyable)
        {
            var copyBtn = SettingsUi.Button("复制", (_, _) =>
            {
                try
                {
                    Clipboard.SetText(value);
                }
                catch
                {
                    // 剪贴板被占用时忽略（与 TS execCommand 兜底等价的最小面）
                }
            });
            copyBtn.HorizontalAlignment = HorizontalAlignment.Left;
            copyBtn.Margin = new Thickness(0, 4, 0, 0);
            right.Children.Add(copyBtn);
        }
        if (open)
        {
            var openBtn = SettingsUi.Button("在浏览器打开", (_, _) => OpenExternal(value));
            openBtn.HorizontalAlignment = HorizontalAlignment.Left;
            openBtn.Margin = new Thickness(0, 4, 0, 0);
            right.Children.Add(openBtn);
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
        }.Live(TextBlock.ForegroundProperty, "BrushFog");

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
                status.Live(TextBlock.ForegroundProperty, "BrushFog");
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
            "导出内容：主题、新手/专家模式、置顶共享、告警规则、开机自启意图、高级设置（更新源/审计阈值/日志轮转/FTP 被动段）。\n" +
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
        // 纵向单列：模式/主题各自一行，标签与选项垂直排列；Checked 即提交逻辑不变
        var mode = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        mode.Children.Add(SettingsUi.FogText("使用模式", 0, 4));
        var novice = new RadioButton { Content = "新手模式", GroupName = "usage-mode", Margin = new Thickness(0, 0, 0, 4) };
        var expert = new RadioButton { Content = "专家模式", GroupName = "usage-mode", Margin = new Thickness(0, 0, 0, 4) };
        novice.IsChecked = !_vm.Usage.AdvancedMode;
        expert.IsChecked = _vm.Usage.AdvancedMode;
        novice.Checked += async (_, _) => await _vm.Usage.ApplyModeAsync(false);
        expert.Checked += async (_, _) => await _vm.Usage.ApplyModeAsync(true);
        mode.Children.Add(novice);
        mode.Children.Add(expert);
        p.Children.Add(mode);
        _usageRadios = mode;

        var theme = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        theme.Children.Add(SettingsUi.FogText("外观主题", 0, 4));
        var light = new RadioButton { Content = "浅色", GroupName = "theme", Margin = new Thickness(0, 0, 0, 4) };
        var dark = new RadioButton { Content = "深色", GroupName = "theme", Margin = new Thickness(0, 0, 0, 4) };
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
        // 纵向单列：开关与状态文案各占一行
        var row = new StackPanel();
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
        var sw = new CheckBox { Content = vm.Pending is not null ? "写入中…" : vm.StateText, Margin = new Thickness(0, 0, 0, 4) };
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
        // 纵向单列（原 WrapPanel 横向换行取消）：每个控件独立一行
        var grid = new StackPanel();
        var vm = _vm.Alerts;

        var idleCombo = SettingsUi.ComboField("空闲会话提醒", "SelectedIdle", vm,
            ["关闭", "15 分钟", "30 分钟", "60 分钟"], width: 110);
        // 下拉选项→数值提交（label 顺序对齐 AlertRulesCardViewModel.IdleOptions）；
        // 索引取控件依赖 SettingsUi.FieldRow 的 [0]=标签、[1]=控件 子元素顺序
        var idleMap = new[] { 0, 15, 30, 60 };
        var idleCb = (ComboBox)((StackPanel)idleCombo).Children[1];
        idleCb.SelectionChanged += (_, _) =>
        {
            var idx = idleCb.SelectedIndex;
            if (idx >= 0 && idx < idleMap.Length) _ = vm.SaveIdleAsync(idleMap[idx]);
        };
        idleCb.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(idleCombo);
        grid.Children.Add(SettingsUi.FogText("会话空闲超过该时长时在监控页提示（关闭=不提示）"));

        var row = new StackPanel();
        row.Children.Add(SettingsUi.Toggle("SMB1 接入告警", nameof(AlertRulesCardViewModel.Smb1Alert), vm));
        row.Children.Add(SettingsUi.Toggle("弱口令 / 空口令告警", nameof(AlertRulesCardViewModel.WeakPasswordAlert), vm));
        _ = row;
        grid.Children.Add(row);

        var diskRow = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var tb = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
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
        // 纵向单列：预设下拉 / 起止端口 / 添加按钮 / 规则列表 / 消息条 自上而下逐行排列
        var pickRow = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var combo = new ComboBox { Style = SettingsUi.Style("ComboBoxSoft"), Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
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

        var rangeRow = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        // v2.1：预填取高级设置 ftpPassiveFrom/To（原硬编码 50000/51000；Store 缺席回落默认）
        (int DefFrom, int DefTo) advRange;
        try
        {
            var a = AppServices.SettingsStore.Load().advanced;
            advRange = (a.ftpPassiveFrom, a.ftpPassiveTo);
        }
        catch { advRange = (50000, 51000); }
        var from = new TextBox { Width = 100, Text = advRange.DefFrom.ToString(), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "「FTP 被动段」预设规则的起始端口（默认值可在「高级设置」卡配置）" };
        var to = new TextBox { Width = 100, Text = advRange.DefTo.ToString(), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "「FTP 被动段」预设规则的结束端口（默认值可在「高级设置」卡配置）" };
        void SyncRange()
        {
            _ = int.TryParse(from.Text.Trim(), out var f);
            _ = int.TryParse(to.Text.Trim(), out var t2);
            vm.SetPassiveRange(f, t2);
        }
        from.LostFocus += (_, _) => SyncRange();
        to.LostFocus += (_, _) => SyncRange();
        rangeRow.Children.Add(SettingsUi.FogText("起始", 0, 2));
        rangeRow.Children.Add(from);
        rangeRow.Children.Add(SettingsUi.FogText("结束", 0, 2));
        rangeRow.Children.Add(to);
        SyncRange();

        var add = SettingsUi.Button("添加预设规则", async (_, _) => await vm.AddAsync(), primary: true);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        var controls = new StackPanel();
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
                // 每条规则纵向：名称 / 端口 / 状态 / 删除按钮各占一行
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
                row.Children.Add(new TextBlock
                {
                    Text = r.name,
                }.Live(TextBlock.ForegroundProperty, "BrushText"));

                row.Children.Add(new TextBlock
                {
                    Text = r.ports,
                }.Live(TextBlock.ForegroundProperty, "BrushFog"));

                row.Children.Add(new TextBlock
                {
                    Text = r.enabled ? "启用" : "已禁用",
                    Foreground = SettingsUi.TagColor(r.enabled ? "green" : "orange"),
                    FontWeight = FontWeights.SemiBold,
                });
                var del = SettingsUi.Button("删除", async (_, _) => await vm.RemoveAsync(r.name));
                del.HorizontalAlignment = HorizontalAlignment.Left;
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
                // 纵向单列：级别标签与明细各占一行
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
                    Text = $"{issue.user}：{issue.issue}",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                }.Live(TextBlock.ForegroundProperty, "BrushText"));

                panel.Children.Add(row);
            }
            panel.Children.Add(SettingsUi.FogText("建议：为共享账号设置口令并开启密码过期策略（本地用户页可改），不再使用的账号请禁用。", 4, 0));
        }
    }

    // ===== 审计日志（journal 台账最近 200 条 + 结果过滤；对齐 LogPanels.AuditLogPanel 语义） =====

    StackPanel? _journalPanel;

    /// <summary>
    /// 操作回收站（TS: JournalDrawer.tsx）：最近 200 条可撤销操作 + 一键撤销 + 清空。
    /// 撤销语义见 Core JournalService（delete 走快照重建、toggle-off 走 disabled 通道、permset 回写权限）；
    /// create 与非 SMB 操作仅留档（按钮对 undoable=false 的条目禁用并注明）。
    /// </summary>
    Border BuildJournalCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("操作回收站",
            SettingsUi.Button("刷新", (_, _) => RebuildJournal()),
            SettingsUi.Button("清空", (_, _) =>
            {
                IJournalService? svc = SettingsServiceSlots.JournalService;
                if (svc is null) return;
                MessageBoxResult ok = MessageBox.Show("清空后无法恢复撤销能力，继续？", "清空操作日志", MessageBoxButton.YesNo);
                if (ok != MessageBoxResult.Yes) return;
                int n = svc.ClearJournal();
                RebuildJournal();
                MessageBox.Show($"已清空 {n} 条操作记录", "操作回收站");
            })));
        p.Children.Add(SettingsUi.FogText("最近 200 条操作留档：共享删除/禁用/权限变更可一键撤销；创建与非 SMB 操作仅留档。"));
        _journalPanel = new StackPanel();
        p.Children.Add(_journalPanel);
        RebuildJournal();
        return SettingsUi.Card(p);
    }

    void RebuildJournal()
    {
        var panel = _journalPanel;
        if (panel is null) return;
        panel.Children.Clear();
        IJournalService? svc = SettingsServiceSlots.JournalService;
        if (svc is null)
        {
            panel.Children.Add(SettingsUi.FogText("操作回收站服务未注册：撤销能力不可用（服务层接入后自动可用）。"));
            return;
        }
        IReadOnlyList<JournalEntry> entries;
        try
        {
            entries = svc.ListJournal();
        }
        catch (Exception e)
        {
            panel.Children.Add(SettingsUi.FogText($"读取操作日志失败：{e.Message}"));
            return;
        }
        if (entries.Count == 0)
        {
            panel.Children.Add(SettingsUi.FogText("暂无操作记录（删除/禁用共享后此处会出现可撤销条目）"));
            return;
        }

        foreach (JournalEntry entry in entries.Take(50))
        {
            // 纵向单列：操作摘要 / 明细 / 撤销按钮各占一行（撤销语义与禁用态不变）
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = $"{entry.action} {entry.protocol.ToString().ToUpperInvariant()} {entry.name}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            }.Live(TextBlock.ForegroundProperty, "BrushText"));

            row.Children.Add(new TextBlock
            {
                Text = entry.detail ?? "",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            }.Live(TextBlock.ForegroundProperty, "BrushFog"));

            JournalEntry captured = entry;
            var undo = SettingsUi.Button(entry.undoable ? "撤销" : "仅留档", async (_, _) =>
            {
                IJournalService? s = SettingsServiceSlots.JournalService;
                if (s is null) return;
                try
                {
                    string msg = await s.UndoJournalAsync(captured.id);
                    RebuildJournal();
                    MessageBox.Show(msg, "撤销成功");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "撤销失败");
                }
            });
            undo.IsEnabled = entry.undoable;
            undo.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(undo);
            panel.Children.Add(row);
        }
        if (entries.Count > 50)
        {
            panel.Children.Add(SettingsUi.FogText($"仅显示最近 50 条（共 {entries.Count} 条）", 4, 0));
        }
    }

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
            }),
            SettingsUi.Button("导出", async (_, _) => await ExportAuditAsync()),
            SettingsUi.Button("清空", (_, _) =>
            {
                if (MessageBox.Show("清空当前审计日志文件（audit.log）？已轮转的历史归档保留。继续？",
                        "清空审计日志", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var sys = SettingsServiceSlots.SystemService;
                if (sys is null) { MessageBox.Show("系统服务未注册，清空不可用", "清空失败"); return; }
                try
                {
                    bool ok = sys.ClearAuditLogAsync().GetAwaiter().GetResult();
                    MessageBox.Show(ok ? "审计日志已清空" : "清空失败（文件可能被占用）", "清空审计日志");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "清空失败");
                }
                _vm.Audit.Reload();
                RebuildAudit();
            })));
        p.Children.Add(SettingsUi.FogText("最近 200 条操作审计（谁做了什么、结果与说明）。运行日志见下方「应用日志」。"));
        _auditPanel = new StackPanel();
        p.Children.Add(_auditPanel);
        RebuildAudit();
        return SettingsUi.Card(p);
    }

    /// <summary>v2.1 面板解锁：审计日志导出（全量 audit.log 文本另存为 .log 文件）。</summary>
    private async Task ExportAuditAsync()
    {
        var sys = SettingsServiceSlots.SystemService;
        if (sys is null) { MessageBox.Show("系统服务未注册，导出不可用", "导出失败"); return; }
        try
        {
            string text = await sys.GetAuditLogAsync();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(string.IsNullOrEmpty(text) ? "（审计日志为空）" : text);
            await AppServices.SaveFileWithDialogAsync($"winshare-audit-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.log", bytes);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "导出失败");
        }
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
        var list = new StackPanel();
        foreach (var r in rows)
        {
                // 纵向单列：时间 / 操作 / 对象 / 结果 / 说明 各占一行（过滤行为不变）
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
                row.Children.Add(new TextBlock
                {
                    Text = r.Time ?? "",
                }.Live(TextBlock.ForegroundProperty, "BrushFog"));

                row.Children.Add(new TextBlock { Text = r.Action ?? "-", FontSize = 12 });
                row.Children.Add(new TextBlock
                {
                    Text = r.Target ?? "-",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
                row.Children.Add(new TextBlock
                {
                    Text = r.Result,
                    Foreground = SettingsUi.TagColor(r.Result == "可撤销" ? "blue" : "green"),
                    FontSize = 12,
                });
                row.Children.Add(new TextBlock
                {
                    Text = r.Detail ?? "",
                    Foreground = SettingsUi.TagColor("red"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
                list.Children.Add(row);
            }
            panel.Children.Add(list);
    }

    // ===== 应用日志（app.log 最近 300 行；等宽滚动 + 级别过滤） =====

    Border BuildAppLogCard()
    {
        var p = new StackPanel();
        p.Children.Add(SettingsUi.HeaderRow("应用日志",
            SettingsUi.Button("打开日志文件夹", (_, _) => WinShare.App.Shell.AppShell.OpenLogDirectory()),
            SettingsUi.Button("刷新", (_, _) => { _vm.AppLog.Load(); RefreshAppLog(); }),
            SettingsUi.Button("复制", (_, _) =>
            {
                try { Clipboard.SetText(_vm.AppLog.RawText); } catch { }
            }),
            SettingsUi.Button("导出", async (_, _) => await ExportAppLogAsync()),
            SettingsUi.Button("清空", (_, _) =>
            {
                if (MessageBox.Show("清空当前应用日志文件（app.log）？已轮转的历史归档保留。继续？",
                        "清空应用日志", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var sys = SettingsServiceSlots.SystemService;
                if (sys is null) { MessageBox.Show("系统服务未注册，清空不可用", "清空失败"); return; }
                try
                {
                    bool ok = sys.ClearAppLogAsync().GetAwaiter().GetResult();
                    MessageBox.Show(ok ? "应用日志已清空" : "清空失败（文件可能被占用）", "清空应用日志");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "清空失败");
                }
                _vm.AppLog.Load();
                RefreshAppLog();
            })));
        p.Children.Add(SettingsUi.FogText("显示 app.log 最近 300 行（%APPDATA%\\WinSharePanel\\logs\\）；含主进程运行日志，报错后可在此复制完整堆栈。"));
        _appLogBox = SettingsUi.MonospaceView(_vm.AppLog.Lines);
        p.Children.Add(_appLogBox);
        RefreshAppLog();
        return SettingsUi.Card(p);
    }

    void RefreshAppLog()
    {
        var box = _appLogBox;
        if (box is null) return;
        box.Text = string.Join(Environment.NewLine, _vm.AppLog.Lines);
    }

    /// <summary>v2.1 面板解锁：应用日志导出（尾部 300 行文本另存为 .log 文件）。</summary>
    private async Task ExportAppLogAsync()
    {
        try
        {
            _vm.AppLog.Load();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                string.IsNullOrEmpty(_vm.AppLog.RawText) ? "（应用日志为空）" : _vm.AppLog.RawText);
            await AppServices.SaveFileWithDialogAsync($"winshare-applog-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.log", bytes);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "导出失败");
        }
    }
}
