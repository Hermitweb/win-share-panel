using WinShare.App.Users;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Users;

/// <summary>
/// 用户页 ViewModel（对齐 src/pages/Users.tsx + src/components/*Modal/Drawer）：
/// 过滤/批量文案/矩阵加载/NTFS/用户共享权限/服务缺席降级。
/// </summary>
public class UsersViewModelTests
{
    StubUsersDataFeed MakeFeed() => new();

    static List<(string, string)> CaptureNotify(UsersViewModel vm)
    {
        var log = new List<(string, string)>();
        vm.Notify = (kind, text) => log.Add((kind, text));
        return log;
    }

    static string LastNotify(List<(string, string)> log, string kind) =>
        log.LastOrDefault(l => l.Item1 == kind).Item2;

    // ===== 过滤（对齐 filteredUsers） =====

    [Source("[Source: src/pages/Users.tsx filteredUsers + userColumns]")]
    [Fact]
    public async Task 过滤_关键词按字段范围()
    {
        var feed = MakeFeed();
        feed.Bundle = new UsersBundle
        {
            Users = new[]
            {
                TestUsers.User("alice", "Alice Smith", lastLogon: "2024-01-01T08:00:00Z"),
                TestUsers.User("bob", "Bob Builder"),
            },
            GroupsOk = true,
            UsersOk = true,
        };
        var vm = new UsersViewModel(feed, new StubUserService());
        await vm.RefreshAsync();

        vm.Keyword = "ali";
        Assert.Single(vm.UserRows);
        Assert.Equal("alice", vm.UserRows[0].user.name);
        // StatusText/LastLogon 预格式化（对齐 TS render：启用/-/YYYY-MM-DD HH:mm）
        Assert.Equal("启用", vm.UserRows[0].StatusText);

        vm.SearchField = UserSearchField.Name;
        vm.Keyword = "Smith";
        Assert.Empty(vm.UserRows); // 全名不在「仅用户名」范围

        vm.SearchField = UserSearchField.All;
        Assert.Single(vm.UserRows); // 全部字段可命中全名

        vm.Keyword = "";
        vm.OnlyEnabled = true;
        feed.Bundle = new UsersBundle
        {
            Users = new[] { TestUsers.User("alice"), TestUsers.User("guest", enabled: false) },
            UsersOk = true,
            GroupsOk = true,
        };
        await vm.RefreshAsync();
        Assert.Single(vm.UserRows);
        Assert.Equal("alice", vm.UserRows[0].user.name);
    }

    [Source("[Source: src/pages/Users.tsx userColumns/groups 列 render + groupColumns]")]
    [Fact]
    public void 列预览_前3加号与前5等N()
    {
        Assert.Equal("-", UsersViewModel.FormatGroups(null));
        Assert.Equal("A, B, C +2", UsersViewModel.FormatGroups(new[] { "A", "B", "C", "D", "E" }));
        Assert.Equal("A, B", UsersViewModel.FormatGroups(new[] { "A", "B" }));

        var members = Enumerable.Range(1, 7)
            .Select(i => new GroupMember { name = $"m{i}", objectClass = MemberObjectClass.User })
            .ToList();
        Assert.Equal("m1, m2, m3, m4, m5 等 7 个", UsersViewModel.FormatMembers(members));
        Assert.Equal("-", UsersViewModel.FormatMembers(null));
        Assert.Equal("-", UsersViewModel.FmtTime(null, "yyyy-MM-dd HH:mm"));
        Assert.Equal("2024-01-01 08:00", UsersViewModel.FmtTime("2024-01-01 08:00:00", "yyyy-MM-dd HH:mm"));
        Assert.Equal("bad-ts", UsersViewModel.FmtTime("bad-ts", "yyyy-MM-dd HH:mm"));
    }

    // ===== 批量操作文案（对齐 handleBatch*） =====

    [Source("[Source: src/pages/Users.tsx handleBatchEnable/Disable/Delete]")]
    [Fact]
    public async Task 批量启用_失败收集与文案()
    {
        var svc = new StubUserService();
        var feed = MakeFeed();
        feed.Bundle = new UsersBundle { UsersOk = true, GroupsOk = true };
        var vm = new UsersViewModel(feed, svc);
        var history = CaptureNotify(vm);
        vm.SetSelectedUsers(new[] { "u1", "u2", "u3" });
        svc.FailNames.Add("EnableUser:u2");

        var result = await vm.BatchEnableAsync();

        Assert.Equal(2, result.Ok);
        Assert.Equal(1, result.Failed);
        Assert.Equal("已启用 2 个用户，1 个失败", LastNotify(history, "success"));
        Assert.Empty(vm.SelectedUsers); // 完成后清选（TS setSelectedUsers([])）
        Assert.Equal(new[] { "EnableUser:u1", "EnableUser:u2", "EnableUser:u3" },
            svc.Calls.Where(c => c.StartsWith("EnableUser:")).ToArray());
    }

    [Source("[Source: src/pages/Users.tsx handleBatchDelete]")]
    [Fact]
    public async Task 批量删除_全成文案无失败后缀()
    {
        var svc = new StubUserService();
        var vm = new UsersViewModel(MakeFeed(), svc);
        vm.Notify = null;
        vm.SetSelectedUsers(new[] { "u1", "u2" });
        var r = await vm.BatchDeleteAsync();
        Assert.Equal(2, r.Ok);
        var vm2 = new UsersViewModel(MakeFeed(), svc) { };
        var log2 = new List<(string, string)>();
        vm2.Notify = (k, t) => log2.Add((k, t));
        vm2.SetSelectedUsers(new[] { "x" });
        await vm2.BatchDeleteAsync();
        Assert.Equal("已删除 1 个用户", log2.Single(l => l.Item1 == "success").Item2);
    }

    [Source("[Source: src/pages/Users.tsx handleBatchAssignConfirm]")]
    [Fact]
    public async Task 批量分配到组_文案与组名书名号()
    {
        var svc = new StubUserService();
        var vm = new UsersViewModel(MakeFeed(), svc);
        var log = new List<(string, string)>();
        vm.Notify = (k, t) => log.Add((k, t));
        vm.SetSelectedUsers(new[] { "u1", "u2" });
        svc.FailNames.Add("AddMember:devs:u2");
        var r = await vm.BatchAssignGroupAsync("devs");
        Assert.Equal("已分配 1 个用户到组「devs」，1 个失败", log.Single(l => l.Item1 == "success").Item2);
        Assert.Equal(1, r.Failed);
        Assert.Contains("AddMember:devs:u1", svc.Calls);
    }

    // ===== 新建用户（对齐 UserCreateModal handleCreate） =====

    [Source("[Source: src/components/UserCreateModal.tsx 创建后分配到所选组]")]
    [Fact]
    public async Task 新建用户_组分配失败走warning_全成走success()
    {
        var svc = new StubUserService();
        var vm = new UsersViewModel(MakeFeed(), svc);
        var log = new List<(string, string)>();
        vm.Notify = (k, t) => log.Add((k, t));

        var (ok, msg) = await vm.CreateUserAsync(new CreateUserOpts
        {
            name = "newguy",
            password = "p",
        }, new[] { "g1", "g2" });
        Assert.True(ok);
        Assert.Equal("用户已创建并加入 2 个组", msg);
        Assert.Equal("success", log.Single(l => l.Item1 == "success").Item1);

        var svc2 = new StubUserService();
        svc2.FailNames.Add("AddMember:g1:newguy");
        var vm2 = new UsersViewModel(MakeFeed(), svc2);
        var log2 = new List<(string, string)>();
        vm2.Notify = (k, t) => log2.Add((k, t));
        var (ok2, msg2) = await vm2.CreateUserAsync(new CreateUserOpts { name = "newguy", password = "p" },
            new[] { "g1", "g2" });
        Assert.True(ok2); // 创建成功，分配部分失败不整体失败
        Assert.Contains("用户已创建，但 1 个组分配失败：g1", msg2);
        Assert.Equal("warning", log2.Single(l => l.Item1 == "warning").Item1);

        var vm3 = new UsersViewModel(MakeFeed(), new StubUserService());
        var (ok3, msg3) = await vm3.CreateUserAsync(new CreateUserOpts { name = "n", password = "p" }, Array.Empty<string>());
        Assert.Equal("用户已创建", msg3);
    }

    // ===== 服务缺席降级（空馈送不崩） =====

    [Source("[Source: T3a 空馈送模式 + user.ts 各写操作]")]
    [Fact]
    public async Task 服务缺席_写路径如实报错_读路径空态()
    {
        var feed = MakeFeed();
        var vm = new UsersViewModel(feed, user: null);
        var log = new List<(string, string)>();
        vm.Notify = (k, t) => log.Add((k, t));

        await vm.RefreshAsync();
        Assert.Empty(vm.Users);
        Assert.Equal("用户数据获取失败", vm.ErrorText); // 空 stub 无错误信息 → 兜底文案
        Assert.Equal("error", log.Single(l => l.Item1 == "error").Item1);

        var r = await vm.DeleteUserAsync("x");
        Assert.False(r);
        var r2 = await vm.BatchEnableAsync();
        Assert.Equal(0, r2.Ok);
        Assert.Contains("用户服务未注册", log.Last().Item2);
    }

    [Source("[Source: T3a 空馈送模式：契约服务缺席 → 逐源错误信息]")]
    [Fact]
    public async Task 契约馈送_服务缺席带错误信息()
    {
        var feed = new ContractUsersDataFeed(() => null, () => null);
        var b = await feed.GetAsync();
        Assert.False(b.UsersOk);
        Assert.False(b.GroupsOk);
        Assert.False(b.SharesOk);
        Assert.Equal("IUserService 未注册", b.UsersError);
        Assert.Equal("IUserService 未注册", b.GroupsError);
        Assert.False(b.SharesOk); // 契约馈送未携带 shares 错误信息字段（users/groups 已携带）

        // 注入服务后自动点亮
        var svc = new StubUserService { Users = new[] { TestUsers.User("u") } };
        var feed2 = new ContractUsersDataFeed(() => svc, () => null);
        var b2 = await feed2.GetAsync();
        Assert.True(b2.UsersOk);
        Assert.Single(b2.Users);
    }

    // ===== 组管理（对齐 GroupManageModal） =====

    [Source("[Source: src/components/GroupManageModal.tsx 各 handler]")]
    [Fact]
    public async Task 组管理_描述重命名与成员文案()
    {
        var svc = new StubUserService();
        var vm = new UsersViewModel(MakeFeed(), svc);
        var log = new List<(string, string)>();
        vm.Notify = (k, t) => log.Add((k, t));

        var (ok, msg) = await vm.UpdateGroupDescriptionAsync("devs", "新描述");
        Assert.True(ok);
        Assert.Equal("描述已更新", msg);

        var (ok2, _) = await vm.RenameGroupAsync("devs", "  ");
        Assert.True(ok2); // trim 后空 → 不触发服务（TS handleRename 早退）
        Assert.DoesNotContain("RenameGroup:devs", svc.Calls);

        var (ok3, msg3) = await vm.RenameGroupAsync("devs", "dev2");
        Assert.Equal("组已重命名", msg3);

        var (ok4, msg4) = await vm.AddGroupMemberAsync("devs", "  alice ");
        Assert.Equal("已添加成员：alice", msg4);

        var batch = await vm.BatchAddGroupMembersAsync("devs", new[] { "a", "b" });
        Assert.Equal(2, batch.Ok);
        var lastOk = log.Last(l => l.Item1 == "success").Item2;
        Assert.Equal("已添加 2 个成员", lastOk);
        Assert.Equal("已移除成员：alice",
            (await vm.RemoveGroupMemberAsync("devs", "alice")).Item2);
    }

    // ===== 用户属性保存（对齐 UserDetailDrawer handleSave） =====

    [Source("[Source: src/components/UserDetailDrawer.tsx handleSave]")]
    [Fact]
    public async Task 保存用户_改名优先于更新_密码用最终用户名()
    {
        var svc = new StubUserService();
        var vm = new UsersViewModel(MakeFeed(), svc);
        var (ok, msg) = await vm.SaveUserAsync("old", new UsersViewModel.UserEditArgs(
            NewName: "old", FullName: "F", Description: "D",
            Enabled: true, UserMayChangePassword: false, PasswordNeverExpires: true,
            NewPassword: "npw"));
        Assert.True(ok);
        Assert.Equal("已保存", msg);
        Assert.Contains("UpdateUser:old", svc.Calls);
        Assert.Contains("SetPassword:old", svc.Calls);

        var svc2 = new StubUserService();
        var vm2 = new UsersViewModel(MakeFeed(), svc2);
        await vm2.SaveUserAsync("old", new UsersViewModel.UserEditArgs(
            NewName: "new", FullName: "", Description: "",
            Enabled: false, UserMayChangePassword: true, PasswordNeverExpires: false,
            NewPassword: "x"));
        var callOrder = svc2.Calls.Where(c => c.StartsWith("RenameUser") || c.StartsWith("UpdateUser") || c.StartsWith("SetPassword")).ToList();
        Assert.Equal(new[] { "RenameUser:old", "UpdateUser:old", "SetPassword:new" }, callOrder);
    }

    // ===== 权限矩阵（对齐 PermissionMatrix.tsx effect） =====

    [Source("[Source: src/components/PermissionMatrix.tsx useEffect load]")]
    [Fact]
    public async Task 矩阵_普通共享过滤IPC与Special_按账号分组PickAccess()
    {
        // 矩阵取数走 IUserService.GetSharePermissionsAsync（stub 按配置返回；非 IShareService）
        var svc = new StubUserService
        {
            Users = new[] { TestUsers.User("alice"), TestUsers.User("bob") },
            Groups = new[] { TestUsers.Group("devs") },
            Perms = new[]
            {
                TestUsers.Perm("pub", "alice", ShareAccess.Read),
                TestUsers.Perm("pub", "alice", ShareAccess.Full),   // 同账号两条 → 取 Full
                TestUsers.Perm("pub", "bob", ShareAccess.NoAccess, deny: true), // → Deny
            },
        };
        var shareSvc = new StubShareService
        {
            Shares = new[]
            {
                TestUsers.Share("pub"),
                TestUsers.Share("IPC$", type: ShareType.IPC),
                TestUsers.Share("ADMIN$", type: ShareType.Special),
            },
            Perms = new[]
            {
                TestUsers.Perm("pub", "alice", ShareAccess.Read),
                TestUsers.Perm("pub", "alice", ShareAccess.Full),   // 同账号两条 → 取 Full
                TestUsers.Perm("pub", "bob", ShareAccess.NoAccess, deny: true), // → Deny
            },
        };
        var feed = new ContractUsersDataFeed(() => svc, () => shareSvc);
        var vm = new UsersViewModel(feed, svc);
        var log = new List<(string, string)>();
        vm.Notify = (k, t) => log.Add((k, t));

        await vm.LoadMatrixAsync();

        Assert.Equal(1, vm.MatrixShares.Count);
        Assert.Equal("pub", vm.MatrixShares[0].name);
        Assert.Equal(3, vm.MatrixAccounts.Count); // alice, bob, devs
        Assert.Equal(MatrixAccess.Full, vm.MatrixData["pub"]["alice"]);
        Assert.Equal(MatrixAccess.Deny, vm.MatrixData["pub"]["bob"]);
        Assert.Equal(MatrixAccess.None, vm.MatrixData["pub"]["devs"]);
        Assert.Contains("1 共享 × 3 账号", vm.MatrixNote);
    }

    [Source("[Source: src/components/PermissionMatrix.tsx cancel]")]
    [Fact]
    public void 矩阵_取消提示()
    {
        var vm = new UsersViewModel(MakeFeed(), new StubUserService());
        var log = new List<(string, string)>();
        vm.Notify = (k, t) => log.Add((k, t));
        vm.CancelMatrix();
        Assert.Equal("已取消加载", vm.MatrixNote);
        Assert.Equal("info", log.Single(l => l.Item2 == "已取消加载").Item1);
    }

    // ===== NTFS / 用户共享权限（对齐 getNtfsPermissions + drawer perms tab） =====

    [Source("[Source: electron/services/user.ts getNtfsPermissions]")]
    [Fact]
    public async Task NTFS_查看映射允许拒绝()
    {
        var svc = new StubUserService
        {
            Ntfs = new NtfsAcl
            {
                path = @"C:\data",
                entries = new[]
                {
                    new NtfsAclEntry { account = "ADMIN", rights = "FullControl", type = NtfsAclType.Allow, inherited = true },
                    new NtfsAclEntry { account = "GUEST", rights = "ReadAndExecute", type = NtfsAclType.Deny, inherited = false },
                },
            },
        };
        var vm = new UsersViewModel(MakeFeed(), svc);
        await vm.ViewNtfsAsync(@"C:\data");
        Assert.Equal(2, vm.NtfsRows.Count);
        Assert.Equal("允许", vm.NtfsRows[0].TypeText);
        Assert.Equal("拒绝", vm.NtfsRows[1].TypeText);
        Assert.Equal(@"C:\data", vm.NtfsPath);
    }

    [Source("[Source: src/components/UserDetailDrawer.tsx 共享权限 Tab]")]
    [Fact]
    public async Task 用户共享权限_访问级别标签()
    {
        var svc = new StubUserService
        {
            Perms = new[]
            {
                TestUsers.Perm("pub", "alice", ShareAccess.Full),
                TestUsers.Perm("pub", "alice", ShareAccess.Read, deny: true),
            },
        };
        var vm = new UsersViewModel(MakeFeed(), svc);
        var rows = await vm.LoadUserSharePermsAsync("alice");
        Assert.Equal(2, rows.Count);
        Assert.Equal("完全控制", rows[0].AccessText);
        Assert.Equal("拒绝", rows[1].AccessText);
        Assert.Equal("用户", rows[0].TypeText);
    }

    // ===== 校验（对齐 Form rules） =====

    [Source("[Source: src/components/UserCreateModal.tsx / GroupCreateModal.tsx Form rules]")]
    [Theory]
    [InlineData("john.doe", null)]
    [InlineData("a-b_c", null)]
    [InlineData("john", null)]
    [InlineData("", "请输入用户名")]
    [InlineData("123456789012345678901", "用户名不能超过 20 字符")]
    [InlineData("bad name!", "仅支持字母、数字、点、下划线、连字符")]
    public void 用户名校验(string name, string? expected)
    {
        Assert.Equal(expected, UsersValidators.ValidateUserName(name));
    }

    [Source("[Source: src/components/GroupCreateModal.tsx Form rules]")]
    [Theory]
    [InlineData("ProjectEditors", null)]
    [InlineData("项目组", null)]
    [InlineData("", "请输入组名")]
    [InlineData("bad/slash", "名称包含非法字符")]
    public void 组名校验(string name, string? expected)
    {
        Assert.Equal(expected, UsersValidators.ValidateGroupName(name));
    }
}
