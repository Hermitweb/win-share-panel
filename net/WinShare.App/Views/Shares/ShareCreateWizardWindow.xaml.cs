using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinShare.App.Shares;

namespace WinShare.App.Views.Shares;

/// <summary>
/// 新建共享向导窗口（对齐 Shares.tsx 新建 Modal + CREATE_SCENARIO_PRESETS +
/// fillPathAndName/浏览/场景预设交互）：界面逻辑在 ShareCreateFormVm，
/// 本窗口只做控件 ↔ 表单字段同步与协议块显隐。
/// </summary>
public partial class ShareCreateWizardWindow : Window
{
    readonly SharesViewModel _vm;
    ShareCreateFormVm Form => _vm.CreateForm;

    // 程序化同步期间抑制 TextChanged（自动值不得被误判为"用户手改"）
    bool _syncing;

    public ShareCreateWizardWindow(SharesViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Shell.UiTestHooks.Prepare(this);
        Loaded += (_, _) => SyncFromForm();
        _vm.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(SyncFromForm);
        // v2.2.0 第二轮：Esc 关闭（向导为 Show() 非模态，IsCancel 不生效）
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _vm.CloseCreateWizard();
                Close();
                e.Handled = true;
            }
        };
    }

    // ===== 表单 → 控件 =====
    void SyncFromForm()
    {
        _syncing = true;
        try
        {
            // 协议
            var proto = Form.Protocol;
            var idx = proto switch { "nfs" => 1, "ftp" => 2, "webdav" => 3, _ => 0 };
            if (CmbProtocol.SelectedIndex != idx) CmbProtocol.SelectedIndex = idx;
            OptNfs.IsEnabled = !_vm.NfsBlocked;
            OptFtp.IsEnabled = !_vm.IisBlocked;
            OptWebdav.IsEnabled = !_vm.IisBlocked;
            if (!_vm.NfsBlocked)
                OptNfs.Content = "NFS（网络文件系统）";
            else
                OptNfs.Content = "NFS（网络文件系统） — 本机为 NFS 客户端系统，不支持创建";
            if (!_vm.IisBlocked)
            {
                OptFtp.Content = "FTP（IIS FTP 站点）";
                OptWebdav.Content = "WebDAV（IIS WebDAV 站点）";
            }
            else
            {
                OptFtp.Content = "FTP（IIS FTP 站点） — 本机系统不支持 IIS";
                OptWebdav.Content = "WebDAV（IIS WebDAV 站点） — 本机系统不支持 IIS";
            }

            // 通用字段
            TxtName.Text = Form.Name;
            TxtPath.Text = Form.Path;
            TxtDescription.Text = Form.Description;

            // SMB 块（v2.2.0：访问控制四框为可编辑下拉——候选=本地用户+组；引用相等判断避免输入中重置候选）
            SyncAccountCombo(TxtFull, Form.FullAccess);
            SyncAccountCombo(TxtChange, Form.ChangeAccess);
            SyncAccountCombo(TxtRead, Form.ReadAccess);
            SyncAccountCombo(TxtNoAccess, Form.NoAccess);
            ChkEncrypted.IsChecked = Form.Encrypted;
            ChkEncryptData.IsChecked = Form.EncryptData;
            ChkShadowCopy.IsChecked = Form.ShareShadowCopy;
            SetComboTag(CmbFem, Form.FolderEnumerationMode, "Unrestricted");
            SetComboTag(CmbCaching, Form.CachingMode, "Manual");
            TxtConcurrentLimit.Text = Form.ConcurrentUserLimit > 0 ? Form.ConcurrentUserLimit.ToString() : "";

            // NFS 块
            SyncNfsAuthCheckboxes();
            RadNfsRw.IsChecked = Form.NfsPermission != "ro";
            RadNfsRo.IsChecked = Form.NfsPermission == "ro";
            ChkUnmapped.IsChecked = Form.EnableUnmappedAccess;
            ChkAllowRoot.IsChecked = Form.AllowRootAccess;
            TxtAnonUid.Text = Form.AnonymousUid > 0 ? Form.AnonymousUid.ToString() : "0";
            TxtAnonGid.Text = Form.AnonymousGid > 0 ? Form.AnonymousGid.ToString() : "0";

            // FTP / WebDAV 块
            if (Form.Protocol == "ftp") TxtFtpPort.Text = (Form.Port > 0 ? Form.Port : 21).ToString();
            if (Form.Protocol == "webdav") TxtWdavPort.Text = (Form.Port > 0 ? Form.Port : 80).ToString();
            RadFtpSslAllow.IsChecked = Form.SslPolicy == "SslAllow";
            RadFtpSslRequire.IsChecked = Form.SslPolicy == "SslRequire";
            RadFtpSslCred.IsChecked = Form.SslPolicy == "SslRequireCredentials";
            RadFtpAnon.IsChecked = Form.AuthMode == "anonymous";
            RadFtpBasic.IsChecked = Form.AuthMode == "basic";
            RadFtpWin.IsChecked = Form.AuthMode == "windows";
            ChkWdavAnon.IsChecked = Form.AnonymousEnabled;

            // 权限模板下拉（SMB；TS presetId 选择器）
            CmbPreset.ItemsSource = null;
            // v2.2.0 第五批：首项占位「不使用模板」（此前空下拉像坏掉的控件）
            CmbPreset.Items.Add(new ComboBoxItem { Content = "不使用模板" });
            foreach (var p in _vm.CreatePresets)
            {
                CmbPreset.Items.Add(new ComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(p.description) ? p.name : $"{p.name} - {p.description}",
                    Tag = p.id,
                });
            }
            CmbPreset.SelectedIndex = 0;
            if (Form.PresetId is { Length: > 0 } pid &&
                CmbPreset.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == pid) is { } item)
            {
                CmbPreset.SelectedItem = item;
            }

            // 场景预设按钮（新手模式收敛高级预设）
            BuildScenarioButtons();

            // 新手模式高级项收敛（不渲染 = 不提交，表单层再兜一道）
            var adv = _vm.AdvancedMode;
            LblEncrypted.Visibility = adv ? Visibility.Visible : Visibility.Collapsed;
            ChkEncrypted.Visibility = adv ? Visibility.Visible : Visibility.Collapsed;
            ExpAdvanced.Visibility = adv ? Visibility.Visible : Visibility.Collapsed;
            NfsAdvancedPanel.Visibility = adv ? Visibility.Visible : Visibility.Collapsed;

            UpdateProtocolBlocks();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>访问控制下拉候选同步：候选列表按引用缓存（VM 侧按 bundle 缓存），不变不重置——避免用户输入中被清空。</summary>
    void SyncAccountCombo(System.Windows.Controls.ComboBox combo, string text)
    {
        var options = _vm.CreateAccountOptions;
        if (!ReferenceEquals(combo.Tag, options))
        {
            combo.ItemsSource = options;
            combo.Tag = options;
        }
        if (combo.Text != text) combo.Text = text;
    }

    void UpdateProtocolBlocks()
    {
        var proto = Form.Protocol;
        BlockSmb.Visibility = proto == "smb" ? Visibility.Visible : Visibility.Collapsed;
        BlockNfs.Visibility = proto == "nfs" ? Visibility.Visible : Visibility.Collapsed;
        BlockFtp.Visibility = proto == "ftp" ? Visibility.Visible : Visibility.Collapsed;
        BlockWebdav.Visibility = proto == "webdav" ? Visibility.Visible : Visibility.Collapsed;
    }

    void BuildScenarioButtons()
    {
        ScenarioPanel.Children.Clear();
        foreach (var preset in _vm.CreateScenarios)
        {
            // 1 列纵向流：场景预设按钮自上而下堆叠（DashButton：与其他按钮统一 token 视觉）
            var btn = new Button
            {
                Content = preset.Name,
                Style = (Style?)TryFindResource("DashButton"),
                FontSize = 12,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 0, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = preset.Tip ?? preset.Name,
            };
            btn.Click += (_, _) =>
            {
                Form.ApplyScenario(preset);
                _syncing = true;
                SyncFromForm();
                _syncing = false;
                LblError.Text = $"已套用「{preset.Name}」预设";
                LblError.Foreground = (Brush?)TryFindResource("BrushFog");
                LblError.Visibility = Visibility.Visible;
            };
            ScenarioPanel.Children.Add(btn);
        }
        if (_vm.CreateScenarios.Count == 0)
            ScenarioPanel.Children.Add(new TextBlock
            {
                Text = "（当前协议无可用场景预设）",
                FontSize = 11,
                Foreground = (Brush?)TryFindResource("BrushFog"),
            });
    }

    void SyncNfsAuthCheckboxes()
    {
        NfsAuthPanel.Children.Clear();
        var selected = Form.Authentication.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (label, value) in new[] { ("Krb5", "krb5"), ("Krb5i", "krb5i"), ("Krb5p", "krb5p"), ("AUTH_SYS", "sys") })
        {
            // 1 列纵向流：NFS 认证方式多选自上而下堆叠（CheckBoxSoft：与应用设置页勾选框同款）
            var cb = new CheckBox
            {
                Content = label,
                Style = (Style?)TryFindResource("CheckBoxSoft"),
                IsChecked = selected.Contains(value),
                Margin = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = value,
            };
            cb.Click += (_, _) =>
            {
                var vals = NfsAuthPanel.Children.OfType<CheckBox>()
                    .Where(c => c.IsChecked == true)
                    .Select(c => (string)c.Tag!)
                    .ToList();
                Form.Authentication = string.Join(",", vals);
            };
            NfsAuthPanel.Children.Add(cb);
        }
    }

    static void SetComboTag(ComboBox combo, string value, string fallback)
    {
        var item = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == value || (string?)i.Tag == fallback);
        combo.SelectedItem = item;
    }

    // ===== 控件 → 表单 =====
    void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        // TS nameTouchedRef：用户一旦手改过名字，路径变化就不再覆盖它
        Form.Name = TxtName.Text;
        Form.NameTouched = TxtName.Text.Length > 0;
    }

    void OnPathChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        // TS handlePathInput：路径手输变化同样带名（未手改名字时）
        Form.OnPathText(TxtPath.Text);
        TxtName.Text = Form.Name; // 同步自动名（守卫下不会误触 NameTouched）
        _syncing = true;
        _syncing = false;
    }

    void OnProtocolChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (CmbProtocol.SelectedItem is ComboBoxItem ci && ci.Tag is string tag)
        {
            Form.Protocol = tag;
            // 切协议默认端口（TS initialValue 语义：ftp=21 / webdav=80；用户已填不动）
            if (tag == "ftp" && Form.Port <= 0) Form.Port = 21;
            if (tag == "webdav" && Form.Port <= 0) Form.Port = 80;
            // TS：协议切离 smb 清空 presetId
            if (tag != "smb") Form.PresetId = null;
            SyncFromForm();
        }
    }

    void OnNfsPermChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null || RadNfsRo is null) return;
        Form.NfsPermission = RadNfsRo.IsChecked == true ? "ro" : "rw";
    }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        // 原生文件夹选择（TS api.system.selectFolder）
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择本地路径" };
        if (dlg.ShowDialog() == true)
        {
            _vm.PickedFolder(dlg.FolderName);
            TxtPath.Text = Form.Path;
            TxtName.Text = Form.Name;
        }
    }

    // ===== 提交（TS handleCreate：校验 → 按协议提交（新手模式收敛）→ preset 应用 → 复位） =====
    async void OnCreate(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        SyncControlsToForm();
        _syncing = false;

        if (!Form.Validate(out var err))
        {
            ShowWizardError(err!);
            return;
        }
        // v2.2.0：端口/UID/GID/并发上限非法不再静默归 0——提交前就地红字说明（此前填错被悄悄替换成默认值）
        string? numericErr = ValidateNumerics();
        if (numericErr is not null)
        {
            ShowWizardError(numericErr);
            return;
        }
        // v2.2.0 安装与运行配置：FTP/WebDAV 端口占用预检（创建前拦截，友好提示）
        string? portErr = await CheckPortFreeAsync();
        if (portErr is not null)
        {
            ShowWizardError(portErr);
            return;
        }
        var result = await _vm.CreateShareAsync();
        if (string.IsNullOrEmpty(result))
        {
            // v2.2.0：成功反馈带「别人怎么访问」（UNC/URL 由 VM 生成），替代过去只有列表刷新
            string hint = _vm.LastCreatedAccessHint;
            Shell.ToastHost.Show(Shell.ToastLevel.Success,
                string.IsNullOrEmpty(hint) ? "共享创建成功" : $"共享创建成功，{hint}");
            Close();
        }
        else
        {
            ShowWizardError(result);
        }
    }

    /// <summary>向导内联错误（红字；只提示不清表单——改改就能重试）。</summary>
    void ShowWizardError(string text)
    {
        LblError.Text = text;
        LblError.Foreground = (System.Windows.Media.Brush?)TryFindResource("BrushDangerStrong");
        LblError.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// v2.2.0 安装与运行配置：FTP/WebDAV 端口占用预检。
    /// 本机有进程监听该端口（含 IIS 自身站点）→ 创建同端口站点必然失败，提前拦截给换端口建议。
    /// </summary>
    async Task<string?> CheckPortFreeAsync()
    {
        int? port = Form.Protocol switch
        {
            "ftp" => Form.Port > 0 ? Form.Port : 21,
            "webdav" => Form.Port > 0 ? Form.Port : 80,
            _ => null,
        };
        if (port is null) return null;
        try
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port.Value);
            probe.Start();
            probe.Stop();
            return null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return $"端口 {port} 已被本机其它程序占用：请更换一个端口，或先停掉占用它的服务再创建。";
        }
    }

    /// <summary>数字类输入即时校验（空=保持默认；填了就必须合法，不再静默归 0）。</summary>
    string? ValidateNumerics()
    {
        if (Form.Protocol == "ftp" && TxtFtpPort.Text.Trim().Length > 0 &&
            (!int.TryParse(TxtFtpPort.Text.Trim(), out int fp) || fp is < 1 or > 65535))
            return "FTP 端口需为 1-65535 的数字";
        if (Form.Protocol == "webdav" && TxtWdavPort.Text.Trim().Length > 0 &&
            (!int.TryParse(TxtWdavPort.Text.Trim(), out int wp) || wp is < 1 or > 65535))
            return "WebDAV 端口需为 1-65535 的数字";
        if (Form.Protocol == "nfs")
        {
            if (TxtAnonUid.Text.Trim().Length > 0 && (!int.TryParse(TxtAnonUid.Text.Trim(), out int uid) || uid < 0))
                return "匿名 UID 需为非负整数（留空或 0 = 默认）";
            if (TxtAnonGid.Text.Trim().Length > 0 && (!int.TryParse(TxtAnonGid.Text.Trim(), out int gid) || gid < 0))
                return "匿名 GID 需为非负整数（留空或 0 = 默认）";
        }
        if (TxtConcurrentLimit.Text.Trim().Length > 0 &&
            (!int.TryParse(TxtConcurrentLimit.Text.Trim(), out int lim) || lim < 0))
            return "并发用户上限需为非负整数（留空 = 不限制）";
        return null;
    }

    void SyncControlsToForm()
    {
        Form.Name = TxtName.Text;
        Form.Path = TxtPath.Text;
        Form.Description = TxtDescription.Text;

        Form.FullAccess = TxtFull.Text;
        Form.ChangeAccess = TxtChange.Text;
        Form.ReadAccess = TxtRead.Text;
        Form.NoAccess = TxtNoAccess.Text;
        Form.Encrypted = ChkEncrypted.IsChecked == true;
        Form.EncryptData = ChkEncryptData.IsChecked == true;
        Form.ShareShadowCopy = ChkShadowCopy.IsChecked == true;
        Form.FolderEnumerationMode = (CmbFem.SelectedItem as ComboBoxItem)?.Tag as string ?? "Unrestricted";
        Form.CachingMode = (CmbCaching.SelectedItem as ComboBoxItem)?.Tag as string ?? "Manual";
        Form.ConcurrentUserLimit = int.TryParse(TxtConcurrentLimit.Text, out var limit) ? limit : 0;

        var nfsVals = NfsAuthPanel.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Tag!)
            .ToList();
        Form.Authentication = string.Join(",", nfsVals);
        Form.EnableUnmappedAccess = ChkUnmapped.IsChecked == true;
        Form.AllowRootAccess = ChkAllowRoot.IsChecked == true;
        Form.AnonymousUid = int.TryParse(TxtAnonUid.Text, out var uid) ? uid : 0;
        Form.AnonymousGid = int.TryParse(TxtAnonGid.Text, out var gid) ? gid : 0;

        if (Form.Protocol == "ftp" && int.TryParse(TxtFtpPort.Text, out var ftpPort))
            Form.Port = ftpPort;
        if (Form.Protocol == "webdav" && int.TryParse(TxtWdavPort.Text, out var wdPort))
            Form.Port = wdPort;
        Form.SslPolicy = RadFtpSslRequire.IsChecked == true ? "SslRequire"
            : RadFtpSslCred.IsChecked == true ? "SslRequireCredentials" : "SslAllow";
        Form.AuthMode = RadFtpAnon.IsChecked == true ? "anonymous"
            : RadFtpWin.IsChecked == true ? "windows" : "basic";
        Form.AnonymousEnabled = ChkWdavAnon.IsChecked == true;

        Form.PresetId = (CmbPreset.SelectedItem as ComboBoxItem)?.Tag as string ?? null;
    }

    void OnCancel(object sender, RoutedEventArgs e)
    {
        _vm.CloseCreateWizard();
        Close();
    }
}