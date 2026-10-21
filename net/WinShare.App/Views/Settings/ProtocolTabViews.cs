using System.Windows;
using System.Windows.Controls;
using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Settings;

/// <summary>
/// 协议配置页签基座（对齐 ProtocolSettingsShell.tsx + 各面板降级分支
/// [Source: src/components/ProtocolSettingsShell.tsx + NfsSettingsPanel.tsx]）：
/// 未安装 → 安装引导横幅；未知/探测中 → 占位；已装 → 预设条 + 表单 + 动作按钮组 + 服务状态 + 底部说明。
/// 契约服务缺席（card.DegradedNote）→ 顶部降级说明 + 动作按钮禁用（只读降级，不崩不臆造）。
/// </summary>
public abstract class ProtocolTabView<TConfig> : UserControl
    where TConfig : class, new()
{
    public ProtocolConfigCardViewModel<TConfig> Card { get; }
    public ProtocolDetectionViewModel Detection { get; }

    protected ProtocolTabView(ProtocolConfigCardViewModel<TConfig> card, ProtocolDetectionViewModel detection)
    {
        Card = card;
        Detection = detection;
        var body = new StackPanel { Margin = new Thickness(16), DataContext = card };
        Content = new Border
        {
            Background = SettingsUi.Token("BrushCard", "#FFFFFF"),
            BorderBrush = SettingsUi.Token("BrushBorder", "#E4E9F0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = body,
        };
        _form = BuildForm();
        body.Children.Add(BuildBanner());
        body.Children.Add(BuildPresets());
        body.Children.Add(_form);
        body.Children.Add(BuildActions());
        _svcStrip = SettingsUi.SvcStatusStrip(null, Card.Protocol.Display());
        body.Children.Add(_svcStrip);
        body.Children.Add(SettingsUi.FogText(FooterText, 8, 0));
        // 服务状态刷新：只更新自身内容（Clear + Add 到同一个面板是安全的；
        // 禁止把「新面板的子元素」逐个 Add 到本面板——那会抛
        // InvalidOperationException「指定的元素已经是另一元素的逻辑子元素」）
        Card.PropertyChanged += (_, _) =>
        {
            var status = Card.Service;
            _svcStrip.Children.Clear();
            foreach (System.Windows.UIElement c in SettingsUi.SvcStatusStripChildren(status, Card.Protocol.Display()))
                _svcStrip.Children.Add(c);
        };
        // 服务器值回填后重置表单绑定（绑定源=镜像属性，DataContext 换绑触发全量重拉）
        Card.OnConfigReloaded += () =>
        {
            _form.DataContext = null;
            _form.DataContext = Card;
        };
    }

    StackPanel _form = null!;
    StackPanel _svcStrip = null!;

    protected abstract string FooterText { get; }
    protected abstract ConfigPreset[] Presets { get; }
    protected abstract StackPanel BuildForm();

    /// <summary>未安装引导 / 探测占位 / 服务缺席说明（安装成功后视图侧调用 Rebuild 重渲染）。</summary>
    protected Border BuildBanner()
    {
        var border = new Border { Margin = new Thickness(0, 0, 0, 8) };
        var proto = Card.Protocol;
        void Rebuild()
        {
            var installed = Detection.InstalledOf(proto);
            var stack = new StackPanel();
            if (Card.DegradedNote is not null)
            {
                stack.Children.Add(SettingsUi.FogText(Card.DegradedNote));
            }
            if (installed is false)
            {
                var cap = Detection.FeatureStateOf(proto);
                stack.Children.Add(SettingsUi.CardTitle($"{proto.Display()} 协议未安装"));
                if (cap is not null)
                {
                    stack.Children.Add(SettingsUi.FogText(cap.installHint ?? ""));
                    var badges = ProtocolDetectionViewModel.InferBadges(cap);
                    if (badges.Count > 0)
                    {
                        var bp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                        foreach (var b in badges)
                        {
                            bp.Children.Add(new TextBlock
                            {
                                Text = b.Label,
                                Foreground = SettingsUi.TagColor(b.Color),
                                FontSize = 11,
                                Margin = new Thickness(6, 0, 0, 0),
                            });
                        }
                        stack.Children.Add(bp);
                    }
                    if (Detection.HasInstallCommand(proto))
                    {
                        var installHost = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                        var install = SettingsUi.Button("安装", async (sender, _) =>
                        {
                            // 经 sender 引用自身：避免 async 提升闭包捕获尚未赋值的本地变量（CS0841）
                            if (sender is Button btn) btn.IsEnabled = false;
                            await Detection.InstallAsync(proto);
                            if (sender is Button b2) b2.IsEnabled = true;
                            Rebuild();
                        }, primary: true);
                        installHost.Children.Add(install);
                        stack.Children.Add(installHost);
                    }
                }
            }
            else if (installed is null)
            {
                stack.Children.Add(SettingsUi.FogText("正在检测协议安装状态…（服务缺席时保持此占位，不做臆造断言）"));
            }
            border.Child = stack;
        }
        Rebuild();
        Detection.PropertyChanged += (_, _) => { Rebuild(); };
        return border;
    }

    protected StackPanel BuildPresets()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(SettingsUi.FogText("配置预设", 4, 4));
        var cb = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center, DataContext = Card };
        var defs = Presets;
        cb.ItemsSource = defs.ToList();
        cb.DisplayMemberPath = nameof(ConfigPreset.Name);
        cb.SelectionChanged += async (_, _) =>
        {
            if (cb.SelectedItem is not ConfigPreset preset) return;
            if (preset.Risk is { } risk)
            {
                var confirm = MessageBox.Show($"风险提示：{risk}\n应用该预设？", "应用前确认", MessageBoxButton.YesNo);
                if (confirm != MessageBoxResult.Yes) { cb.SelectedIndex = -1; return; }
            }
            await Card.ApplyPresetAsync(preset);
        };
        row.Children.Add(cb);
        return row;
    }

    protected StackPanel BuildActions()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var degraded = Card.DegradedNote is not null;

        var save = SettingsUi.Button("保存配置", async (_, _) => await Card.SaveAsync(), primary: true);
        var restore = SettingsUi.Button("恢复默认", async (_, _) => await Card.RestoreDefaultAsync());
        var restart = SettingsUi.Button("重启服务", async (_, _) => await Card.RestartAsync());
        // 停止：契约无 stop 方法 → 禁用并注明（缺口，见任务回报）；启动：stopped 态经 restart 达成
        var stop = SettingsUi.Button("停止", null, stopHint: "契约缺口：IProtocolAdapter 无停止方法（待 Core 服务层补齐）");
        stop.IsEnabled = false;
        var start = SettingsUi.Button("启动", async (_, _) => await Card.StartAsync());

        Card.PropertyChanged += (_, _) =>
        {
            if (Card.IsServiceStopped)
            {
                start.Visibility = Visibility.Visible;
                stop.Visibility = Visibility.Collapsed;
            }
            else
            {
                start.Visibility = Visibility.Collapsed;
                stop.Visibility = Visibility.Collapsed;
            }
            save.IsEnabled = !Card.Busy && !degraded;
            restore.IsEnabled = !Card.Busy && !degraded;
            restart.IsEnabled = !Card.Busy && !degraded;
        };

        row.Children.Add(save);
        row.Children.Add(restore);
        row.Children.Add(restart);
        row.Children.Add(start);
        row.Children.Add(stop);
        row.Children.Add(SettingsUi.MessageStrip(Card));
        return row;
    }
}

/// <summary>SMB 页签（表单逐字段对齐 Settings.tsx 服务器配置面板；新手模式收起高级参数）。</summary>
public sealed class SmbTabView : ProtocolTabView<SmbServerConfig>
{
    public SmbTabView(SmbConfigCardViewModel card, ProtocolDetectionViewModel detection) : base(card, detection) { }

    protected override string FooterText =>
        "提示：SMB1 出于安全考虑默认关闭；建议保持「要求签名」开启以防止中间人攻击。修改高级参数可能影响性能与兼容性，不确定时请点「恢复默认」。";

    protected override ConfigPreset[] Presets => [.. ProtocolPresets.Smb];

    protected override StackPanel BuildForm()
    {
        var p = new StackPanel();
        var c = (SmbConfigCardViewModel)Card;

        p.Children.Add(SettingsUi.FogText("基础协议", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("SMB1", nameof(SmbConfigCardViewModel.EnableSmb1), c),
            SettingsUi.Toggle("SMB2", nameof(SmbConfigCardViewModel.EnableSmb2), c),
            SettingsUi.Toggle("SMB3", nameof(SmbConfigCardViewModel.EnableSmb3), c),
            SettingsUi.Toggle("审计 SMB1", nameof(SmbConfigCardViewModel.AuditSmb1), c)));

        p.Children.Add(SettingsUi.FogText("安全", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("访客访问", nameof(SmbConfigCardViewModel.GuestAccess), c),
            SettingsUi.Toggle("不安全访客登录", nameof(SmbConfigCardViewModel.InsecureGuestLogons), c),
            SettingsUi.Toggle("要求签名", nameof(SmbConfigCardViewModel.RequireSignature), c),
            SettingsUi.Toggle("严格名称检查", nameof(SmbConfigCardViewModel.StrictNameChecking), c),
            SettingsUi.Toggle("静默 AU", nameof(SmbConfigCardViewModel.SilentAu), c)));

        p.Children.Add(SettingsUi.FogText("性能与功能", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("多通道", nameof(SmbConfigCardViewModel.MultiChannel), c),
            SettingsUi.Toggle("租约", nameof(SmbConfigCardViewModel.Leasing), c),
            SettingsUi.Toggle("机会锁", nameof(SmbConfigCardViewModel.Oplocks), c),
            SettingsUi.Toggle("目录缓存机会锁", nameof(SmbConfigCardViewModel.OplockDirectoryCache), c)));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("目录缓存", nameof(SmbConfigCardViewModel.DirectoryCache), c),
            SettingsUi.Toggle("通道切换", nameof(SmbConfigCardViewModel.ChannelChange), c),
            SettingsUi.Toggle("SMB QUIC", nameof(SmbConfigCardViewModel.SmbQuic), c),
            SettingsUi.Toggle("声明服务器", nameof(SmbConfigCardViewModel.AnnounceServer), c),
            SettingsUi.Toggle("每连接多会话", nameof(SmbConfigCardViewModel.MultipleSessions), c)));

        if (c.AdvancedMode)
        {
            p.Children.Add(SettingsUi.FogText("高级参数（吞吐/超时/压缩；专家模式）", 0, 4));
            p.Children.Add(SettingsUi.NumberField("未认证超时(秒)", nameof(SmbConfigCardViewModel.UnauthenticatedTimeLimit), c));
            p.Children.Add(SettingsUi.NumberField("会话超时(秒)", nameof(SmbConfigCardViewModel.SessionTimeout), c));
            p.Children.Add(SettingsUi.NumberField("每连接最大会话", nameof(SmbConfigCardViewModel.MaxSessionPerConn), c));
            p.Children.Add(SettingsUi.NumberField("最大 Mpx 数", nameof(SmbConfigCardViewModel.MaxMpxCount), c));
            p.Children.Add(SettingsUi.NumberField("最大工作项", nameof(SmbConfigCardViewModel.MaxWorkItems), c));
            p.Children.Add(SettingsUi.NumberField("每队列最大线程", nameof(SmbConfigCardViewModel.MaxThreadsPerQueue), c));
            p.Children.Add(SettingsUi.ComboField("请求压缩", nameof(SmbConfigCardViewModel.RequestCompression), c,
                ["关闭", "允许", "要求"], width: 120));
        }
        else
        {
            p.Children.Add(SettingsUi.FogText(
                "当前为新手模式：高级参数（未认证超时 / 会话超时 / 每连接最大会话 / 请求压缩等）已收起，" +
                "保存时不会改动这些项；需要调整请到「应用设置」切换到专家模式。"));
        }
        return p;
    }
}

/// <summary>NFS 页签（基础配置 + 连接与超时；身份映射/网关信息只读）。</summary>
public sealed class NfsTabView : ProtocolTabView<NfsServerConfig>
{
    public NfsTabView(NfsConfigCardViewModel card, ProtocolDetectionViewModel detection) : base(card, detection) { }

    protected override string FooterText =>
        "NFS 服务器配置修改后通常即时生效，部分参数需重启服务。客户端能力检测与共享管理见「共享管理」页。";

    protected override ConfigPreset[] Presets => [.. ProtocolPresets.Nfs];

    protected override StackPanel BuildForm()
    {
        var p = new StackPanel();
        var c = (NfsConfigCardViewModel)Card;
        p.Children.Add(SettingsUi.FogText("基础配置", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("优雅卸载", nameof(NfsConfigCardViewModel.GracefulUnmount), c),
            SettingsUi.Toggle("记录活动日志", nameof(NfsConfigCardViewModel.LogActivity), c),
            SettingsUi.Toggle("未映射用户访问", nameof(NfsConfigCardViewModel.UnmappedAccess), c),
            SettingsUi.Toggle("认证重协商", nameof(NfsConfigCardViewModel.AuthRenegotiation), c)));
        p.Children.Add(SettingsUi.FogText("连接与超时", 0, 4));
        p.Children.Add(SettingsUi.NumberField("TCP 连接超时(秒)", nameof(NfsConfigCardViewModel.TcpTimeout), c));
        p.Children.Add(SettingsUi.NumberField("UDP 连接超时(秒)", nameof(NfsConfigCardViewModel.UdpTimeout), c));
        p.Children.Add(SettingsUi.NumberField("重启连接超时(秒)", nameof(NfsConfigCardViewModel.RestartTimeout), c));
        p.Children.Add(SettingsUi.NumberField("每用户最大并发连接(0=不限)", nameof(NfsConfigCardViewModel.MaxConcurrentPerUser), c));
        p.Children.Add(SettingsUi.NumberField("目录缓存过期(秒)", nameof(NfsConfigCardViewModel.DirectoryCacheExpiry), c));
        p.Children.Add(SettingsUi.FogText("身份映射 / 网关信息（只读）", 0, 4));
        p.Children.Add(SettingsUi.FogText($"匿名 UID：{c.AnonymousUid}    匿名 GID：{c.AnonymousGid}    网关字符集：{c.GatewayCharacterSet}    协议版本：{c.ProtocolVersion}"));
        return p;
    }
}

/// <summary>FTP 页签（SSL/安全、认证、被动端口、消息与目录浏览、用户隔离/超时/文件处理/日志）。</summary>
public sealed class FtpTabView : ProtocolTabView<FtpServerConfig>
{
    public FtpTabView(FtpConfigCardViewModel card, ProtocolDetectionViewModel detection) : base(card, detection) { }

    protected override string FooterText =>
        "FTP 服务器级配置（IIS ftpServer/* 配置节）。站点级配置（端口/路径/授权）请在「共享管理」页对单个站点编辑。部分配置节可能因 IIS 锁定而写入失败。";

    protected override ConfigPreset[] Presets => [.. ProtocolPresets.Ftp];

    protected override StackPanel BuildForm()
    {
        var p = new StackPanel();
        var c = (FtpConfigCardViewModel)Card;
        p.Children.Add(SettingsUi.FogText("SSL / 安全", 0, 4));
        p.Children.Add(SettingsUi.ComboField("控制通道 SSL", nameof(FtpConfigCardViewModel.SslControl), c,
            ["允许（不强制）", "要求", "要求证书"], width: 180));
        p.Children.Add(SettingsUi.ComboField("数据通道 SSL", nameof(FtpConfigCardViewModel.SslData), c,
            ["允许（不强制）", "要求", "要求证书"], width: 180));
        p.Children.Add(SettingsUi.TextField("SSL 证书哈希(SHA-1)", nameof(FtpConfigCardViewModel.SslCertHash), c, 320));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("要求客户端证书", nameof(FtpConfigCardViewModel.SslClientCertRequired), c),
            SettingsUi.Toggle("强制 128 位 SSL", nameof(FtpConfigCardViewModel.Ssl128), c)));
        p.Children.Add(SettingsUi.FogText("认证", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("匿名认证", nameof(FtpConfigCardViewModel.AnonymousEnabled), c),
            SettingsUi.Toggle("基本认证", nameof(FtpConfigCardViewModel.BasicEnabled), c)));
        p.Children.Add(SettingsUi.TextField("匿名用户名", nameof(FtpConfigCardViewModel.AnonymousUser), c, 200));
        p.Children.Add(SettingsUi.FogText("被动模式端口范围（防火墙支持）", 0, 4));
        p.Children.Add(SettingsUi.NumberField("起始端口(0=未配置)", nameof(FtpConfigCardViewModel.FwLowPort), c));
        p.Children.Add(SettingsUi.NumberField("结束端口(0=未配置)", nameof(FtpConfigCardViewModel.FwHighPort), c));
        p.Children.Add(SettingsUi.FogText("消息与目录浏览", 0, 4));
        p.Children.Add(SettingsUi.TextField("欢迎消息", nameof(FtpConfigCardViewModel.GreetingMessage), c, 280));
        p.Children.Add(SettingsUi.TextField("横幅消息", nameof(FtpConfigCardViewModel.BannerMessage), c, 280));
        p.Children.Add(SettingsUi.TextField("退出消息", nameof(FtpConfigCardViewModel.ExitMessage), c, 280));
        p.Children.Add(SettingsUi.TextField("超限消息", nameof(FtpConfigCardViewModel.MaxClientsMessage), c, 280));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("抑制默认消息", nameof(FtpConfigCardViewModel.SuppressDefaultMessages), c),
            SettingsUi.Toggle("显示虚拟目录", nameof(FtpConfigCardViewModel.ShowVirtualDirs), c)));
        p.Children.Add(SettingsUi.FogText("用户隔离 / 超时 / 文件处理 / 日志", 0, 4));
        p.Children.Add(SettingsUi.ComboField("用户隔离模式", nameof(FtpConfigCardViewModel.IsolationMode), c,
            ["无隔离", "起始用户目录", "隔离用户", "隔离（无 AD）", "Active Directory"], width: 200));
        p.Children.Add(SettingsUi.NumberField("未认证超时(秒)", nameof(FtpConfigCardViewModel.UnauthTimeout), c));
        p.Children.Add(SettingsUi.NumberField("控制连接超时(秒)", nameof(FtpConfigCardViewModel.ControlTimeout), c));
        p.Children.Add(SettingsUi.NumberField("数据通道超时(秒)", nameof(FtpConfigCardViewModel.DataChannelTimeout), c));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("保留部分上传", nameof(FtpConfigCardViewModel.KeepPartialUploads), c),
            SettingsUi.Toggle("重命名时覆盖", nameof(FtpConfigCardViewModel.ReplaceOnRename), c)));
        p.Children.Add(SettingsUi.TextField("日志目录", nameof(FtpConfigCardViewModel.LogDir), c, 320));
        p.Children.Add(SettingsUi.ComboField("日志周期", nameof(FtpConfigCardViewModel.LogPeriod), c,
            ["每小时", "每天", "每周", "每月", "按大小", "从不"], width: 120));
        return p;
    }
}

/// <summary>WebDAV 页签（Authoring、请求筛选、认证、请求限制；服务器级只读状态单列）。</summary>
public sealed class WebdavTabView : ProtocolTabView<WebdavServerConfig>
{
    public WebdavTabView(WebdavConfigCardViewModel card, ProtocolDetectionViewModel detection) : base(card, detection) { }

    protected override string FooterText =>
        "WebDAV 服务器级配置（IIS system.webServer/* 配置节）。站点级 authoring 规则请在「共享管理」页对单个站点编辑。只读字段为服务器级状态，不可直接修改。";

    protected override ConfigPreset[] Presets => [.. ProtocolPresets.Webdav];

    protected override StackPanel BuildForm()
    {
        var p = new StackPanel();
        var c = (WebdavConfigCardViewModel)Card;
        p.Children.Add(SettingsUi.FogText("WebDAV Authoring", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("启用 Authoring", nameof(WebdavConfigCardViewModel.AuthoringEnabled), c)));
        p.Children.Add(SettingsUi.NumberField("最大请求体(字节, 0=不限)", nameof(WebdavConfigCardViewModel.AuthoringMaxBody), c, isLong: true));
        p.Children.Add(SettingsUi.FogText("请求筛选", 0, 4));
        p.Children.Add(SettingsUi.NumberField("最大内容长度(字节)", nameof(WebdavConfigCardViewModel.MaxContentLength), c, isLong: true));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("允许双重转义", nameof(WebdavConfigCardViewModel.DoubleEscaping), c),
            SettingsUi.Toggle("验证集成", nameof(WebdavConfigCardViewModel.VerifyIntegration), c)));
        p.Children.Add(SettingsUi.FogText("认证", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("匿名认证", nameof(WebdavConfigCardViewModel.AnonymousEnabled), c),
            SettingsUi.Toggle("基本认证", nameof(WebdavConfigCardViewModel.BasicEnabled), c),
            SettingsUi.Toggle("Windows 认证", nameof(WebdavConfigCardViewModel.WindowsAuth), c)));
        p.Children.Add(SettingsUi.FogText("请求限制", 0, 4));
        p.Children.Add(SettingsUi.NumberField("最大 URL 长度", nameof(WebdavConfigCardViewModel.MaxUrlLength), c));
        p.Children.Add(SettingsUi.NumberField("最大查询字符串长度", nameof(WebdavConfigCardViewModel.MaxQueryStringLength), c));
        p.Children.Add(SettingsUi.FogText("只读信息（服务器级状态）", 0, 4));
        p.Children.Add(SettingsUi.FogText(
            $"全局 Authoring 规则数：{c.GlobalRulesCount}    静态压缩：{(c.StaticCompression ? "启用" : "禁用")}    " +
            $"动态压缩：{(c.DynamicCompression ? "启用" : "禁用")}    要求 SSL：{(c.RequireSsl ? "是" : "否")}"));
        return p;
    }
}
