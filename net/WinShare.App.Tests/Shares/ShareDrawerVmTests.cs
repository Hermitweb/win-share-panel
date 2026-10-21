using System.Windows;
using WinShare.App.Shares;
using WinShare.App.Views.Shares;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Shares;

/// <summary>
/// 视图设计期实例化测试（验收标准：SharesPageView 可被设计期实例化——
/// 构造不触碰 AppServices/主题字典；DynamicResource 缺席安全降级；子窗口/抽屉同法验证）。
/// </summary>
public class SharesViewDesignTimeTests
{
    [Fact]
    public void SharesPageView_InstantiatesWithoutApplication()
    {
        // 无 Application/无 AppServices 装配：STA 线程上构造 + XAML 装载本身不得崩
        Object? view = null;
        Exception? failure = null;
        RunOnSta(() => { try { view = new SharesPageView(); } catch (Exception e) { failure = e; } });
        Assert.Null(failure);
        Assert.IsAssignableFrom<System.Windows.Controls.UserControl>(view);
    }

    [Fact]
    public void ShareCreateWizardWindow_Instantiates()
    {
        Object? w = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try
            {
                var win = new ShareCreateWizardWindow(MakeVm());
                Assert.Equal("新建共享", win.Title);
                win.Close();
                w = win;
            }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
    }

    [Fact]
    public void ShareEditWindow_Instantiates()
    {
        Object? w = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try
            {
                var form = new ShareEditFormVm { Name = "Docs", Path = @"E:\Docs", Protocol = Protocol.Smb };
                var win = new ShareEditWindow(MakeVm(), form);
                Assert.Equal("编辑共享：Docs", win.Title);
                win.Close();
                w = win;
            }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
    }

    [Fact]
    public void ShareDetailDrawer_InstantiatesAndSetShare()
    {
        Object? drawer = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try
            {
                var d = new ShareDetailDrawer();
                var vm = MakeVm();
                var share = TestShares.Smb("Docs");
                d.SetShare(share, vm.MakeDetailVm(share));
                d.SetShare(TestShares.Smb("Docs2"), vm.MakeDetailVm(TestShares.Smb("Docs2")));
                drawer = d;
            }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
    }

    [Fact]
    public void SharePermissionDrawer_InstantiatesAndSetShare()
    {
        Object? drawer = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try
            {
                var d = new SharePermissionDrawer();
                var vm = MakeVm();
                var share = TestShares.Smb("Docs");
                d.SetShare(share, vm.MakePermissionVm(share));
                drawer = d;
            }
            catch (Exception e) { failure = e; }
        });
        Assert.Null(failure);
    }

    SharesViewModel MakeVm()
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

    /// <summary>STA 线程执行 WPF 构造（xunit 默认 MTA 线程无法构造 UI 组件，同 Sessions 测试模式）。</summary>
    static void RunOnSta(System.Action body)
    {
        var done = new System.Threading.ManualResetEventSlim(false);
        var thread = new System.Threading.Thread(() =>
        {
            try { body(); }
            finally { done.Set(); }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "STA 线程超时");
        thread.Join();
    }
}

/// <summary>
/// 详情/权限子 VM 行为测试（对齐 ShareDetailDrawer.tsx / PermissionDrawer.tsx 取数与文案语义；
/// 服务缺席走占位降级路径）。
/// </summary>
public class ShareDrawerVmTests
{
    [Fact]
    public async Task Detail_SaveSmbPropsRequiresService()
    {
        var vm = new ShareDetailViewModel(TestShares.Smb("Docs"), shareService: null, null, null);
        Assert.Equal("保存属性需要共享契约服务（未接入）", await vm.SaveSmbPropsAsync());
    }

    [Fact]
    public async Task Detail_CloseAllFiles_AggregatesClosedAndFailed()
    {
        var shareSvc = new FakeShareService
        {
            CloseResult = new CloseOpenFilesResult { closed = 1, failed = 0 },
        };
        var vm = new ShareDetailViewModel(TestShares.Smb("Docs"), shareSvc, null, null);
        Assert.Equal("已关闭 1 个打开文件", await vm.CloseAllFilesAsync());
    }

    [Fact]
    public async Task Detail_CloseAllFiles_WithFailedUsesWarningWording()
    {
        var shareSvc = new FakeShareService { CloseResult = new CloseOpenFilesResult { closed = 3, failed = 1 } };
        var vm = new ShareDetailViewModel(TestShares.Smb("Docs"), shareSvc, null, null);
        Assert.Equal("已关闭 3 个，失败 1 个", await vm.CloseAllFilesAsync());
    }

    [Fact]
    public async Task Detail_ConnectionsErrorSurfaced()
    {
        var shareSvc = new FakeShareService();
        shareSvc.FailOn.Add("Docs");
        var vm = new ShareDetailViewModel(TestShares.Smb("Docs"), shareSvc, null, null);
        await vm.RefreshConnectionsAsync();
        Assert.Equal("模拟失败", vm.ConnectionsError);
    }

    [Fact]
    public void Detail_NetPathRules()
    {
        var vm = new ShareDetailViewModel(TestShares.Ftp("site", port: 2121), null, null, null);
        Assert.Equal("ftp://:2121", vm.NetPath); // 主机名缺席仍给出协议化形态
        var smb = new ShareDetailViewModel(TestShares.Smb("Docs"), null, null, null);
        Assert.Equal("", smb.NetPath);            // SMB 无主机名 → 空（TS：host 缺席不拼 UNC）
    }

    [Fact]
    public void Detail_SiteConfigInput_AssemblyByProtocol()
    {
        var nfs = new ShareDetailViewModel(new Share
        {
            name = "nfs1", path = @"E:\n", description = "", protocol = Protocol.Nfs,
            type = ShareType.Disk, hidden = false, encrypted = false,
            concurrentUsers = 0, status = ShareStatus.Enabled, cached = false,
            nfsPermission = NfsPermission.Ro, allowRootAccess = true,
        }, null, null, null);
        var input = nfs.BuildSiteConfigInput();
        Assert.Equal(NfsPermission.Ro, input.nfsPermission);
        Assert.True(input.allowRootAccess);
        Assert.Null(input.sslPolicy);
    }

    [Fact]
    public async Task Perm_AddRowThreeGuards()
    {
        var vm = new SharePermissionsViewModel(TestShares.Smb("Docs"), null, null, new FakeSharesFeed());
        Assert.Equal("请输入账号名", vm.AddRow("  ", AccountType.User, "Read"));
        Assert.Equal("", vm.AddRow("alice", AccountType.User, "Read"));
        Assert.Equal("该账号已存在", vm.AddRow("alice", AccountType.Group, "Full"));
        Assert.Single(vm.Rows);
        vm.RemoveRow("alice");
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Perm_SaveRequiresUserService()
    {
        var vm = new SharePermissionsViewModel(TestShares.Smb("Docs"), null, null, new FakeSharesFeed());
        vm.AddRow("alice", AccountType.User, "Full");
        Assert.Equal("权限保存需要用户契约服务（未接入）", await vm.SaveAsync());
    }

    [Fact]
    public async Task Perm_SaveWritesToUserService()
    {
        var user = new FakeUserService();
        var vm = new SharePermissionsViewModel(TestShares.Smb("Docs"), null, user, new FakeSharesFeed());
        vm.AddRow("alice", AccountType.Group, "Deny");
        Assert.Equal("权限已保存", await vm.SaveAsync());
        var saved = user.Saved.Single();
        var perm = saved.perms.Single();
        Assert.Equal(ShareAccess.NoAccess, perm.access);
        Assert.True(perm.deny); // Deny → NoAccess + deny=true（TS toPerm）
    }

    [Fact]
    public async Task Perm_CandidatesTipFirstTwelve()
    {
        var feed = new FakeSharesFeed
        {
            Users = Enumerable.Range(0, 15).Select(i => new LocalUser
            {
                name = "u" + i, fullName = "", enabled = true, description = "",
                groups = Array.Empty<string>(), principalSource = "Local",
            }).ToList(),
            Groups = new List<LocalGroup>
            {
                new() { name = "Users", description = "", members = Array.Empty<GroupMember>() },
            },
        };
        var vm = new SharePermissionsViewModel(TestShares.Smb("Docs"), null, null, feed);
        await vm.LoadAsync();
        Assert.Equal(16, vm.Candidates.Count);
        Assert.Contains("u11", vm.CandidatesTip);
        Assert.Contains("等", vm.CandidatesTip);
    }
}
