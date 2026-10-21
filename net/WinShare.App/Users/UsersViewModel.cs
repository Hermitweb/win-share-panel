using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;

namespace WinShare.App.Users;

/// <summary>用户搜索字段（对齐 Users.tsx searchField: 'name' | 'all'）。</summary>
public enum UserSearchField { Name, All }

/// <summary>用户表格行（列渲染文本预格式化；对齐 TS userColumns 各列 render）。</summary>
public sealed record UserRow(LocalUser user, string StatusText, string GroupsText, string LastLogonText);

/// <summary>组表格行（对齐 TS groupColumns：成员数 + 成员预览）。</summary>
public sealed record GroupRow(LocalGroup group, int MemberCount, string MembersPreview);

/// <summary>NTFS ACL 行（对齐 NtfsAclEntry 展示）。</summary>
public sealed record NtfsRow(NtfsAclEntry entry, string TypeText);

/// <summary>用户跨共享权限行（对齐 UserDetailDrawer 共享权限 Tab）。</summary>
public sealed record UserPermRow(SharePermission perm, string AccessText, string TypeText);

/// <summary>
/// 用户/组名校验（对齐 TS UserCreateModal/GroupCreateModal Form rules）：
/// 用户名 ^[A-Za-z0-9._-]+$ 且 ≤20 字符；组名 ^[A-Za-z0-9._\- \u4e00-\u9fa5]+$ 且 ≤256 字符。
/// 返回错误文案，合法返回 null。
/// </summary>
public static class UsersValidators
{
    public static string? ValidateUserName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "请输入用户名";
        if (name.Length > 20) return "用户名不能超过 20 字符";
        if (!Regex.IsMatch(name, "^[A-Za-z0-9._-]+$")) return "仅支持字母、数字、点、下划线、连字符";
        return null;
    }

    public static string? ValidateGroupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "请输入组名";
        if (name.Length > 256) return "组名不能超过 256 字符";
        if (!Regex.IsMatch(name, "^[A-Za-z0-9._\\- 一-龥]+$")) return "名称包含非法字符";
        return null;
    }
}

/// <summary>
/// 用户与权限页 ViewModel（对齐 src/pages/Users.tsx + src/components/PermissionMatrix.tsx +
/// UserCreateModal/UserDetailDrawer/GroupCreateModal/GroupManageModal 的界面逻辑）：
/// 用户/组 CRUD、共享权限 Full/Change/Read/Deny、NTFS 权限查看、权限矩阵（Deny 优先 +
/// CSV/JSON/HTML 报告导出）、批量启停/删除/分组。
/// 不引用 WPF 控件类型（只 INotifyPropertyChanged + ICommand），xUnit 直测。
/// 写路径走 IUserService 契约（缺席=如实报错不崩）；读路径走 IUsersDataFeed（逐源空馈送降级）。
/// </summary>
public sealed class UsersViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    readonly IUsersDataFeed _feed;
    readonly IUserService? _user;
    readonly Func<string, byte[], Task>? _fileSaver;
    readonly Func<long> _nowMs;

    public UsersViewModel(
        IUsersDataFeed feed,
        IUserService? user = null,
        Func<string, byte[], Task>? fileSaver = null,
        Func<long>? nowMs = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _user = user;
        _fileSaver = fileSaver;
        _nowMs = nowMs ?? (() => DateTimeOffset.Now.ToUnixTimeMilliseconds());

        RefreshCommand = new RelayCommand(() => RefreshAsync());
        LoadMatrixCommand = new RelayCommand(() => LoadMatrixAsync());
        CancelMatrixCommand = new RelayCommand(CancelMatrix);
        ExportMatrixCsvCommand = new RelayCommand(() => ExportMatrixCsvAsync());
        ExportMatrixJsonCommand = new RelayCommand(() => ExportMatrixJsonAsync());
        ExportMatrixHtmlCommand = new RelayCommand(() => ExportMatrixHtmlAsync());
    }

    // ===== 命令 =====
    public RelayCommand RefreshCommand { get; }
    public RelayCommand LoadMatrixCommand { get; }
    public RelayCommand CancelMatrixCommand { get; }
    public RelayCommand ExportMatrixCsvCommand { get; }
    public RelayCommand ExportMatrixJsonCommand { get; }
    public RelayCommand ExportMatrixHtmlCommand { get; }

    /// <summary>轻量通知面（kind: success|error|info|warning + 文案）；视图接 toast/状态条。对齐 TS message.*。</summary>
    public Action<string, string>? Notify { get; set; }

    void Tell(string kind, string text) => Notify?.Invoke(kind, text);

    // ===== 数据（读路径） =====
    IReadOnlyList<LocalUser> _users = Array.Empty<LocalUser>();
    IReadOnlyList<LocalGroup> _groups = Array.Empty<LocalGroup>();
    IReadOnlyList<Share> _shares = Array.Empty<Share>();
    bool _hasData;
    string _errorText = "";
    bool _loading;

    /// <summary>本地用户（过滤前全量）。</summary>
    public IReadOnlyList<LocalUser> Users => _users;
    /// <summary>本地组。</summary>
    public IReadOnlyList<LocalGroup> Groups => _groups;
    /// <summary>共享（矩阵共享列；仅普通共享参与矩阵计算）。</summary>
    public IReadOnlyList<Share> Shares => _shares.Where(s => s.type is not (ShareType.Special or ShareType.IPC)).ToList();
    /// <summary>首轮加载失败提示（TS: error && !data 才提示；有数据后静默）。</summary>
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }

    // ===== 过滤（对齐 Users.tsx filteredUsers） =====
    string _keyword = "";
    UserSearchField _searchField = UserSearchField.Name;
    bool _onlyEnabled;
    IReadOnlyList<UserRow> _userRows = Array.Empty<UserRow>();
    IReadOnlyList<GroupRow> _groupRows = Array.Empty<GroupRow>();

    /// <summary>关键词（对齐 keyword state；trim + toLowerCase 匹配）。</summary>
    public string Keyword { get => _keyword; set { if (_keyword != value) { _keyword = value; Set(ref _keyword, value); ApplyFilters(); } } }
    /// <summary>搜索字段：仅用户名 / 全部字段。</summary>
    public UserSearchField SearchField { get => _searchField; set { if (_searchField != value) { _searchField = value; Set(ref _searchField, value); ApplyFilters(); } } }
    /// <summary>仅启用开关。</summary>
    public bool OnlyEnabled { get => _onlyEnabled; set { if (_onlyEnabled != value) { _onlyEnabled = value; Set(ref _onlyEnabled, value); ApplyFilters(); } } }
    /// <summary>过滤后用户行（含列渲染文本）。</summary>
    public IReadOnlyList<UserRow> UserRows { get => _userRows; private set => Set(ref _userRows, value); }
    /// <summary>组行（成员数 + 成员预览）。</summary>
    public IReadOnlyList<GroupRow> GroupRows { get => _groupRows; private set => Set(ref _groupRows, value); }

    // ===== 批量选择（视图 DataGrid 选择 → 此处；TS selectedUsers） =====
    List<string> _selected = new();
    /// <summary>已选用户名（TS selectedUsers）。</summary>
    public IReadOnlyList<string> SelectedUsers => _selected;
    public void SetSelectedUsers(IReadOnlyList<string> names)
    {
        _selected = names.ToList();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedUsers)));
    }

    // ===== 权限矩阵（对齐 PermissionMatrix.tsx） =====
    bool _matrixLoading;
    volatile bool _matrixCancel;
    IReadOnlyList<MatrixAccount> _matrixAccounts = Array.Empty<MatrixAccount>();
    IReadOnlyList<Share> _matrixShares = Array.Empty<Share>();
    IReadOnlyDictionary<string, Dictionary<string, MatrixAccess>> _matrixData =
        new Dictionary<string, Dictionary<string, MatrixAccess>>();
    string _matrixNote = "";

    public bool MatrixLoading { get => _matrixLoading; private set => Set(ref _matrixLoading, value); }
    /// <summary>矩阵账号列（用户 + 组；对齐 TS acctList 顺序）。</summary>
    public IReadOnlyList<MatrixAccount> MatrixAccounts { get => _matrixAccounts; private set => Set(ref _matrixAccounts, value); }
    /// <summary>矩阵共享行（普通共享，过滤 IPC/Special）。</summary>
    public IReadOnlyList<Share> MatrixShares { get => _matrixShares; private set => Set(ref _matrixShares, value); }
    /// <summary>矩阵值：shareName → (accountName → 单元格)；None 表示 '-'。</summary>
    public IReadOnlyDictionary<string, Dictionary<string, MatrixAccess>> MatrixData { get => _matrixData; private set => Set(ref _matrixData, value); }
    /// <summary>矩阵提示行（"矩阵单元格按 Deny 优先显示（安全语义）。N 共享 × M 账号。" / 已取消）。</summary>
    public string MatrixNote { get => _matrixNote; private set => Set(ref _matrixNote, value); }

    // ===== NTFS（对齐 getNtfsPermissions 查看面） =====
    string _ntfsPath = "";
    IReadOnlyList<NtfsRow> _ntfsRows = Array.Empty<NtfsRow>();
    public string NtfsPath { get => _ntfsPath; private set => Set(ref _ntfsPath, value); }
    public IReadOnlyList<NtfsRow> NtfsRows { get => _ntfsRows; private set => Set(ref _ntfsRows, value); }

    // ===== 用户共享权限（对齐 UserDetailDrawer「共享权限」Tab） =====
    string _userPermsUser = "";
    IReadOnlyList<UserPermRow> _userPermRows = Array.Empty<UserPermRow>();
    public string UserPermsUser { get => _userPermsUser; private set => Set(ref _userPermsUser, value); }
    public IReadOnlyList<UserPermRow> UserPermRows { get => _userPermRows; private set => Set(ref _userPermRows, value); }

    // =====================================================================
    // 读路径
    // =====================================================================

    /// <summary>刷新（对齐 load() 失效重取；首轮失败提示，后续静默保留旧数据）。</summary>
    public async Task RefreshAsync()
    {
        Loading = true;
        var b = await _feed.GetAsync();
        _users = b.Users;
        _groups = b.Groups;
        _shares = b.Shares;
        if (b.UsersOk)
        {
            _hasData = true;
            ErrorText = "";
        }
        else if (!_hasData)
        {
            // [Source: src/pages/Users.tsx 首轮失败提示：if (error && !data) message.error(...)]
            ErrorText = b.UsersError ?? "用户数据获取失败";
            Tell("error", ErrorText);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Users)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Groups)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shares)));
        ApplyFilters();
        Loading = false;
    }

    void ApplyFilters()
    {
        // [Source: src/pages/Users.tsx filteredUsers]
        var q = _keyword.Trim().ToLowerInvariant();
        _userRows = _users
            .Where(u =>
            {
                if (_onlyEnabled && !u.enabled) return false;
                if (q.Length == 0) return true;
                if (_searchField == UserSearchField.Name) return u.name.ToLowerInvariant().Contains(q);
                return u.name.ToLowerInvariant().Contains(q)
                    || (u.fullName ?? "").ToLowerInvariant().Contains(q)
                    || (u.description ?? "").ToLowerInvariant().Contains(q)
                    || (u.groups ?? new List<string>()).Any(g => g.ToLowerInvariant().Contains(q));
            })
            .Select(u => new UserRow(u,
                StatusText: u.enabled ? "启用" : "禁用",
                GroupsText: FormatGroups(u.groups),
                LastLogonText: FmtTime(u.lastLogon, "yyyy-MM-dd HH:mm")))
            .ToList();
        _groupRows = _groups
            .Select(g => new GroupRow(g,
                MemberCount: g.members?.Count ?? 0,
                MembersPreview: FormatMembers(g.members)))
            .ToList();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserRows)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupRows)));
    }

    /// <summary>用户「所属组」列：前 3 个 + "+N"；空为 '-'（对齐 TS slice(0,3) + &lt;Tag&gt;+N&lt;/Tag&gt;）。</summary>
    public static string FormatGroups(IReadOnlyList<string>? groups)
    {
        if (groups is null || groups.Count == 0) return "-";
        var head = string.Join(", ", groups.Take(3));
        var rest = groups.Count - 3;
        return rest > 0 ? $"{head} +{rest}" : head;
    }

    /// <summary>组「成员预览」列：前 5 个 + "等 N 个"；空为 '-'（对齐 TS slice(0,5)）。</summary>
    public static string FormatMembers(IReadOnlyList<GroupMember>? members)
    {
        if (members is null || members.Count == 0) return "-";
        var head = string.Join(", ", members.Take(5).Select(m => m.name));
        return members.Count > 5 ? $"{head} 等 {members.Count} 个" : head;
    }

    /// <summary>时间格式化（对齐 dayjs：空→'-'，无效→原值）。</summary>
    public static string FmtTime(string? ts, string format)
    {
        if (string.IsNullOrEmpty(ts)) return "-";
        return DateTime.TryParse(ts, out var d) ? d.ToString(format) : ts;
    }

    // =====================================================================
    // 写路径（IUserService；缺席 → 如实报错不崩）
    // =====================================================================

    static readonly string NoUserSvc = "用户服务未注册（组合根未接线，见 UsersModule 服务槽位）";

    bool GuardUser()
    {
        if (_user is null)
        {
            Tell("error", NoUserSvc);
            return false;
        }
        return true;
    }

    /// <summary>启用/禁用切换（对齐 handleToggleUser）：成功文案 已禁用/已启用。</summary>
    public async Task<bool> ToggleUserAsync(LocalUser u)
    {
        if (!GuardUser()) return false;
        try
        {
            if (u.enabled)
            {
                await _user!.DisableUserAsync(u.name);
                Tell("success", "已禁用");
            }
            else
            {
                await _user!.EnableUserAsync(u.name);
                Tell("success", "已启用");
            }
            await RefreshAsync();
            return true;
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return false;
        }
    }

    /// <summary>删除用户（对齐 handleDeleteUser；确认框在视图层）。</summary>
    public async Task<bool> DeleteUserAsync(string name)
    {
        if (!GuardUser()) return false;
        try
        {
            await _user!.DeleteUserAsync(name);
            Tell("success", "用户已删除");
            await RefreshAsync();
            return true;
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return false;
        }
    }

    /// <summary>
    /// 批量操作（对齐 handleBatchEnable/Disable/Delete：逐个 try/catch 收集失败，
    /// 文案 "已启用/已禁用/已删除 N 个用户" + 可选 "，M 个失败"；完成后清选 + 刷新）。
    /// </summary>
    public async Task<BatchResult> BatchEnableAsync() =>
        await RunBatch("enable", "启用");

    public async Task<BatchResult> BatchDisableAsync() =>
        await RunBatch("disable", "禁用");

    public async Task<BatchResult> BatchDeleteAsync() =>
        await RunBatch("delete", "删除");

    async Task<BatchResult> RunBatch(string op, string verb)
    {
        var targets = _selected.ToList();
        if (targets.Count == 0) return new BatchResult(0, 0);
        if (!GuardUser()) return new BatchResult(0, 0);

        // [Source: src/pages/Users.tsx handleBatchEnable/Disable/Delete]
        var failed = new List<string>();
        foreach (var name in targets)
        {
            try
            {
                switch (op)
                {
                    case "enable": await _user!.EnableUserAsync(name); break;
                    case "disable": await _user!.DisableUserAsync(name); break;
                    default: await _user!.DeleteUserAsync(name); break;
                }
            }
            catch { failed.Add(name); }
        }
        var ok = targets.Count - failed.Count;
        // 文案逐字对齐 TS：`已启用 ${ok} 个用户${failed.length ? `，${failed.length} 个失败` : ''}`
        Tell("success", $"已{verb} {ok} 个用户{(failed.Count > 0 ? $"，{failed.Count} 个失败" : "")}");
        _selected.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedUsers)));
        await RefreshAsync();
        return new BatchResult(ok, failed.Count, failed);
    }

    /// <summary>批量结果（成功数/失败数/失败名单）。</summary>
    public sealed record BatchResult(int Ok, int Failed, IReadOnlyList<string>? FailedNames = null);

    /// <summary>
    /// 批量分配到组（对齐 handleBatchAssignConfirm：逐个 AddGroupMember 收集失败；
    /// 文案 "已分配 N 个用户到组「G」" + 可选 "，M 个失败"；清选 + 刷新）。
    /// </summary>
    public async Task<BatchResult> BatchAssignGroupAsync(string groupName)
    {
        if (string.IsNullOrEmpty(groupName) || _selected.Count == 0) return new BatchResult(0, 0);
        if (!GuardUser()) return new BatchResult(0, 0);
        var targets = _selected.ToList();
        var failed = new List<string>();
        foreach (var name in targets)
        {
            try { await _user!.AddGroupMemberAsync(groupName, name); }
            catch { failed.Add(name); }
        }
        var ok = targets.Count - failed.Count;
        Tell("success", $"已分配 {ok} 个用户到组「{groupName}」{(failed.Count > 0 ? $"，{failed.Count} 个失败" : "")}");
        _selected.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedUsers)));
        await RefreshAsync();
        return new BatchResult(ok, failed.Count, failed);
    }

    /// <summary>
    /// 新建用户（对齐 UserCreateModal handleCreate：创建后逐组分配，
    /// 分配失败→warning "用户已创建，但 N 个组分配失败：名单"；全成→"用户已创建并加入 N 个组"；无组→"用户已创建"）。
    /// </summary>
    public async Task<(bool ok, string message)> CreateUserAsync(CreateUserOpts opts, IReadOnlyList<string> targetGroups)
    {
        if (!GuardUser()) return (false, NoUserSvc);
        try
        {
            await _user!.CreateUserAsync(opts);
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return (false, e.Message);
        }

        // [Source: src/components/UserCreateModal.tsx 创建后分配到所选组]
        var failedGroups = new List<string>();
        if (targetGroups is { Count: > 0 })
        {
            foreach (var g in targetGroups)
            {
                try { await _user!.AddGroupMemberAsync(g, opts.name); }
                catch { failedGroups.Add(g); }
            }
        }
        string msg;
        if (failedGroups.Count > 0)
        {
            msg = $"用户已创建，但 {failedGroups.Count} 个组分配失败：{string.Join(", ", failedGroups)}";
            Tell("warning", msg);
        }
        else if (targetGroups is { Count: > 0 })
        {
            msg = $"用户已创建并加入 {targetGroups.Count} 个组";
            Tell("success", msg);
        }
        else
        {
            msg = "用户已创建";
            Tell("success", msg);
        }
        await RefreshAsync();
        return (true, msg);
    }

    /// <summary>新建组（对齐 GroupCreateModal）：成功 "组已创建"。</summary>
    public async Task<(bool ok, string message)> CreateGroupAsync(CreateGroupOpts opts)
    {
        if (!GuardUser()) return (false, NoUserSvc);
        try
        {
            await _user!.CreateGroupAsync(opts);
            Tell("success", "组已创建");
            await RefreshAsync();
            return (true, "组已创建");
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return (false, e.Message);
        }
    }

    /// <summary>删除组（对齐 handleDeleteGroup；确认框在视图层）：成功 "组已删除"。</summary>
    public async Task<(bool ok, string message)> DeleteGroupAsync(string name)
    {
        if (!GuardUser()) return (false, NoUserSvc);
        try
        {
            await _user!.DeleteGroupAsync(name);
            Tell("success", "组已删除");
            await RefreshAsync();
            return (true, "组已删除");
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return (false, e.Message);
        }
    }

    // ===== 用户属性保存（对齐 UserDetailDrawer handleSave：改名→更新→可选重设密码） =====

    /// <summary>用户编辑参数（对齐 Drawer 表单字段；fullName/description 空串=清空）。</summary>
    public sealed record UserEditArgs(string NewName, string FullName, string Description,
        bool Enabled, bool UserMayChangePassword, bool PasswordNeverExpires, string? NewPassword);

    /// <summary>保存用户（[Source: src/components/UserDetailDrawer.tsx handleSave]）：
    /// 用户名变更先 RenameUser；UpdateUser 落全名/描述/启用/可改密码/永不过期（passwordExpires = !永不过期）；
    /// 开关打开且有新密码 → SetUserPassword（用最终用户名）。成功 "已保存"。</summary>
    public async Task<(bool ok, string message)> SaveUserAsync(string originalName, UserEditArgs args)
    {
        if (!GuardUser()) return (false, NoUserSvc);
        try
        {
            if (args.NewName != originalName)
                await _user!.RenameUserAsync(originalName, args.NewName);
            await _user!.UpdateUserAsync(originalName, new UpdateUserOpts
            {
                fullName = args.FullName,
                description = args.Description,
                enabled = args.Enabled,
                passwordChangeable = args.UserMayChangePassword,
                passwordExpires = !args.PasswordNeverExpires,
            });
            if (args.NewPassword is { Length: > 0 })
                await _user!.SetUserPasswordAsync(args.NewName.Length > 0 ? args.NewName : originalName, args.NewPassword);
            Tell("success", "已保存");
            await RefreshAsync();
            return (true, "已保存");
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return (false, e.Message);
        }
    }

    // ===== 组成员管理（对齐 GroupManageModal） =====

    /// <summary>保存组描述（对齐 handleSaveDesc）：成功 "描述已更新"。</summary>
    public async Task<(bool ok, string message)> UpdateGroupDescriptionAsync(string groupName, string description)
    {
        if (!GuardUser()) return (false, NoUserSvc);
        try
        {
            await _user!.UpdateGroupAsync(groupName, description);
            Tell("success", "描述已更新");
            await RefreshAsync();
            return (true, "描述已更新");
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return (false, e.Message);
        }
    }

    /// <summary>重命名组（对齐 handleRename；成功后视图关闭抽屉）：成功 "组已重命名"。</summary>
    public async Task<(bool ok, string message)> RenameGroupAsync(string oldName, string newName)
    {
        if (!GuardUser()) return (false, NoUserSvc);
        var trimmed = newName.Trim();
        if (trimmed.Length == 0 || trimmed == oldName)
        {
            // TS: trim 后与原值相同 → 直接结束改名交互，不调用服务
            return (true, "");
        }
        try
        {
            await _user!.RenameGroupAsync(oldName, trimmed);
            Tell("success", "组已重命名");
            await RefreshAsync();
            return (true, "组已重命名");
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return (false, e.Message);
        }
    }

    /// <summary>手动添加成员（对齐 handleAddMember；member trim 后非空）：成功 "已添加成员：X"。</summary>
    public async Task<(bool ok, string message)> AddGroupMemberAsync(string groupName, string member)
    {
        if (!GuardUser()) return (false, NoUserSvc);
        var m = member.Trim();
        if (m.Length == 0) return (false, "");
        try
        {
            await _user!.AddGroupMemberAsync(groupName, m);
            Tell("success", $"已添加成员：{m}");
            await RefreshAsync();
            return (true, "已添加成员：" + m);
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return (false, e.Message);
        }
    }

    /// <summary>批量添加成员（对齐 handleBatchAddMembers）："已添加 N 个成员" + 可选 "，M 个失败"。</summary>
    public async Task<BatchResult> BatchAddGroupMembersAsync(string groupName, IReadOnlyList<string> members)
    {
        if (!GuardUser() || members is not { Count: > 0 }) return new BatchResult(0, 0);
        var failed = new List<string>();
        foreach (var m in members)
        {
            try { await _user!.AddGroupMemberAsync(groupName, m); }
            catch { failed.Add(m); }
        }
        var ok = members.Count - failed.Count;
        Tell("success", $"已添加 {ok} 个成员{(failed.Count > 0 ? $"，{failed.Count} 个失败" : "")}");
        await RefreshAsync();
        return new BatchResult(ok, failed.Count, failed);
    }

    /// <summary>移除成员（对齐 handleRemoveMember）：成功 "已移除成员：X"。</summary>
    public async Task<(bool ok, string message)> RemoveGroupMemberAsync(string groupName, string memberName)
    {
        if (!GuardUser()) return (false, NoUserSvc);
        try
        {
            await _user!.RemoveGroupMemberAsync(groupName, memberName);
            Tell("success", $"已移除成员：{memberName}");
            await RefreshAsync();
            return (true, "已移除成员：" + memberName);
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            return (false, e.Message);
        }
    }

    // ===== 用户跨共享权限（对齐 UserDetailDrawer 共享权限 Tab：api.user.sharePermissionsForUser） =====

    /// <summary>加载某用户的共享权限（[Source: src/components/UserDetailDrawer.tsx loadPerms]）。</summary>
    public async Task<IReadOnlyList<UserPermRow>> LoadUserSharePermsAsync(string username)
    {
        _userPermsUser = username;
        if (!GuardUser())
        {
            _userPermRows = Array.Empty<UserPermRow>();
            return _userPermRows;
        }
        try
        {
            var perms = await _user!.GetUserSharePermissionsAsync(username);
            // 访问级别展示：deny→"拒绝"，否则 Full/Change/Read 标签（对齐 ACCESS_LABEL）
            _userPermRows = perms.Select(p => new UserPermRow(p,
                AccessText: p.deny
                    ? "拒绝"
                    : p.access switch
                    {
                        ShareAccess.Full => "完全控制",
                        ShareAccess.Change => "更改",
                        ShareAccess.Read => "只读",
                        _ => "拒绝",
                    },
                TypeText: p.accountType == AccountType.User ? "用户" : "组")).ToList();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserPermRows)));
            return _userPermRows;
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
            _userPermRows = Array.Empty<UserPermRow>();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserPermRows)));
            return _userPermRows;
        }
    }

    // =====================================================================
    // 权限矩阵（对齐 PermissionMatrix.tsx effect：普通共享 × 账号，并发 4，Deny 优先）
    // =====================================================================

    /// <summary>
    /// 加载矩阵（[Source: src/components/PermissionMatrix.tsx useEffect load]：
    /// 共享列表过滤 IPC/Special；账号=用户+组；逐共享权限并发 4（mapPool）+ 取消位；
    /// 按 account 分组后 PickAccess（Deny 优先））。
    /// </summary>
    public async Task LoadMatrixAsync()
    {
        _matrixCancel = false;
        MatrixLoading = true;
        try
        {
            if (_shares.Count == 0)
                await RefreshAsync(); // 挂载即取数（TS 组件挂载 effect 同语义）

            var normal = Shares;
            var accounts = BuildMatrixAccounts();

            IReadOnlyList<IReadOnlyList<SharePermission>> perms;
            if (_user is null)
            {
                // 服务缺席：逐共享空列表（空馈送降级，矩阵如实空态不崩）
                perms = normal.Select(_ => Array.Empty<SharePermission>()).ToList();
            }
            else
            {
                var svc = _user;
                perms = await PermissionMatrix.MapPool(normal, 4,
                    s => SafePerms(svc, s.name),
                    () => _matrixCancel);
            }
            if (_matrixCancel)
            {
                MatrixNote = "已取消加载";
                Tell("info", "已取消加载");
                return;
            }

            var map = new Dictionary<string, Dictionary<string, MatrixAccess>>();
            for (int i = 0; i < normal.Count; i++)
            {
                var list = perms[i];
                // 按 account 分组（对齐 TS grouped）
                var grouped = list
                    .GroupBy(p => p.account)
                    .ToDictionary(g => g.Key, g => g.ToList());
                var byAccount = new Dictionary<string, MatrixAccess>();
                foreach (var a in accounts)
                    byAccount[a.name] = grouped.TryGetValue(a.name, out var list2)
                        ? PermissionMatrix.PickAccess(list2)
                        : MatrixAccess.None;
                map[normal[i].name] = byAccount;
            }

            _matrixShares = normal;
            _matrixAccounts = accounts;
            _matrixData = map;
            MatrixNote = $"矩阵单元格按 Deny 优先显示（安全语义）。{normal.Count} 共享 × {accounts.Count} 账号。";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatrixShares)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatrixAccounts)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatrixData)));
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
        }
        finally
        {
            MatrixLoading = false;
        }
    }

    /// <summary>矩阵账号列（[Source: PermissionMatrix.tsx acctList]：先用户后组）。</summary>
    IReadOnlyList<MatrixAccount> BuildMatrixAccounts()
    {
        var list = new List<MatrixAccount>();
        foreach (var u in _users) list.Add(new MatrixAccount(u.name, "User"));
        foreach (var g in _groups) list.Add(new MatrixAccount(g.name, "Group"));
        return list;
    }

    static async Task<IReadOnlyList<SharePermission>> SafePerms(IUserService svc, string shareName)
    {
        try { return await svc.GetSharePermissionsAsync(shareName); }
        catch { return Array.Empty<SharePermission>(); } // 单共享失败 → 该共享空行（对齐 TS .catch(() => [])）
    }

    /// <summary>取消矩阵加载（对齐 TS cancel()：置位 + 退出加载态 + "已取消加载"）。</summary>
    public void CancelMatrix()
    {
        _matrixCancel = true;
        MatrixLoading = false;
        MatrixNote = "已取消加载";
        Tell("info", "已取消加载");
    }

    // =====================================================================
    // NTFS 权限查看（对齐 getNtfsPermissions）
    // =====================================================================

    /// <summary>查看路径 NTFS 权限（视图层持有路径输入；结果 → NtfsRows）。</summary>
    public async Task ViewNtfsAsync(string path)
    {
        if (!GuardUser()) return;
        try
        {
            var acl = await _user!.GetNtfsPermissionsAsync(path);
            NtfsPath = acl.path;
            _ntfsRows = acl.entries.Select(e => new NtfsRow(e,
                e.type == NtfsAclType.Deny ? "拒绝" : "允许")).ToList();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NtfsRows)));
        }
        catch (Exception e)
        {
            Tell("error", e.Message);
        }
    }

    // =====================================================================
    // 矩阵导出（对齐 exportCsv / exportJson / exportHtml；文件保存走视图 SaveFileDialog）
    // =====================================================================

    async Task ExportMatrixCsvAsync() =>
        await ExportMatrixFileAsync("csv",
            PermissionMatrix.BuildMatrixCsv(_matrixShares, _matrixAccounts, _matrixData));

    async Task ExportMatrixJsonAsync() =>
        await ExportMatrixFileAsync("json",
            PermissionMatrix.BuildMatrixJson(_matrixShares, _matrixAccounts, _matrixData, DateTimeOffset.Now));

    async Task ExportMatrixHtmlAsync() =>
        await ExportMatrixFileAsync("html",
            PermissionMatrix.BuildHtmlReport(_matrixShares, _matrixAccounts, _matrixData, DateTimeOffset.Now));

    async Task ExportMatrixFileAsync(string ext, string content)
    {
        if (_matrixShares.Count == 0) return; // 对齐 TS：导出按钮 loading||!shares.length 时 disabled
        if (_fileSaver is null)
        {
            Tell("error", "导出不可用（组合根未装配文件保存面）");
            return;
        }
        // [Source: PermissionMatrix.tsx 导出文件名 permission-matrix-{ts}.csv / permission-report-{ts}.html]
        var ts = _nowMs();
        string name = ext == "html"
            ? $"permission-report-{ts}.html"
            : $"permission-matrix-{ts}.{ext}";
        await _fileSaver(name, System.Text.Encoding.UTF8.GetBytes(content));
        Tell("success", ext switch
        {
            "csv" => "CSV 已导出",
            "json" => "JSON 已导出",
            _ => "HTML 报告已导出",
        });
    }

    // ===== INotifyPropertyChanged =====

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
