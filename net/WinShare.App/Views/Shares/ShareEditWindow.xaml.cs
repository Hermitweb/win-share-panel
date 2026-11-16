using System.Windows;
using System.Windows.Controls;
using WinShare.App.Shares;

namespace WinShare.App.Views.Shares;

/// <summary>
/// 编辑共享窗口（对齐 Shares.tsx 编辑 Modal；v2.1 面板解锁：名称/路径可改——
/// 改名/改路径会经重建通道保留授权，保存前弹确认；root 访问属深水区——新手模式不渲染也不提交）。
/// </summary>
public partial class ShareEditWindow : Window
{
    readonly SharesViewModel _vm;
    readonly ShareEditFormVm _form;

    public ShareEditWindow(SharesViewModel vm, ShareEditFormVm form)
    {
        _vm = vm;
        _form = form;
        InitializeComponent();
        Title = $"编辑共享：{form.Name}";
        LoadFromForm();
    }

    void LoadFromForm()
    {
        TxtName.Text = _form.Name;
        TxtPath.Text = _form.Path;
        TxtDescription.Text = _form.Description;
        BlockNfs.Visibility = _form.Protocol == WinShare.Core.Contracts.Protocol.Nfs ? Visibility.Visible : Visibility.Collapsed;
        BlockFtp.Visibility = _form.Protocol == WinShare.Core.Contracts.Protocol.Ftp ? Visibility.Visible : Visibility.Collapsed;
        BlockWebdav.Visibility = _form.Protocol == WinShare.Core.Contracts.Protocol.Webdav ? Visibility.Visible : Visibility.Collapsed;
        ChkEditRoot.Visibility = _vm.AdvancedMode ? Visibility.Visible : Visibility.Collapsed;
        if (_form.Protocol == WinShare.Core.Contracts.Protocol.Nfs)
        {
            RadEditRw.IsChecked = _form.NfsPermission != "ro";
            RadEditRo.IsChecked = _form.NfsPermission == "ro";
            ChkEditRoot.IsChecked = _form.AllowRootAccess;
        }
        if (_form.Protocol == WinShare.Core.Contracts.Protocol.Ftp)
        {
            RadEditSslAllow.IsChecked = _form.SslPolicy == "SslAllow";
            RadEditSslRequire.IsChecked = _form.SslPolicy == "SslRequire";
            RadEditSslCred.IsChecked = _form.SslPolicy == "SslRequireCredentials";
            RadEditAnon.IsChecked = _form.AuthMode == "anonymous";
            RadEditBasic.IsChecked = _form.AuthMode == "basic";
            RadEditWin.IsChecked = _form.AuthMode == "windows";
        }
        if (_form.Protocol == WinShare.Core.Contracts.Protocol.Webdav)
        {
            ChkEditWdavAnon.IsChecked = _form.AnonymousEnabled;
        }
    }

    void SyncToForm()
    {
        _form.NewName = TxtName.Text.Trim();
        _form.NewPath = TxtPath.Text.Trim();
        _form.Description = TxtDescription.Text;
        _form.NfsPermission = RadEditRo.IsChecked == true ? "ro" : "rw";
        _form.AllowRootAccess = ChkEditRoot.IsChecked == true;
        _form.SslPolicy = RadEditSslRequire.IsChecked == true ? "SslRequire"
            : RadEditSslCred.IsChecked == true ? "SslRequireCredentials" : "SslAllow";
        _form.AuthMode = RadEditAnon.IsChecked == true ? "anonymous"
            : RadEditWin.IsChecked == true ? "windows" : "basic";
        _form.AnonymousEnabled = ChkEditWdavAnon.IsChecked == true;
    }

    async void OnSave(object sender, RoutedEventArgs e)
    {
        string newName = TxtName.Text.Trim();
        string newPath = TxtPath.Text.Trim();
        // v2.1：改名/改路径会走重建通道（移除→按新值重建→授权回放），中断现存连接，保存前确认
        bool rename = !string.IsNullOrEmpty(newName) && newName != _form.Name;
        bool move = !string.IsNullOrEmpty(newPath) && newPath != _form.Path;
        if (rename || move)
        {
            string what = rename && move ? "共享名与路径" : rename ? "共享名" : "路径";
            MessageBoxResult ok = MessageBox.Show(
                $"修改{what}需要重建共享：现存连接将中断，共享授权与高级属性会按快照回放。继续？",
                "重建确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.Yes) return;
        }
        SyncToForm();
        var result = await _vm.SaveEditAsync();
        // SaveEditAsync 契约：成功返回"已保存"（并同步 StatusMsg），失败返回错误文案
        if (string.IsNullOrEmpty(result) || result == "已保存")
        {
            Close();
        }
        else
        {
            LblError.Text = result;
            LblError.Visibility = Visibility.Visible;
        }
    }

    void OnCancel(object sender, RoutedEventArgs e) => Close();
}
