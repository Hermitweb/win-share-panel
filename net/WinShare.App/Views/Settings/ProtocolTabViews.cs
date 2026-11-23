using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Settings;

/// <summary>
/// 协议配置板块基座（纵向流重构：原协议配置页签，去掉 TabItem 载体语义，
/// 内容作为独立 section 直接嵌入设置页纵向单列；卡壳由宿主 SettingsPageView 提供，
/// 本基座不再自带 Border）。降级分支不变（对齐 ProtocolSettingsShell.tsx + 各面板
/// [Source: src/components/ProtocolSettingsShell.tsx + NfsSettingsPanel.tsx]）：
/// 未安装 → 安装引导横幅；未知/探测中 → 占位；已装 → 预设条 + 表单 + 动作按钮组 + 服务状态 + 底部说明。
/// 契约服务缺席（card.DegradedNote）→ 顶部降级说明 + 动作按钮禁用（只读降级，不崩不臆造）。
/// 内部布局一律纵向单列；重渲染时只能 Clear+Add 回同一面板（_svcStrip / _form）。
/// </summary>
public abstract class ProtocolSectionView<TConfig> : UserControl
    where TConfig : class, new()
{
    public ProtocolConfigCardViewModel<TConfig> Card { get; }
    public ProtocolDetectionViewModel Detection { get; }

    protected ProtocolSectionView(ProtocolConfigCardViewModel<TConfig> card, ProtocolDetectionViewModel detection)
    {
        Card = card;
        Detection = detection;
        var body = new StackPanel { Margin = new Thickness(0), DataContext = card };
        Content = body;
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
            foreach (System.Windows.UIElement c in SettingsUi.SvcStatusStripChildren(
                status, Card.Protocol.Display(),
                applyStartType: token => Card.SetServiceStartTypeAsync(token)))
                _svcStrip.Children.Add(c);
        };
        // 服务器值回填后重建表单（v2.3：原「DataContext 重绑」在部分场景不刷新勾选态——
        // 预设应用/恢复默认后字段毫无变化，用户以为没联动。重建保证显示最新镜像值）
        Card.OnConfigReloaded += () =>
        {
            int idx = body.Children.IndexOf(_form);
            if (idx < 0) return;
            var fresh = BuildForm();
            fresh.DataContext = Card;
            body.Children[idx] = fresh;
            _form = fresh;
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
                        var bp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                        foreach (var b in badges)
                        {
                            bp.Children.Add(new TextBlock
                            {
                                Text = b.Label,
                                Foreground = SettingsUi.TagColor(b.Color),
                                FontSize = 11,
                                Margin = new Thickness(0, 0, 0, 2),
                            });
                        }
                        stack.Children.Add(bp);
                    }
                    if (Detection.HasInstallCommand(proto))
                    {
                        var install = SettingsUi.Button("安装", async (sender, _) =>
                        {
                            // 经 sender 引用自身：避免 async 提升闭包捕获尚未赋值的本地变量（CS0841）
                            if (sender is Button btn) btn.IsEnabled = false;
                            await Detection.InstallAsync(proto);
                            if (sender is Button b2) b2.IsEnabled = true;
                            Rebuild();
                        }, primary: true);
                        install.HorizontalAlignment = HorizontalAlignment.Left;
                        install.Margin = new Thickness(0, 4, 0, 0);
                        stack.Children.Add(install);
                    }
                }
            }
            else if (installed is null)
            {
                stack.Children.Add(SettingsUi.FogText("正在检测协议安装状态…"));
            }
            border.Child = stack;
        }
        Rebuild();
        // 板块随页面卸载后必须退订：否则探测状态每次变化都会 Rebuild 一个已卸载的板块。
        // 注意必须持有同一 handler 引用退订——原写法 `-=` 新建 lambda 与订阅实例不同，实际永不解绑。
        PropertyChangedEventHandler onDetectionChanged = (_, _) => Rebuild();
        Detection.PropertyChanged += onDetectionChanged;
        Unloaded += (_, _) => Detection.PropertyChanged -= onDetectionChanged;
        return border;
    }

    protected StackPanel BuildPresets()
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        p.Children.Add(SettingsUi.FogText("配置预设", 4, 4));
        // v2.2.0 第五批：首项为占位「选择预设…」（此前空下拉像坏掉的控件）
        var cb = new ComboBox
        {
            Style = (Style?)TryFindResource("ComboBoxSoft"),
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            DataContext = Card,
        };
        cb.Items.Add(new ComboBoxItem { Content = "选择预设…" });
        foreach (var def in Presets)
            cb.Items.Add(new ComboBoxItem { Content = def.Name, Tag = def });
        cb.SelectedIndex = 0;
        cb.SelectionChanged += async (_, _) =>
        {
            if (cb.SelectedItem is not ComboBoxItem { Tag: ConfigPreset preset }) return;
            if (preset.Risk is { } risk)
            {
                var confirm = MessageBox.Show($"风险提示：{risk}\n应用该预设？", "应用前确认", MessageBoxButton.YesNo);
                if (confirm != MessageBoxResult.Yes) { cb.SelectedIndex = 0; return; }
            }
            await Card.ApplyPresetAsync(preset);
            cb.SelectedIndex = 0; // 应用后回到占位（配置已写入，避免误以为"还没生效"）
        };
        p.Children.Add(cb);
        return p;
    }

    protected StackPanel BuildActions()
    {
        var row = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var degraded = Card.DegradedNote is not null;

        var save = SettingsUi.Button("保存配置", async (_, _) => await Card.SaveAsync(), primary: true);
        var restore = SettingsUi.Button("恢复默认", async (_, _) =>
        {
            // v2.2.0 第二轮：恢复默认会用安全推荐默认覆盖当前配置——先确认（此前直接执行）
            var confirm = MessageBox.Show($"将把 {Card.Protocol.Display()} 服务器配置恢复为安全推荐默认值（当前自定义修改会被覆盖），继续？",
                "恢复默认确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm == MessageBoxResult.OK) await Card.RestoreDefaultAsync();
        });
        var restart = SettingsUi.Button("重启服务", async (_, _) =>
        {
            // v2.2.0 第二轮：重启会中断现存连接——先确认（与「停止」口径一致）
            var confirm = MessageBox.Show($"重启 {Card.Protocol.Display()} 服务将中断当前连接（短暂不可访问），继续？",
                "重启确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm == MessageBoxResult.OK) await Card.RestartAsync();
        });
        // 停止：契约已补 IProtocolAdapter.StopServiceAsync（v1.1.1）；启动：stopped 态经 restart 达成
        var stop = SettingsUi.Button("停止", async (_, _) =>
        {
            MessageBoxResult confirm = MessageBox.Show($"停止 {Card.Protocol.Display()} 服务将中断所有连接，继续？", "停止确认", MessageBoxButton.YesNo);
            if (confirm == MessageBoxResult.Yes) await Card.StopAsync();
        }, stopHint: "停止该协议服务（会中断现存连接）");
        var start = SettingsUi.Button("启动", async (_, _) => await Card.StartAsync());
        // v2.2.0 第四轮：动作按钮横排（纵排 5 行占满垂直空间，设置页高度紧张）
        var actionRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var b in new[] { save, restore, restart, start, stop })
        {
            b.Margin = new Thickness(0, 0, 8, 4);
            actionRow.Children.Add(b);
        }
        row.Children.Add(actionRow);

        Card.PropertyChanged += (_, _) =>
        {
            // 三态显隐：stopped → 显示「启动」；running → 显示「停止」；未知/未读取 → 两者都隐藏
            bool stopped = Card.IsServiceStopped;
            bool running = Card.Service?.status == WinShare.Core.Contracts.ServiceState.Running;
            start.Visibility = stopped ? Visibility.Visible : Visibility.Collapsed;
            stop.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            // 注意：save/restore/restart/start/stop 已加入 actionRow（横排），此处仅控制显隐
            save.IsEnabled = !Card.Busy && !degraded;
            restore.IsEnabled = !Card.Busy && !degraded;
            restart.IsEnabled = !Card.Busy && !degraded;
            stop.IsEnabled = !Card.Busy && !degraded;
        };

        row.Children.Add(SettingsUi.MessageStrip(Card));
        return row;
    }
}

/// <summary>SMB 板块（纵向单列；表单逐字段对齐 Settings.tsx 服务器配置面板；新手模式收起高级参数）。</summary>
public sealed class SmbTabView : ProtocolSectionView<SmbServerConfig>
{
    /// <summary>
    /// VM 属性名 → Set-SmbServerConfiguration 参数名（v2.3：UI 禁用不支持字段；
    /// 保存侧过滤见 SmbService.GetSettableFieldsAsync——两处同源于该映射）。
    /// </summary>
    internal static readonly Dictionary<string, string> VmToPs = new()
    {
        ["EnableSmb1"] = "EnableSMB1Protocol",
        ["EnableSmb2"] = "EnableSMB2Protocol",
        ["EnableSmb3"] = "EnableSMB3Protocol",
        ["AuditSmb1"] = "AuditSmb1Access",
        ["GuestAccess"] = "EnableGuestUserAccess",
        ["InsecureGuestLogons"] = "EnableInsecureGuestLogons",
        ["RequireSignature"] = "RequireSecuritySignature",
        ["StrictNameChecking"] = "EnableStrictNameChecking",
        ["SilentAu"] = "SilentAU",
        ["MultiChannel"] = "EnableMultiChannel",
        ["Leasing"] = "EnableLeasing",
        ["Oplocks"] = "EnableOplocks",
        ["OplockDirectoryCache"] = "EnableOplockDirectoryCache",
        ["DirectoryCache"] = "EnableSMBDirectoryCache",
        ["ChannelChange"] = "EnableChannelChange",
        ["SmbQuic"] = "EnableSMBQUIC",
        ["AnnounceServer"] = "AnnounceServer",
        ["MultipleSessions"] = "MultipleSessionsPerConnection",
        ["UnauthenticatedTimeLimit"] = "UnauthenticatedUsersTimeLimit",
        ["SessionTimeout"] = "SessionTimeoutSeconds",
        ["MaxSessionPerConn"] = "MaxSessionPerConnection",
        ["MaxMpxCount"] = "MaxMpxCount",
        ["MaxWorkItems"] = "MaxWorkItems",
        ["MaxThreadsPerQueue"] = "MaxThreadsPerQueue",
        ["RequestCompression"] = "RequestCompression",
    };

    public SmbTabView(SmbConfigCardViewModel card, ProtocolDetectionViewModel detection) : base(card, detection) { }

    protected override string FooterText =>
        "SMB1 默认关闭，建议保持「请求签名」开启。";

    protected override ConfigPreset[] Presets => [.. ProtocolPresets.Smb];

    /// <summary>给字段控件打 Tag（VM 属性名），供异步能力探测后禁用。
    /// NumberField/ComboField 返回 FieldRow（Children[1]=控件）→ Tag 落在控件上；Toggle 直接是 CheckBox。</summary>
    static T Tag<T>(T element, string vmPath) where T : FrameworkElement
    {
        if (element is StackPanel { Children.Count: > 1 } row && row.Children[1] is FrameworkElement fe)
            fe.Tag = vmPath;
        else element.Tag = vmPath;
        return element;
    }

    protected override StackPanel BuildForm()
    {
        var p = new StackPanel();
        var c = (SmbConfigCardViewModel)Card;

        p.Children.Add(SettingsUi.FogText("基础协议", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            Tag(SettingsUi.Toggle("SMB1", nameof(SmbConfigCardViewModel.EnableSmb1), c), nameof(SmbConfigCardViewModel.EnableSmb1)),
            Tag(SettingsUi.Toggle("SMB2", nameof(SmbConfigCardViewModel.EnableSmb2), c), nameof(SmbConfigCardViewModel.EnableSmb2)),
            Tag(SettingsUi.Toggle("SMB3", nameof(SmbConfigCardViewModel.EnableSmb3), c), nameof(SmbConfigCardViewModel.EnableSmb3)),
            Tag(SettingsUi.Toggle("审计 SMB1", nameof(SmbConfigCardViewModel.AuditSmb1), c), nameof(SmbConfigCardViewModel.AuditSmb1))));

        p.Children.Add(SettingsUi.FogText("安全", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            Tag(SettingsUi.Toggle("访客访问", nameof(SmbConfigCardViewModel.GuestAccess), c), nameof(SmbConfigCardViewModel.GuestAccess)),
            Tag(SettingsUi.Toggle("不安全访客登录", nameof(SmbConfigCardViewModel.InsecureGuestLogons), c), nameof(SmbConfigCardViewModel.InsecureGuestLogons)),
            Tag(SettingsUi.Toggle("要求签名", nameof(SmbConfigCardViewModel.RequireSignature), c), nameof(SmbConfigCardViewModel.RequireSignature)),
            Tag(SettingsUi.Toggle("严格名称检查", nameof(SmbConfigCardViewModel.StrictNameChecking), c), nameof(SmbConfigCardViewModel.StrictNameChecking)),
            Tag(SettingsUi.Toggle("静默 AU", nameof(SmbConfigCardViewModel.SilentAu), c), nameof(SmbConfigCardViewModel.SilentAu))));

        p.Children.Add(SettingsUi.FogText("性能与功能", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            Tag(SettingsUi.Toggle("多通道", nameof(SmbConfigCardViewModel.MultiChannel), c), nameof(SmbConfigCardViewModel.MultiChannel)),
            Tag(SettingsUi.Toggle("租约", nameof(SmbConfigCardViewModel.Leasing), c), nameof(SmbConfigCardViewModel.Leasing)),
            Tag(SettingsUi.Toggle("机会锁", nameof(SmbConfigCardViewModel.Oplocks), c), nameof(SmbConfigCardViewModel.Oplocks)),
            Tag(SettingsUi.Toggle("目录缓存机会锁", nameof(SmbConfigCardViewModel.OplockDirectoryCache), c), nameof(SmbConfigCardViewModel.OplockDirectoryCache))));
        p.Children.Add(SettingsUi.ToggleGroup(
            Tag(SettingsUi.Toggle("目录缓存", nameof(SmbConfigCardViewModel.DirectoryCache), c), nameof(SmbConfigCardViewModel.DirectoryCache)),
            Tag(SettingsUi.Toggle("通道切换", nameof(SmbConfigCardViewModel.ChannelChange), c), nameof(SmbConfigCardViewModel.ChannelChange)),
            Tag(SettingsUi.Toggle("SMB QUIC", nameof(SmbConfigCardViewModel.SmbQuic), c), nameof(SmbConfigCardViewModel.SmbQuic)),
            Tag(SettingsUi.Toggle("声明服务器", nameof(SmbConfigCardViewModel.AnnounceServer), c), nameof(SmbConfigCardViewModel.AnnounceServer)),
            Tag(SettingsUi.Toggle("每连接多会话", nameof(SmbConfigCardViewModel.MultipleSessions), c), nameof(SmbConfigCardViewModel.MultipleSessions))));

        // v2.2.0 第二轮：SMB 高级参数与其他三协议统一为折叠语义（新手收起、专家展开，
        // 展开后可编辑并提交——所见即所存；此前新手整体隐藏 + 补丁门控，展开编辑保存会被静默丢弃）
        var smbAdv = new StackPanel();
        smbAdv.Children.Add(Tag(SettingsUi.NumberField("未认证超时(秒)", nameof(SmbConfigCardViewModel.UnauthenticatedTimeLimit), c), nameof(SmbConfigCardViewModel.UnauthenticatedTimeLimit)));
        smbAdv.Children.Add(Tag(SettingsUi.NumberField("会话超时(秒)", nameof(SmbConfigCardViewModel.SessionTimeout), c), nameof(SmbConfigCardViewModel.SessionTimeout)));
        smbAdv.Children.Add(Tag(SettingsUi.NumberField("每连接最大会话", nameof(SmbConfigCardViewModel.MaxSessionPerConn), c), nameof(SmbConfigCardViewModel.MaxSessionPerConn)));
        smbAdv.Children.Add(Tag(SettingsUi.NumberField("最大 Mpx 数", nameof(SmbConfigCardViewModel.MaxMpxCount), c), nameof(SmbConfigCardViewModel.MaxMpxCount)));
        smbAdv.Children.Add(Tag(SettingsUi.NumberField("最大工作项", nameof(SmbConfigCardViewModel.MaxWorkItems), c), nameof(SmbConfigCardViewModel.MaxWorkItems)));
        smbAdv.Children.Add(Tag(SettingsUi.NumberField("每队列最大线程", nameof(SmbConfigCardViewModel.MaxThreadsPerQueue), c), nameof(SmbConfigCardViewModel.MaxThreadsPerQueue)));
        smbAdv.Children.Add(Tag(SettingsUi.ComboField("请求压缩", nameof(SmbConfigCardViewModel.RequestCompression), c,
            ["关闭", "允许", "要求"], width: 120), nameof(SmbConfigCardViewModel.RequestCompression)));
        p.Children.Add(SettingsUi.ExpanderField("高级参数（吞吐 / 超时 / 压缩）", smbAdv, c.AdvancedMode));

        // v2.3：异步探测本机 Set 参数集 → 禁用不支持字段 + 顶部说明（保存侧同步跳过，双保险）
        Loaded += async (_, _) => await ApplySettableAsync(p);
        return p;
    }

    async System.Threading.Tasks.Task ApplySettableAsync(StackPanel form)
    {
        try
        {
            var svc = SettingsServiceSlots.SmbService;
            if (svc is null) return;
            var supported = await svc.GetSettableFieldsAsync();
            if (supported is null || supported.Count == 0) return;   // 探测失败/空 → 不禁用（保存侧兜底）
            var disabled = new List<string>();
            foreach (Control ctl in Descendants(form))
            {
                if (ctl.Tag is not string vmPath || !VmToPs.TryGetValue(vmPath, out var ps)) continue;
                if (supported.Contains(ps)) continue;
                ctl.IsEnabled = false;
                ctl.ToolTip = $"本机 Set-SmbServerConfiguration 不支持参数 {ps}（保存时将自动跳过）";
                if (disabled.Count < 4) disabled.Add(ps);
            }
            if (disabled.Count == 0) return;
            form.Children.Insert(0, SettingsUi.FogText(
                $"以下字段本机不支持，已禁用（保存时自动跳过）：{string.Join("、", disabled)}{(disabled.Count == 4 ? " 等" : "")}", 0, 0));
        }
        catch { /* 能力探测失败不影响表单 */ }
    }

    /// <summary>可视树后代控件（Tag 定位字段控件；Form 树很浅，递归成本可忽略）。</summary>
    static IEnumerable<Control> Descendants(System.Windows.DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Control c) yield return c;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}

/// <summary>NFS 板块（纵向单列；基础配置 + 连接与超时 + 协议版本开关；身份映射/网关经提供程序直写）。</summary>
public sealed class NfsTabView : ProtocolSectionView<NfsServerConfig>
{
    public NfsTabView(NfsConfigCardViewModel card, ProtocolDetectionViewModel detection) : base(card, detection) { }

    protected override string FooterText =>
        "部分参数需重启服务后生效。";

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
        p.Children.Add(SettingsUi.FogText($"协议版本：{c.ProtocolVersion}"));

        // v2.2.0 第二轮：超时/协议版本/身份映射/网关属专家字段——折叠为「高级参数」
        // （新手默认收起、专家默认展开；展开可正常编辑，合并-回写语义下收起不影响现值）
        var adv = new StackPanel();
        adv.Children.Add(SettingsUi.FogText("连接与超时", 0, 4));
        adv.Children.Add(SettingsUi.NumberField("TCP 连接超时(秒)", nameof(NfsConfigCardViewModel.TcpTimeout), c));
        adv.Children.Add(SettingsUi.NumberField("UDP 连接超时(秒)", nameof(NfsConfigCardViewModel.UdpTimeout), c));
        adv.Children.Add(SettingsUi.NumberField("重启连接超时(秒)", nameof(NfsConfigCardViewModel.RestartTimeout), c));
        adv.Children.Add(SettingsUi.NumberField("每用户最大并发连接(0=不限)", nameof(NfsConfigCardViewModel.MaxConcurrentPerUser), c));
        adv.Children.Add(SettingsUi.NumberField("目录缓存过期(秒)", nameof(NfsConfigCardViewModel.DirectoryCacheExpiry), c));
        adv.Children.Add(SettingsUi.FogText("协议版本", 0, 4));
        adv.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("启用 NFSv2", nameof(NfsConfigCardViewModel.EnableNfsV2), c),
            SettingsUi.Toggle("启用 NFSv3", nameof(NfsConfigCardViewModel.EnableNfsV3), c),
            SettingsUi.Toggle("启用 NFSv4.1", nameof(NfsConfigCardViewModel.EnableNfsV4), c)));
        adv.Children.Add(SettingsUi.FogText("身份映射 / 网关", 0, 4));
        var uidField = SettingsUi.NumberField("匿名 UID(-2=未映射)", nameof(NfsConfigCardViewModel.AnonymousUid), c);
        var gidField = SettingsUi.NumberField("匿名 GID(-2=未映射)", nameof(NfsConfigCardViewModel.AnonymousGid), c);
        uidField.ToolTip = gidField.ToolTip = "不确定时保持 -2（未映射）；仅当 Linux 客户端需要固定 UID/GID 归属时才修改。";
        adv.Children.Add(uidField);
        adv.Children.Add(gidField);

        adv.Children.Add(SettingsUi.TextField("网关字符集", nameof(NfsConfigCardViewModel.GatewayCharacterSet), c, 160));
        p.Children.Add(SettingsUi.ExpanderField("高级参数", adv, c.AdvancedMode));
        return p;
    }
}

/// <summary>FTP 板块（纵向单列；SSL/安全、认证、被动端口、消息与目录浏览、用户隔离/超时/文件处理/日志）。</summary>
public sealed class FtpTabView : ProtocolSectionView<FtpServerConfig>
{
    public FtpTabView(FtpConfigCardViewModel card, ProtocolDetectionViewModel detection) : base(card, detection) { }

    protected override string FooterText =>
        "站点级配置（端口 / 路径 / 授权）在「共享管理」页编辑。";

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
        p.Children.Add(SettingsUi.FogText("认证", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("匿名认证", nameof(FtpConfigCardViewModel.AnonymousEnabled), c),
            SettingsUi.Toggle("基本认证", nameof(FtpConfigCardViewModel.BasicEnabled), c)));
        p.Children.Add(SettingsUi.TextField("匿名用户名", nameof(FtpConfigCardViewModel.AnonymousUser), c, 200));

        // v2.2.0 第二轮：证书/被动端口/消息/用户隔离/超时/文件处理/日志属专家字段——折叠为「高级参数」
        var adv = new StackPanel();
        adv.Children.Add(SettingsUi.FogText("SSL 高级", 0, 4));
        var sslHashField = SettingsUi.TextField("SSL 证书哈希(SHA-1)", nameof(FtpConfigCardViewModel.SslCertHash), c, 320);
        sslHashField.ToolTip = "留空 = 使用系统默认证书；需指定时填证书的 SHA-1 指纹（certlm.msc → 个人 → 证书 → 详情）。";
        adv.Children.Add(sslHashField);

        adv.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("要求客户端证书", nameof(FtpConfigCardViewModel.SslClientCertRequired), c),
            SettingsUi.Toggle("强制 128 位 SSL", nameof(FtpConfigCardViewModel.Ssl128), c)));
        adv.Children.Add(SettingsUi.FogText("被动模式端口范围", 0, 4));
        adv.Children.Add(SettingsUi.NumberField("起始端口(0=未配置)", nameof(FtpConfigCardViewModel.FwLowPort), c));
        adv.Children.Add(SettingsUi.NumberField("结束端口(0=未配置)", nameof(FtpConfigCardViewModel.FwHighPort), c));
        p.Children.Add(SettingsUi.FogText("消息与目录浏览", 0, 4));
        adv.Children.Add(SettingsUi.TextField("欢迎消息", nameof(FtpConfigCardViewModel.GreetingMessage), c, 280));
        adv.Children.Add(SettingsUi.TextField("横幅消息", nameof(FtpConfigCardViewModel.BannerMessage), c, 280));
        adv.Children.Add(SettingsUi.TextField("退出消息", nameof(FtpConfigCardViewModel.ExitMessage), c, 280));
        adv.Children.Add(SettingsUi.TextField("超限消息", nameof(FtpConfigCardViewModel.MaxClientsMessage), c, 280));
        adv.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("抑制默认消息", nameof(FtpConfigCardViewModel.SuppressDefaultMessages), c),
            SettingsUi.Toggle("显示虚拟目录", nameof(FtpConfigCardViewModel.ShowVirtualDirs), c)));
        p.Children.Add(SettingsUi.FogText("用户隔离 / 超时 / 文件处理 / 日志", 0, 4));
        adv.Children.Add(SettingsUi.ComboField("用户隔离模式", nameof(FtpConfigCardViewModel.IsolationMode), c,
            ["无隔离", "起始用户目录", "隔离用户", "隔离（无 AD）", "Active Directory"], width: 200));
        adv.Children.Add(SettingsUi.NumberField("未认证超时(秒)", nameof(FtpConfigCardViewModel.UnauthTimeout), c));
        adv.Children.Add(SettingsUi.NumberField("控制连接超时(秒)", nameof(FtpConfigCardViewModel.ControlTimeout), c));
        adv.Children.Add(SettingsUi.NumberField("数据通道超时(秒)", nameof(FtpConfigCardViewModel.DataChannelTimeout), c));
        adv.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("保留部分上传", nameof(FtpConfigCardViewModel.KeepPartialUploads), c),
            SettingsUi.Toggle("重命名时覆盖", nameof(FtpConfigCardViewModel.ReplaceOnRename), c)));
        adv.Children.Add(SettingsUi.TextField("日志目录", nameof(FtpConfigCardViewModel.LogDir), c, 320));
        adv.Children.Add(SettingsUi.ComboField("日志周期", nameof(FtpConfigCardViewModel.LogPeriod), c,
            ["每小时", "每天", "每周", "每月", "按大小", "从不"], width: 120));
        p.Children.Add(SettingsUi.ExpanderField("高级参数（证书 / 被动端口 / 消息 / 用户隔离 / 超时 / 日志）", adv, c.AdvancedMode));
        return p;
    }
}

/// <summary>WebDAV 板块（纵向单列；Authoring、请求筛选、认证、请求限制、压缩与 SSL；全局 Authoring 规则管理）。</summary>
public sealed class WebdavTabView : ProtocolSectionView<WebdavServerConfig>
{
    public WebdavTabView(WebdavConfigCardViewModel card, ProtocolDetectionViewModel detection) : base(card, detection) { }

    protected override string FooterText =>
        "站点级规则在「共享管理」页编辑；动态压缩需应用程序池回收后生效。";

    protected override ConfigPreset[] Presets => [.. ProtocolPresets.Webdav];

    protected override StackPanel BuildForm()
    {
        var p = new StackPanel();
        var c = (WebdavConfigCardViewModel)Card;
        p.Children.Add(SettingsUi.FogText("WebDAV Authoring", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("启用 Authoring", nameof(WebdavConfigCardViewModel.AuthoringEnabled), c)));
        p.Children.Add(SettingsUi.FogText("认证", 0, 4));
        p.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("匿名认证", nameof(WebdavConfigCardViewModel.AnonymousEnabled), c),
            SettingsUi.Toggle("基本认证", nameof(WebdavConfigCardViewModel.BasicEnabled), c),
            SettingsUi.Toggle("Windows 认证", nameof(WebdavConfigCardViewModel.WindowsAuth), c)));
        p.Children.Add(SettingsUi.FogText($"全局 Authoring 规则数：{c.GlobalRulesCount}"));

        // v2.2.0 第二轮：请求筛选/请求限制/压缩与 SSL/全局规则管理属专家字段——折叠为「高级参数」
        var adv = new StackPanel();
        adv.Children.Add(SettingsUi.FogText("请求筛选", 0, 4));
        var maxBodyField = SettingsUi.NumberField("最大请求体(字节, 0=不限)", nameof(WebdavConfigCardViewModel.AuthoringMaxBody), c, isLong: true);
        maxBodyField.ToolTip = "30000000 ≈ 28.6 MB；100000000 ≈ 95.4 MB（IIS 对大文件上传的常见上限）。";
        adv.Children.Add(maxBodyField);
        adv.Children.Add(SettingsUi.NumberField("最大内容长度(字节)", nameof(WebdavConfigCardViewModel.MaxContentLength), c, isLong: true));

        adv.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("允许双重转义", nameof(WebdavConfigCardViewModel.DoubleEscaping), c),
            SettingsUi.Toggle("验证集成", nameof(WebdavConfigCardViewModel.VerifyIntegration), c)));
        adv.Children.Add(SettingsUi.FogText("请求限制", 0, 4));
        adv.Children.Add(SettingsUi.NumberField("最大 URL 长度", nameof(WebdavConfigCardViewModel.MaxUrlLength), c));
        adv.Children.Add(SettingsUi.NumberField("最大查询字符串长度", nameof(WebdavConfigCardViewModel.MaxQueryStringLength), c));
        adv.Children.Add(SettingsUi.FogText("压缩与 SSL（服务器级）", 0, 4));
        adv.Children.Add(SettingsUi.ToggleGroup(
            SettingsUi.Toggle("静态压缩", nameof(WebdavConfigCardViewModel.StaticCompression), c),
            SettingsUi.Toggle("动态压缩", nameof(WebdavConfigCardViewModel.DynamicCompression), c),
            SettingsUi.Toggle("要求 SSL", nameof(WebdavConfigCardViewModel.RequireSsl), c)));
        adv.Children.Add(BuildAuthoringRulesPanel());
        p.Children.Add(SettingsUi.ExpanderField("高级参数", adv, c.AdvancedMode));
        return p;
    }

    // === 全局 Authoring 规则管理（v2.1 解锁：查看/添加/删除服务器级 authoringRules） ===

    private StackPanel _rulesList = null!;
    private TextBox _ruleExt = null!;
    private TextBox _rulePath = null!;
    private ComboBox _ruleAccess = null!;
    private TextBox _ruleUsers = null!;
    private TextBox _ruleRoles = null!;

    private StackPanel BuildAuthoringRulesPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(SettingsUi.HeaderRow("全局 Authoring 规则管理",
            SettingsUi.Button("刷新规则", (_, _) => _ = ReloadRulesAsync())));

        // 添加规则表单（纵向单列，对齐页面布局惯例）
        _ruleExt = new TextBox { Style = (Style?)TryFindResource("InputBoxSoft"), Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        _rulePath = new TextBox { Style = (Style?)TryFindResource("InputBoxSoft"), Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Text = "*" };
        _ruleAccess = new ComboBox { Style = (Style?)TryFindResource("ComboBoxSoft"), Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (label, value) in new[] { ("读取", "Read"), ("读取+写入", "Read,Write"), ("读取+写入+源", "Read,Write,Source") })
        {
            _ruleAccess.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        _ruleAccess.SelectedIndex = 1;
        _ruleUsers = new TextBox { Style = (Style?)TryFindResource("InputBoxSoft"), Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Text = "*" };
        _ruleRoles = new TextBox { Style = (Style?)TryFindResource("InputBoxSoft"), Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(SettingsUi.FieldRow("扩展名（* 或 .txt）", _ruleExt));
        panel.Children.Add(SettingsUi.FieldRow("路径（默认 *）", _rulePath));
        panel.Children.Add(SettingsUi.FieldRow("访问权限", _ruleAccess));
        panel.Children.Add(SettingsUi.FieldRow("允许用户（* 或账号）", _ruleUsers));
        panel.Children.Add(SettingsUi.FieldRow("允许组（可空）", _ruleRoles));
        var addBtn = SettingsUi.Button("添加规则", async (_, _) => await AddRuleAsync(), primary: true);
        addBtn.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(addBtn);

        _rulesList = new StackPanel();
        panel.Children.Add(_rulesList);
        _ = ReloadRulesAsync();
        return panel;
    }

    private IWebdavService? RuleSvc =>
        SettingsServiceSlots.WebdavService;

    private async Task ReloadRulesAsync()
    {
        _rulesList.Children.Clear();
        var svc = RuleSvc;
        if (svc is null)
        {
            _rulesList.Children.Add(SettingsUi.FogText("WebDAV 服务未注册：规则管理不可用。"));
            return;
        }
        IReadOnlyList<WebdavAuthoringRule> rules;
        try
        {
            rules = await svc.ListAuthoringRulesAsync();
        }
        catch (Exception e)
        {
            _rulesList.Children.Add(SettingsUi.FogText($"读取全局 Authoring 规则失败：{e.Message}"));
            return;
        }
        if (rules.Count == 0)
        {
            _rulesList.Children.Add(SettingsUi.FogText("暂无全局 Authoring 规则"));
            return;
        }
        foreach (WebdavAuthoringRule rule in rules)
        {
            WebdavAuthoringRule captured = rule;
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            row.Children.Add(SettingsUi.FogText(
                $"· {captured.extension} @ {captured.path} → {captured.access}（用户 {captured.users} / 组 {captured.roles}）"));
            var del = SettingsUi.Button("删除", async (_, _) =>
            {
                if (MessageBox.Show($"删除规则 {captured.extension} @ {captured.path}？", "删除确认",
                        MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                try
                {
                    await RuleSvc!.RemoveAuthoringRuleAsync(captured.extension, captured.path);
                    await ReloadRulesAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "删除失败");
                }
            });
            del.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(del);
            _rulesList.Children.Add(row);
        }
    }

    private async Task AddRuleAsync()
    {
        var svc = RuleSvc;
        if (svc is null) { MessageBox.Show("WebDAV 服务未注册", "添加失败"); return; }
        string access = _ruleAccess.SelectedItem is ComboBoxItem sel ? (string)sel.Tag : "Read,Write";
        try
        {
            await svc.AddAuthoringRuleAsync(new WebdavAuthoringRuleOpts
            {
                extension = _ruleExt.Text.Trim(),
                path = _rulePath.Text.Trim(),
                access = access,
                users = _ruleUsers.Text.Trim(),
                roles = _ruleRoles.Text.Trim(),
            });
            await ReloadRulesAsync();
            MessageBox.Show("已添加全局 Authoring 规则", "添加成功");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "添加失败");
        }
    }
}
