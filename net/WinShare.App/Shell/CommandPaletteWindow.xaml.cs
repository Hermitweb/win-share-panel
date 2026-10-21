using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinShare.App.Events;

namespace WinShare.App.Shell;

/// <summary>
/// 命令面板（对齐 src/components/CommandPalette.tsx 的静态面：
/// STATIC_NAV 六路由 + actionCommands（新建共享 / 强制刷新 / 打开日志文件夹），
/// query 过滤 + ↑↓ 选择 + Enter 执行 + Esc 关闭）。
/// 动态跨域搜索（共享/用户/会话，TS query≥2 字符门控）依赖各模块数据源；
/// 服务层未接，按「不臆造结果」原则只实现静态面。
/// </summary>
public partial class CommandPaletteWindow : Window
{
    /// <summary>一条命令（TS Command：key/label/hint/group）。</summary>
    public sealed record Command(string Key, string Label, string Hint, string Group, Action Run);

    readonly List<Command> _all;
    int _active;

    public CommandPaletteWindow()
    {
        InitializeComponent();

        _all = BuildCommands();
        List.ItemsSource = _all;
        _active = 0;

        Loaded += (_, _) =>
        {
            TxtQuery.Focus();
            TxtQuery.SelectAll();
            ApplyActive();
        };
    }

    static List<Command> BuildCommands()
    {
        var cmds = new List<Command>
        {
            new("/", "前往：仪表板", "概览共享与会话", "页面导航",
                () => AppServices.Navigation.Navigate("/")),
            new("/shares", "前往：共享管理", "创建/编辑/删除共享", "页面导航",
                () => AppServices.Navigation.Navigate("/shares")),
            new("/users", "前往：用户权限", "管理本地用户与权限", "页面导航",
                () => AppServices.Navigation.Navigate("/users")),
            new("/sessions", "前往：会话监控", "查看并断开会话", "页面导航",
                () => AppServices.Navigation.Navigate("/sessions")),
            new("/settings", "前往：服务器配置", "SMB/NFS/FTP/WebDAV 配置与预设", "页面导航",
                () => AppServices.Navigation.Navigate("/settings")),
            new("/app-settings", "前往：应用设置", "版本 / 项目地址 / 偏好与运维 / 日志", "页面导航",
                () => AppServices.Navigation.Navigate("/app-settings")),
        };

        // 操作类（TS actionCommands；导航 → 动作 与 taskbook 边界一致：只调已存在的面）
        cmds.Add(new Command("act-create", "新建共享", "跳到共享管理页", "操作",
            () => AppServices.Navigation.Navigate("/shares")));
        cmds.Add(new Command("act-refresh", "强制刷新当前视图", "重新拉取共享/会话/统计（F5）", "操作",
            () => AppServices.Bus.Publish(new RefreshRequested())));
        cmds.Add(new Command("act-logfolder", "打开日志文件夹", "%APPDATA%\\WinSharePanel\\logs", "操作",
            OpenLogFolder));

        return cmds;
    }

    static void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogSink.LogDir());
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(LogSink.LogDir()) { UseShellExecute = true });
        }
        catch
        {
            // 打开失败静默（TS 同语义：catch 不提示）
        }
    }

    List<Command> Filtered
    {
        get
        {
            var q = TxtQuery.Text.Trim();
            if (q.Length == 0) return _all;
            return _all.Where(c =>
                c.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Hint.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        List.ItemsSource = Filtered;
        _active = 0;
        ApplyActive();
    }

    void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        var items = Filtered;
        switch (e.Key)
        {
            case Key.Down:
                if (_active < items.Count - 1) _active++;
                ApplyActive();
                e.Handled = true;
                break;
            case Key.Up:
                if (_active > 0) _active--;
                ApplyActive();
                e.Handled = true;
                break;
            case Key.Enter:
                if (_active >= 0 && _active < items.Count) Run(items[_active]);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: Command cmd }) Run(cmd);
    }

    void Run(Command cmd)
    {
        cmd.Run();
        Close();
    }

    void ApplyActive()
    {
        var items = Filtered;
        if (_active >= items.Count) _active = items.Count - 1;
        if (_active < 0) _active = 0;
        // ItemsControl 无选中态概念：高亮经容器视觉刷选（当前用索引定位到可视区即可）
        if (List.ItemContainerGenerator.ContainerFromIndex(_active) is ContentPresenter cp &&
            cp.ContentTemplate != null)
        {
            cp.BringIntoView();
        }
    }
}
