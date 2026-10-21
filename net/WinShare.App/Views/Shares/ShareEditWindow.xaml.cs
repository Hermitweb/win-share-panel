using System.Windows;
using System.Windows.Controls;
using WinShare.App.Shares;

namespace WinShare.App.Views.Shares;

/// <summary>
/// 编辑共享窗口（对齐 Shares.tsx 编辑 Modal：名称/路径只读，协议专有可改项；
/// root 访问属深水区——新手模式不渲染也不提交）。
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
        SyncToForm();
        var result = await _vm.SaveEditAsync();
        if (string.IsNullOrEmpty(result))
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
