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
        // 无 Application/无 AppServices 装配：构造 + XAML 装载本身不得崩
        var view = new SharesPageView();
        Assert.NotNull(view);
    }

    [Fact]
    public void ShareCreateWizardWindow_Instantiates()
    {
        var vm = MakeVm();
        var w = new ShareCreateWizardWindow(vm);
        Assert.NotNull(w);
        Assert.Equal("新建共享", w.Title);
        w.Close();
    }

    [Fact]
    public void ShareEditWindow_Instantiates()
    {
        var vm = MakeVm();
        var form = new ShareEditFormVm
        {
            Name = "Docs", Path = @"E:\Docs", Protocol = Protocol.Smb,
        };
        var w = new ShareEditWindow(vm, form);
        Assert.Equal("编辑共享：Docs", w.Title);
        w.Close();
    }

    [Fact]
    public void ShareDetailDrawer_InstantiatesAndSetShare()
    {
        var drawer = new ShareDetailDrawer();
        var vm = MakeVm();
        var share = TestShares.Smb("Docs");
        drawer.SetShare(share, vm.MakeDetailVm(share));
        Assert.Equal("共享详情：Docs", drawer.DrawerTitle.Text);
        Assert.Equal(Visibility.Collapsed, drawer.RbSite.Visibility); // SMB 无站点配置 Tab
    }

    [Fact]
    public void SharePermissionDrawer_InstantiatesAndSetShare()
    {
        var drawer = new SharePermissionDrawer();
        var vm = MakeVm();
        var share = TestShares.Smb("Docs");
        drawer.SetShare(share, vm.MakePermissionVm(share));
        Assert.Equal("权限管理：Docs", drawer.DrawerTitle.Text);
    }

    SharesViewModel MakeVm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winshare-dt-" + Guid.NewGuid("n"));
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
            OpenFiles = new List<ShareOpenFile>
            {
                new() { fileId = 1, path = @"E:\a.txt", clientUserName = "u", clientComputerName = "c", lockCount = 0 },
            },
            CloseResult = new CloseOpenFilesResult { closed = 1, failed = 0 },
        };
        var vm = new ShareDetailViewModel(TestShares.Smb("Docs"), shareSvc, null, null);
        Assert.Equal("已关闭 1 个打开文件", await vm.CloseAllFilesAsync());
    }

    [Fact]
    public async Task Detail_CloseAllFiles_WithFailedUsesWarningWording()
    {
        var shareSvc = new FakeShareService
        {
            OpenFiles = new List<ShareOpenFile>(),
            CloseResult = new CloseOpenFilesResult { closed = 3, failed = 1 },
        };
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
