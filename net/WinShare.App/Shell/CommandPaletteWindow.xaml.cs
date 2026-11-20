using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinShare.App.Events;
using WinShare.Core.Contracts;

namespace WinShare.App.Shell;

/// <summary>
/// 命令面板（对齐 src/components/CommandPalette.tsx）：
/// 静态面 = 六路由导航 + 操作（新建共享 / 强制刷新 / 打开日志文件夹）；
/// 动态面（v2.2.0 第二轮接入，此前因「服务层未接」只做静态面）= 打开时后台一次性加载
/// 共享/用户/组/SMB 会话实体，query≥2 字符参与本地过滤，命中项直达对应页
/// （共享经 TakeAttention→搜索关键字定位）。加载失败对应组静默缺席——不臆造结果。
/// </summary>
public partial class CommandPaletteWindow : Window
{
    /// <summary>一条命令（TS Command：key/label/hint/group）。</summary>
    public sealed record Command(string Key, string Label, string Hint, string Group, Action Run);

    /// <summary>
    /// 面板行（v2.2.0 第五批）：分组标题行 + 命令行。
    /// 行内不再常驻解释性副标题——语义由分组标题与图标承载，数据上下文放行尾灰字。
    /// </summary>
    public sealed class PaletteRow
    {
        public bool IsHeader { get; init; }
        public string Label { get; init; } = "";
        public string Glyph { get; init; } = "";
        /// <summary>行尾文本：动态实体=数据上下文（路径/协议/主机）；静态命令=分组名。</summary>
        public string Context { get; init; } = "";
        public Command? Cmd { get; init; }
    }

    readonly List<Command> _static;
    List<Command> _entities = [];
    int _active;
    /// <summary>键位提示是否已展示过（会话级；首次之后不再常驻）。</summary>
    static bool _hintsShown;

    public CommandPaletteWindow()
    {
        InitializeComponent();

        _static = BuildCommands();
        _active = 0;

        Loaded += (_, _) =>
        {
            TxtQuery.Focus();
            TxtQuery.SelectAll();
            // v2.2.0 第五批：键位提示只首次出现（常驻键位说明属说明书式小字）
            KeyHints.Visibility = _hintsShown ? Visibility.Collapsed : Visibility.Visible;
            _hintsShown = true;
            RefreshList();
        };

        // v2.2.0 第二轮修复：Esc 关闭此前只挂在 TxtQuery.PreviewKeyDown——焦点一旦移出输入框，
        // Esc 就没人接，模态面板卡在屏幕中央关不掉（实测复现）。改窗口级 PreviewKeyDown。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };

        // v2.2.0 第二轮：动态实体一次性预载（窗口生命周期短，无 per-keystroke IPC）
        _ = LoadEntitiesAsync();
    }

    static List<Command> BuildCommands()
    {
        var cmds = new List<Command>
        {
            new("/", "仪表板", "概览共享与会话", "页面导航",
                () => AppServices.Navigation.Navigate("/")),
            new("/shares", "共享管理", "创建/编辑/删除共享", "页面导航",
                () => AppServices.Navigation.Navigate("/shares")),
            new("/users", "用户权限", "管理本地用户与权限", "页面导航",
                () => AppServices.Navigation.Navigate("/users")),
            new("/sessions", "会话监控", "查看并断开会话", "页面导航",
                () => AppServices.Navigation.Navigate("/sessions")),
            new("/settings", "服务器配置", "SMB/NFS/FTP/WebDAV 配置与预设", "页面导航",
                () => AppServices.Navigation.Navigate("/settings")),
            new("/app-settings", "应用设置", "版本 / 项目地址 / 偏好与运维 / 日志", "页面导航",
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
        AppShell.OpenLogDirectory();
    }

    /// <summary>动态实体预载（各源独立兜底：失败/服务缺席 → 对应组静默缺席，不阻塞面板）。</summary>
    async Task LoadEntitiesAsync()
    {
        static Task<IReadOnlyList<T>?> SafeAsync<T>(Func<Task<IReadOnlyList<T>?>> f) =>
            Task.Run(async () => { try { return await f(); } catch { return null; } });

        var sharesTask = SafeAsync(async () =>
            Shares.SharesServices.Share is { } svc ? await svc.ListSharesAsync() : null);
        var usersTask = SafeAsync(async () =>
            WinShare.App.Users.UsersModule.UserService is { } svc ? await svc.ListUsersAsync() : null);
        var groupsTask = SafeAsync(async () =>
            WinShare.App.Users.UsersModule.UserService is { } svc ? await svc.ListGroupsAsync() : null);
        var sessionsTask = SafeAsync(async () =>
            Sessions.SessionsModule.BuildDataFeed() is { } feed
                ? (await feed.GetAsync(Protocol.Smb)).Sessions
                : null);
        // v2.2.0 第二轮：NFS 会话一并接入（协议适配器槽位；缺席/失败静默）
        var nfsSessionsTask = SafeAsync(async () =>
            Sessions.SessionsModule.Adapters is { } ad &&
            ad.TryGetValue(Protocol.Nfs, out var nfs) ? await nfs.ListSessionsAsync() : null);

        await Task.WhenAll(sharesTask, usersTask, groupsTask, sessionsTask, nfsSessionsTask);

        IReadOnlyList<Share>? shares = null;
        try { shares = await sharesTask; } catch { }
        IReadOnlyList<LocalUser>? users = null;
        try { users = await usersTask; } catch { }
        IReadOnlyList<LocalGroup>? groups = null;
        try { groups = await groupsTask; } catch { }
        IReadOnlyList<ProtocolSession>? sessions = null;
        try { sessions = await sessionsTask; } catch { }
        try
        {
            var nfsSessions = await nfsSessionsTask;
            if (nfsSessions is { Count: > 0 })
            {
                sessions = sessions is { Count: > 0 } ? sessions.Concat(nfsSessions).ToList() : nfsSessions;
            }
        }
        catch { }

        // UI 线程重建动态结果（过滤/渲染在 UI 侧）
        Dispatcher.BeginInvoke(() =>
        {
            _entities = CommandPaletteSearch.BuildEntityCommands(
                shares, users, groups, sessions,
                (route, attention) => AppServices.Navigation.Navigate(route, attention));
            RefreshList();
        });
    }

    List<Command> Filtered
    {
        get
        {
            var q = TxtQuery.Text.Trim();
            var result = _static.Where(c =>
                q.Length == 0 ||
                c.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Hint.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            // 动态实体：≥2 字符才参与（对齐 TS 门控；1 字符的实体命中面太大）
            result.AddRange(_entities.Where(c => CommandPaletteSearch.Matches(c, q)));
            // v2.2.0 第二轮打磨：查询无结果时不再塌缩成空列表（只有底部快捷键条），
            // 显示一条不可执行的空态项，明确告诉用户「没有匹配」而非「面板坏了」
            if (q.Length > 0 && result.Count == 0)
            {
                result.Add(new Command("no-result", "没有匹配的结果", "试试共享名 / 用户名，或换个关键词", "提示", () => { }));
            }
            return result;
        }
    }

    void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        QueryHint.Visibility = TxtQuery.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshList();
    }

    /// <summary>各组图标（Segoe MDL2）：导航 / 操作 / 共享 / 用户 / 会话。</summary>
    static readonly Dictionary<string, string> GroupGlyphs = new()
    {
        ["页面导航"] = "",
        ["操作"] = "",
        ["共享"] = "",
        ["用户"] = "",
        ["会话"] = "",
    };

    /// <summary>组显示顺序（导航 → 操作 → 动态实体）。</summary>
    static readonly string[] GroupOrder = ["页面导航", "操作", "共享", "用户", "会话", "提示"];

    /// <summary>由命令列表构建面板行：组首插一行分组标题；动态实体行尾显示数据上下文。</summary>
    public static List<PaletteRow> BuildRows(List<Command> cmds)
    {
        var rows = new List<PaletteRow>();
        foreach (var group in GroupOrder)
        {
            var items = cmds.Where(c => c.Group == group).ToList();
            if (items.Count == 0) continue;
            rows.Add(new PaletteRow { IsHeader = true, Label = group });
            foreach (var c in items)
            {
                bool dynamicRow = group is "共享" or "用户" or "会话";
                rows.Add(new PaletteRow
                {
                    Label = c.Label,
                    Glyph = GroupGlyphs.TryGetValue(group, out var g) ? g : "\uE721",
                    Context = dynamicRow ? c.Hint : c.Group,
                    Cmd = c,
                });
            }
        }
        // 未登记分组兜底追加（不丢结果）
        foreach (var c in cmds.Where(c => !GroupOrder.Contains(c.Group)))
        {
            rows.Add(new PaletteRow
            {
                Label = c.Label,
                Glyph = "\uE721",
                Context = c.Group,
                Cmd = c,
            });
        }
        return rows;
    }

    void RefreshList()
    {
        var cmds = Filtered;
        List.ItemsSource = BuildRows(cmds);
        CountText.Text = cmds.Count == 0 ? "无结果" : $"{cmds.Count} 条";
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
        if (sender is Border { Tag: PaletteRow row } && row.Cmd is { } cmd) Run(cmd);
    }

    // v2.2.0 第二轮：鼠标悬停反馈（此前仅键盘选中行有高亮，鼠标移过无任何响应）
    void OnRowMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b && b.Tag is PaletteRow r && r.Cmd is { } c && !IsKeyboardActive(c))
            b.Background = TryFindResource("BrushSoftGradient") as System.Windows.Media.Brush
                ?? b.Background;
    }

    void OnRowMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b && b.Tag is PaletteRow r && r.Cmd is { } c && !IsKeyboardActive(c))
            b.Background = System.Windows.Media.Brushes.Transparent;
    }

    bool IsKeyboardActive(Command c)
    {
        var items = Filtered;
        return _active >= 0 && _active < items.Count && items[_active].Key == c.Key;
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
        var activeBrush = TryFindResource("BrushSoftGradient") as System.Windows.Media.Brush
            ?? TryFindResource("BrushBgMist") as System.Windows.Media.Brush;
        var activeKey = items.Count > 0 ? items[_active].Key : null;
        // 行与命令不再一一对应（插入了分组标题行）→ 按命令 Key 匹配高亮行
        var rows = List.ItemsSource as IEnumerable<PaletteRow>;
        var rowList = rows?.ToList() ?? new List<PaletteRow>();
        for (int i = 0; i < rowList.Count; i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            if (cp.ContentTemplate?.FindName("Row", cp) is not Border row) continue;
            bool isActive = activeKey is not null && rowList[i].Cmd?.Key == activeKey;
            row.Background = isActive ? activeBrush : System.Windows.Media.Brushes.Transparent;
        }
        int activeRowIndex = rowList.FindIndex(r => r.Cmd?.Key == activeKey);
        if (activeRowIndex >= 0 && List.ItemContainerGenerator.ContainerFromIndex(activeRowIndex) is ContentPresenter active)
        {
            active.BringIntoView();
        }
    }
}
