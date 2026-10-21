using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        Loaded += (_, _) => SyncFromForm();
        _vm.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(SyncFromForm);
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

            // SMB 块
            TxtFull.Text = Form.FullAccess;
            TxtChange.Text = Form.ChangeAccess;
            TxtRead.Text = Form.ReadAccess;
            TxtNoAccess.Text = Form.NoAccess;
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
            CmbPreset.ItemsSource = _vm.CreatePresets.Select(p => new ComboBoxItem
            {
                Content = $"{p.name} - {p.description}",
                Tag = p.id,
            }).ToList();
            CmbPreset.SelectedIndex = -1;
            if (Form.PresetId is { Length: > 0 } pid &&
                CmbPreset.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == pid) is { } item)
            {
                CmbPreset.SelectedItem = item;
            }

            // 场景预设按钮（新手模式收敛高级预设）
            BuildScenarioButtons();

            // 候选账号提示（TS loadCandidates 展示前 12 + "等"）
            var cands = _vm.CreateAccountOptions;
            LblCandidates.Text = cands.Count == 0 ? ""
                : $"常用账号：{string.Join("、", cands.Take(12))}{(cands.Count > 12 ? " 等" : "")}";

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
            var btn = new Button
            {
                Content = preset.Name,
                FontSize = 12,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = preset.Tip ?? preset.Name,
            };
            btn.Click += (_, _) =>
            {
                Form.ApplyScenario(preset);
                _syncing = true;
                SyncFromForm();
                _syncing = false;
                LblError.Text = $"已套用「{preset.Name}」预设";
                LblError.Foreground = Brushes.DimGray;
                LblError.Visibility = Visibility.Visible;
            };
            ScenarioPanel.Children.Add(btn);
        }
        if (_vm.CreateScenarios.Count == 0)
            ScenarioPanel.Children.Add(new TextBlock
            {
                Text = "（当前协议无可用场景预设）",
                FontSize = 11,
                Foreground = Brushes.DimGray,
            });
    }

    void SyncNfsAuthCheckboxes()
    {
        NfsAuthPanel.Children.Clear();
        var selected = Form.Authentication.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (label, value) in new[] { ("Krb5", "krb5"), ("Krb5i", "krb5i"), ("Krb5p", "krb5p"), ("AUTH_SYS", "sys") })
        {
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = selected.Contains(value),
                Margin = new Thickness(0, 0, 12, 0),
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
            LblError.Text = err!;
            LblError.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F));
            LblError.Visibility = Visibility.Visible;
            return;
        }
        var result = await _vm.CreateShareAsync();
        if (string.IsNullOrEmpty(result))
        {
            Close();
        }
        else
        {
            LblError.Text = result;
            LblError.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F));
            LblError.Visibility = Visibility.Visible;
        }
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