using System.ComponentModel;
using WinShare.Core.Contracts;

namespace WinShare.App.Shares;

/// <summary>
/// 权限抽屉 ViewModel（对齐 src/components/PermissionDrawer.tsx 共享权限 Tab）：
/// - toRow/toPerm 映射：Deny 显示值 ≙ (access=NoAccess 或 deny=true)；
///   保存时 Deny → NoAccess+deny=true（TS toPerm）
/// - 添加账号三护栏：空账号/已存在（TS handleAdd 逐条对齐）
/// - 候选账号 = 本地用户 + 本地组（TS loadCandidates，前 12 个提示 + "等"）
/// - NTFS Tab：只读 ACL 视图，点了加载才读（TS loadNtfs 事件路径语义）
/// 服务缺席时走占位降级（如实报"未接入"）。
/// </summary>
public sealed class SharePermissionsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    readonly IShareService? _share;
    readonly IUserService? _user;
    readonly ISharesDataFeed _feed;

    public SharePermissionsViewModel(Share share, IShareService? shareService, IUserService? userService, ISharesDataFeed feed)
    {
        Share = share;
        _share = shareService;
        _user = userService;
        _feed = feed;
    }

    public Share Share { get; }

    /// <summary>访问选择（TS ACCESS_OPTIONS 的四个取值）。</summary>
    public static readonly string[] AccessOptions = ["Full", "Change", "Read", "Deny"];
    public static readonly string[] AccessLabels = ["完全控制", "更改", "只读", "拒绝"];

    /// <summary>TS PermRow 的 C# 形态。</summary>
    public sealed class PermRow
    {
        public required string account { get; init; }
        public required AccountType accountType { get; init; }
        public string access { get; init; } = "Read";

        public string Key => account;
    }

    public List<PermRow> Rows { get; private set; } = new();

    /// <summary>TS toRow：deny/NoAccess 一律显示 Deny。</summary>
    static PermRow ToRow(SharePermission p) => new()
    {
        account = p.account,
        accountType = p.accountType,
        access = p.deny || p.access == ShareAccess.NoAccess ? "Deny" : p.access.ToString(),
    };

    /// <summary>TS toPerm：Deny → NoAccess + deny=true；其余按选择落 access。</summary>
    static SharePermission ToPerm(Share share, PermRow r) => new()
    {
        shareName = share.name,
        account = r.account,
        accountType = r.accountType,
        access = r.access == "Deny" ? ShareAccess.NoAccess : Enum.Parse<ShareAccess>(r.access),
        deny = r.access == "Deny",
    };

    /// <summary>候选账号（TS loadCandidates：用户 + 组，前 12 展示）。</summary>
    public List<string> Candidates { get; private set; } = new();
    public string CandidatesTip =>
        Candidates.Count == 0 ? ""
        : $"常用账号：{string.Join("、", Candidates.Take(12))}{(Candidates.Count > 12 ? " 等" : "")}";

    // ===== 加载（打开/换共享/重新加载；nonce 语义 = 按需重读） =====
    public bool Loading { get; private set; }
    public bool NtfsLoading { get; private set; }
    public bool Saving { get; private set; }
    public string NtfsPathText { get; private set; } = "";
    public NtfsAcl? Ntfs { get; private set; }

    public async Task LoadAsync()
    {
        Loading = true;
        RaiseAll();
        try
        {
            if (_share is null)
            {
                Rows = new List<PermRow>();
            }
            else
            {
                var list = await _share.GetSharePermissionsAsync(Share.name);
                Rows = list.Select(ToRow).ToList();
            }
            var (users, groups) = await _feed.ListUsersGroupsAsync();
            Candidates =
                users.Select(u => u.name).Concat(groups.Select(g => g.name)).ToList();
            NtfsPathText = Share.path;
        }
        catch
        {
            // 重新加载失败保持既有行（TS loadPerms catch → message.error；行数据不重置）
        }
        finally
        {
            Loading = false;
            RaiseAll();
        }
    }

    /// <summary>TS loadNtfs：点了加载才读的只读 ACL 视图（事件路径，不在打开路径上）。</summary>
    public async Task<string> LoadNtfsAsync()
    {
        if (_user is null) return "NTFS 读取需要用户契约服务（未接入）";
        NtfsLoading = true;
        RaiseAll();
        try
        {
            Ntfs = await _user.GetNtfsPermissionsAsync(Share.path);
            return "";
        }
        catch (Exception e)
        {
            Ntfs = null;
            return e.Message;
        }
        finally
        {
            NtfsLoading = false;
            RaiseAll();
        }
    }

    // ===== 行编辑（TS handleAdd/handleRemove/handleAccessChange） =====

    /// <summary>添加账号。返回提示文案（"" = 成功；TS 两条 warning 逐字对齐）。</summary>
    public string AddRow(string accountRaw, AccountType type, string access)
    {
        var acct = accountRaw.Trim();
        if (acct.Length == 0) return "请输入账号名";
        if (Rows.Any(r => r.account == acct)) return "该账号已存在";
        Rows.Add(new PermRow { account = acct, accountType = type, access = access });
        RaiseAll();
        return "";
    }

    public void RemoveRow(string account)
    {
        Rows.RemoveAll(r => r.account == account);
        RaiseAll();
    }

    public void SetAccess(string account, string access)
    {
        var row = Rows.FirstOrDefault(r => r.account == account);
        if (row is null) return;
        Rows.Remove(row);
        Rows.Add(new PermRow { account = row.account, accountType = row.accountType, access = access });
        RaiseAll();
    }

    /// <summary>
    /// TS handleSave：rows → toPerm → user.setSharePermissions（含备份+回滚事务补偿在服务层），
    /// 成功后重新加载（nonce bump）。返回提示文案（"" = 成功）。
    /// </summary>
    public async Task<string> SaveAsync()
    {
        if (_user is null) return "权限保存需要用户契约服务（未接入）";
        Saving = true;
        RaiseAll();
        try
        {
            var perms = Rows.Select(r => ToPerm(Share, r)).ToList();
            await _user.SetSharePermissionsAsync(Share.name, perms);
            await LoadAsync();
            return "权限已保存";
        }
        catch (Exception e)
        {
            return e.Message;
        }
        finally
        {
            Saving = false;
            RaiseAll();
        }
    }

    void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(Rows), nameof(Candidates), nameof(CandidatesTip),
            nameof(Loading), nameof(NtfsLoading), nameof(Ntfs),
            nameof(Saving), nameof(NtfsPathText),
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
