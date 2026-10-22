using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinShare.App.Shares;
using WinShare.App.Views.Shares;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Shares;

/// <summary>
/// 共享页抽屉接线 + 快捷键补缺测试（T-W A 任务：UI 层缺失修补）。
/// [Source: src/pages/Shares.tsx#actions 列详情/权限按钮 → ShareDetailDrawer/PermissionDrawer]
/// [Source: src/pages/Shares.tsx#977 快捷键提示文案（Ctrl+N / Del / Space / F5）]
/// 覆盖原版有、WPF 版缺的两项：
/// ① 详情/权限抽屉宿主里控件缺失（原 TODO no-op）→ 宿主内已声明抽屉且默认折叠；
/// ② Ctrl+N / Del / Space 快捷键无等价物（原版页面级 hotkey）。
/// 纪律：WPF 对象只能在其创建的 STA 线程上访问 —— 断言一律在 STA 块内完成，
/// 只把「结论数据」（bool/字符串）带出，绝不让 UI 对象跨线程（否则 InvalidOperationException）。
/// </summary>
public class ShareDrawerWiringTests
{
    // ===== ① 抽屉宿主接线 =====

    [Source("[Source: src/pages/Shares.tsx#setDetailShare/setDetailOpen → ShareDetailDrawer open]")]
    [Fact]
    public void DetailHost_声明ShareDetailDrawer_且默认折叠()
    {
        var r = ProbeDrawer("DetailHost", "DetailDrawer", typeof(ShareDetailDrawer));
        AssertDrawerWired(r, nameof(ShareDetailDrawer));
    }

    [Source("[Source: src/pages/Shares.tsx#setPermShare/setPermOpen → PermissionDrawer open]")]
    [Fact]
    public void PermHost_声明SharePermissionDrawer_且默认折叠()
    {
        var r = ProbeDrawer("PermHost", "PermDrawer", typeof(SharePermissionDrawer));
        AssertDrawerWired(r, nameof(SharePermissionDrawer));
    }

    [Source("[Source: src/pages/Shares.tsx#ShareDetailDrawer.tsx SetShare(share) 填表]")]
    [Fact]
    public void DetailDrawer_SetShare_可重复调用不抛_逻辑父唯一()
    {
        // 原崩溃根因回归：抽屉常驻宿主、只换数据 → 不得抛「已是另一元素逻辑子元素」
        Exception? failure = null;
        RunOnSta(() =>
        {
            try
            {
                var drawer = new ShareDetailDrawer();
                var vm = MakeVm();
                drawer.SetShare(TestShares.Smb("Docs"), vm.MakeDetailVm(TestShares.Smb("Docs")));
                drawer.SetShare(TestShares.Smb("Docs2"), vm.MakeDetailVm(TestShares.Smb("Docs2")));
            }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
    }

    // ===== ② 快捷键等价物 =====

    [Source("[Source: src/pages/Shares.tsx#977 快捷键提示文案]")]
    [Fact]
    public void 页面提示文案_含全部四个快捷键()
    {
        string? tip = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try
            {
                var page = new SharesPageView();
                tip = AllLogical<TextBlock>(page)
                    .Select(t => t.Text)
                    .FirstOrDefault(t => t is not null && t.StartsWith("提示：拖拽文件夹到本页可快速创建共享", StringComparison.Ordinal));
            }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        Assert.NotNull(tip);
        Assert.Contains("Ctrl+N", tip);
        Assert.Contains("Del", tip);
        Assert.Contains("Space", tip);
        Assert.Contains("F5", tip);
    }

    [Source("[Source: src/pages/Shares.tsx#useTickEffect(shareDeleteTick/shareToggleTick) + shareCreateOpen]")]
    [Fact]
    public void 页面根节点_挂PreviewKeyDown_且代码后置有处理器()
    {
        // 处理器存在 + XAML 把事件接到根节点，两者缺一即快捷键失效
        var handler = typeof(SharesPageView).GetMethod("OnPageKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(handler);

        var xaml = File.ReadAllText(XamlPath("Views/Shares/SharesPageView.xaml"));
        Assert.Contains("PreviewKeyDown=\"OnPageKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"True\"", xaml, StringComparison.Ordinal);
    }

    // ===== helpers =====

    /// <summary>抽屉宿主探针结果（纯数据，可安全跨线程断言）。</summary>
    sealed record DrawerProbe(bool HostFound, bool HostCollapsedByDefault, bool DrawerFound,
        Type? DrawerType, bool RightDocked);

    static DrawerProbe ProbeDrawer(string hostName, string drawerName, Type expected)
    {
        var r = new DrawerProbe(false, false, false, null, false);
        Exception? failure = null;
        RunOnSta(() =>
        {
            try
            {
                var page = new SharesPageView();
                var host = FindLogical<Grid>(page, hostName);
                var drawer = FindLogical<FrameworkElement>(page, drawerName);
                r = new DrawerProbe(
                    HostFound: host is not null,
                    HostCollapsedByDefault: host is { Visibility: Visibility.Collapsed },
                    DrawerFound: drawer is not null,
                    DrawerType: drawer?.GetType(),
                    RightDocked: drawer is { HorizontalAlignment: HorizontalAlignment.Right });
            }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
        return r;
    }

    static void AssertDrawerWired(DrawerProbe r, string expectedTypeName)
    {
        Assert.True(r.HostFound, "抽屉宿主未声明（SharesPageView.xaml）");
        Assert.True(r.HostCollapsedByDefault, "抽屉宿主默认应折叠（不占页面空间）");
        Assert.True(r.DrawerFound, "抽屉控件未挂进宿主 —— 原 TODO no-op 未修补");
        Assert.Equal(expectedTypeName, r.DrawerType?.Name);
        Assert.True(r.RightDocked, "抽屉应右对齐（右侧滑出）");
    }

    static string XamlPath(string relative)
    {
        // 向上找仓库根（bin 目录层级因平台名 x64 浮动，放宽到 12 层）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "net/WinShare.App", relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"XAML not found: {relative}");
    }

    /// <summary>逻辑树查找命名元素（Collapsed 元素不在视觉树，但逻辑树可达）。</summary>
    static T? FindLogical<T>(DependencyObject root, string name) where T : FrameworkElement
        => AllLogical<T>(root).FirstOrDefault(e => e.Name == name);

    static IEnumerable<T> AllLogical<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T hit) yield return hit;
            var children = LogicalTreeHelper.GetChildren(node);
            foreach (var child in children)
                if (child is DependencyObject d) stack.Push(d);
        }
    }

    static SharesViewModel MakeVm()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "dt-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var feed = new FakeSharesFeed { Shares = new List<Share> { TestShares.Smb("Docs") } };
        return new SharesViewModel(
            feed,
            new WinShare.App.State.AppSettingsStore(dir),
            new WinShare.App.Events.AppEventBus(),
            shareService: null, presetService: null, protocolService: null,
            systemService: null, userService: null, diskService: null,
            fileSaver: async (_, _) => { }, importPicker: () => Task.FromResult<string?>(null));
    }

    /// <summary>STA 线程执行 WPF 构造（xunit 默认 MTA 线程无法构造 UI 组件）。</summary>
    static void RunOnSta(System.Action body)
    {
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try { body(); }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "STA 线程超时");
        thread.Join();
    }
}
