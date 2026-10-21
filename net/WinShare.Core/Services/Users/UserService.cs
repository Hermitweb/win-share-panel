// 移植自 electron/services/user.ts（本地用户/组管理 + SMB 共享权限 + NTFS ACL 读取）。
// PS 命令串从 TS 原文件逐字复制（模板字符串插值 → C# 插值；psQuote/psBool/psNumber/psEnum/validate* 归一到
// WinShare.Core.Ps.PsArgs）；执行一律经 IPsInvoker（生产实现 Lib/PsRunner → Ps.PoolSingleton + PsArgs 闸门）。
// TS 的 runPowerShell/runPowerShellVoid 默认参数 { timeout: 15000, retries: 2 }；显式 { retries: 0 } 在 C# 侧
// 以 (command, 15000, 0) 表达；detect/安装域的长超时见 ProtocolDetector。
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Ps;

namespace WinShare.Core.Services.Users;

/// <summary>
/// IUserService 实现（TS: user.ts 全部导出函数）。
/// 依赖 <see cref="IStateStore"/> 记录操作日志（TS addJournal），经注入可替身（同 TS vi.mock 手法）。
/// </summary>
public sealed class UserService : IUserService
{
    private readonly IPsInvoker _ps;
    private readonly IStateStore _store;

    public UserService(IPsInvoker? ps = null, IStateStore? store = null)
    {
        _ps = ps ?? PsRunner.Default;
        _store = store ?? StateStore.Default;
    }

    // === 读域：本地用户 ===

    // TS: `Get-LocalUser | Select-Object Name, FullName, Enabled, Description, PasswordRequired, PasswordChangeable, PasswordExpires, UserMayChangePassword, PasswordLastSet, LastLogon, SID, PrincipalSource`
    private const string ListUsersCommand =
        "Get-LocalUser | Select-Object Name, FullName, Enabled, Description, PasswordRequired, PasswordChangeable, PasswordExpires, UserMayChangePassword, PasswordLastSet, LastLogon, SID, PrincipalSource";

    // TS: `Get-LocalUser -Name ${psQuote(name)} | Select-Object ...`（同上投影）
    private const string GetUserCommandPrefix =
        "Get-LocalUser -Name ";

    private const string UserProjection =
        " | Select-Object Name, FullName, Enabled, Description, PasswordRequired, PasswordChangeable, PasswordExpires, UserMayChangePassword, PasswordLastSet, LastLogon, SID, PrincipalSource";

    // TS: parseTime(raw) —— /Date(123456789000+000)/ → ISO；其余原样
    private static readonly Regex DateWrap = new(@"\/Date\((\d+)(?:[+-]\d+)?\)\/", RegexOptions.Compiled);

    private static string ParseTime(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Undefined || raw.ValueKind == JsonValueKind.Null) return "";
        string s = raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? "" : raw.ToString();
        if (s.Length == 0) return "";
        Match m = DateWrap.Match(s);
        if (m.Success && long.TryParse(m.Groups[1].Value, out long ts))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
                .ToString("yyyy-MM-ddTHH:mm:ss.fff\"Z\"", CultureInfo.InvariantCulture);
        }
        return s;
    }

    // TS: !!raw —— 只接受真 bool
    private static bool Truthy(JsonElement el) =>
        el.ValueKind == JsonValueKind.True;

    // TS: raw.FullName || ''
    private static string Str(JsonElement el) =>
        el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    // TS: u.SID?.Value || u.SID || ''
    private static string SidOf(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("Value", out JsonElement v) && v.ValueKind == JsonValueKind.String)
        {
            string val = v.GetString() ?? "";
            if (val.Length > 0) return val;
        }
        if (el.ValueKind == JsonValueKind.String)
        {
            string s = el.GetString() ?? "";
            if (s.Length > 0) return s;
        }
        return "";
    }

    // TS: arr = Array.isArray(raw) ? raw : [raw]
    private static List<JsonElement> ToArray(JsonElement raw) =>
        raw.ValueKind == JsonValueKind.Array
            ? raw.EnumerateArray().Select(e => e.Clone()).ToList()
            : new List<JsonElement> { raw.Clone() };

    // TS: firstOrNull 归一化（空数组 truthy 缺陷的修正式：无结果返回 null）
    private static JsonElement? FirstOrNull(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Array)
        {
            return raw.GetArrayLength() > 0 ? raw[0].Clone() : null;
        }
        return raw.Clone();
    }

    public async Task<IReadOnlyList<LocalUser>> ListUsersAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync(ListUsersCommand, 15000, 2);
        List<JsonElement> arr = ToArray(raw);
        Dictionary<string, List<string>> groupMap = await BuildUserGroupMapAsync();
        var result = new List<LocalUser>(arr.Count);
        foreach (JsonElement u in arr)
        {
            string name = Str(Prop(u, "Name"));
            result.Add(new LocalUser
            {
                name = name,
                fullName = Str(Prop(u, "FullName")),
                enabled = Truthy(Prop(u, "Enabled")),
                description = Str(Prop(u, "Description")),
                groups = groupMap.TryGetValue(name, out List<string>? gs) ? gs : new List<string>(),
                passwordRequired = Truthy(Prop(u, "PasswordRequired")),
                passwordChangeable = Truthy(Prop(u, "PasswordChangeable")),
                passwordExpires = Truthy(Prop(u, "PasswordExpires")),
                userMayChangePassword = Truthy(Prop(u, "UserMayChangePassword")),
                passwordLastSet = ParseTime(Prop(u, "PasswordLastSet")),
                lastLogon = ParseTime(Prop(u, "LastLogon")),
                sid = SidOf(Prop(u, "SID")),
                principalSource = NonEmpty(Str(Prop(u, "PrincipalSource")), "Local"),
            });
        }
        return result;
    }

    // TS: v || fallback
    private static string NonEmpty(string v, string fallback) => v.Length > 0 ? v : fallback;

    private static JsonElement Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement v) ? v : default;

    // TS: buildUserGroupMap() —— 批量查询避免 N+1；整体失败返回空映射
    private async Task<Dictionary<string, List<string>>> BuildUserGroupMapAsync()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            JsonElement groups = await _ps.RunJsonAsync("Get-LocalGroup | Select-Object Name", 15000, 2);
            foreach (JsonElement g in ToArray(groups))
            {
                string gName = Str(Prop(g, "Name"));
                if (gName.Length == 0) continue;
                try
                {
                    JsonElement members = await _ps.RunJsonAsync(
                        $"Get-LocalGroupMember -Group {PsArgs.PsQuote(gName)} | Select-Object Name, ObjectClass, PrincipalSource",
                        15000, 0);
                    foreach (JsonElement m in ToArray(members))
                    {
                        string mName = Str(Prop(m, "Name"));
                        if (mName.Length == 0) continue;
                        // Name 形如 "COMPUTERNAME\username"，取反斜杠后部分
                        int bs = mName.LastIndexOf('\\');
                        string userName = bs >= 0 ? mName[(bs + 1)..] : mName;
                        if (userName.Length == 0) userName = mName;
                        if (!map.TryGetValue(userName, out List<string>? list))
                        {
                            list = new List<string>();
                            map[userName] = list;
                        }
                        if (!list.Contains(gName)) list.Add(gName);
                    }
                }
                catch
                {
                    // 某些组（如特殊系统组）查询成员可能失败
                }
            }
        }
        catch
        {
            // 整体失败时返回空映射，用户列表仍可用
        }
        return map;
    }

    public async Task<IReadOnlyList<LocalGroup>> ListGroupsAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync("Get-LocalGroup | Select-Object Name, Description", 15000, 2);
        var groups = new List<LocalGroup>();
        foreach (JsonElement g in ToArray(raw))
        {
            var members = new List<GroupMember>();
            try
            {
                JsonElement mraw = await _ps.RunJsonAsync(
                    $"Get-LocalGroupMember -Group {PsArgs.PsQuote(Str(Prop(g, "Name")))} | Select-Object Name, ObjectClass, PrincipalSource",
                    15000, 0);
                foreach (JsonElement m in ToArray(mraw))
                {
                    string mName = Str(Prop(m, "Name"));
                    int bs = mName.LastIndexOf('\\');
                    string name = bs >= 0 ? mName[(bs + 1)..] : mName;
                    if (name.Length == 0) name = mName;
                    members.Add(new GroupMember
                    {
                        name = name,
                        objectClass = Str(Prop(m, "ObjectClass")) == "Group" ? MemberObjectClass.Group : MemberObjectClass.User,
                        principalSource = NonEmpty(Str(Prop(m, "PrincipalSource")), "Local"),
                    });
                }
            }
            catch
            {
                members = new List<GroupMember>();
            }
            groups.Add(new LocalGroup
            {
                name = Str(Prop(g, "Name")),
                description = Str(Prop(g, "Description")),
                members = members,
            });
        }
        return groups;
    }

    public async Task<LocalUser> GetUserAsync(string name)
    {
        if (!PsArgs.ValidateName(name)) throw Errors.InvalidParam("用户名非法");
        JsonElement raw = await _ps.RunJsonAsync(
            $"Get-LocalUser -Name {PsArgs.PsQuote(name)}{UserProjection}", 15000, 2);
        JsonElement? single = FirstOrNull(raw);
        string userName = single is null ? "" : Str(Prop(single.Value, "Name"));
        if (userName.Length == 0) throw Errors.InvalidParam($"用户 {name} 不存在");
        Dictionary<string, List<string>> groupMap = await BuildUserGroupMapAsync();
        JsonElement r = single!.Value;
        return new LocalUser
        {
            name = userName,
            fullName = Str(Prop(r, "FullName")),
            enabled = Truthy(Prop(r, "Enabled")),
            description = Str(Prop(r, "Description")),
            groups = groupMap.TryGetValue(userName, out List<string>? gs) ? gs : new List<string>(),
            passwordRequired = Truthy(Prop(r, "PasswordRequired")),
            passwordChangeable = Truthy(Prop(r, "PasswordChangeable")),
            passwordExpires = Truthy(Prop(r, "PasswordExpires")),
            userMayChangePassword = Truthy(Prop(r, "UserMayChangePassword")),
            passwordLastSet = ParseTime(Prop(r, "PasswordLastSet")),
            lastLogon = ParseTime(Prop(r, "LastLogon")),
            sid = SidOf(Prop(r, "SID")),
            principalSource = NonEmpty(Str(Prop(r, "PrincipalSource")), "Local"),
        };
    }

    // === 写域：用户 CRUD ===

    public async Task CreateUserAsync(CreateUserOpts opts)
    {
        if (!PsArgs.ValidateName(opts.name)) throw Errors.InvalidParam("用户名非法");
        if (string.IsNullOrEmpty(opts.password)) throw Errors.InvalidParam("密码不能为空");
        if (opts.password.Length > 127) throw Errors.InvalidParam("密码长度不能超过 127 字符");
        var parts = new List<string>
        {
            "New-LocalUser",
            $"-Name {PsArgs.PsQuote(opts.name)}",
            $"-Password (ConvertTo-SecureString -AsPlainText -Force {PsArgs.PsQuote(opts.password)})",
        };
        if (opts.fullName is not null) parts.Add($"-FullName {PsArgs.PsQuote(opts.fullName)}");
        if (opts.description is not null) parts.Add($"-Description {PsArgs.PsQuote(opts.description)}");
        if (opts.enabled == false) parts.Add("-Disabled");
        if (opts.passwordChangeable == false) parts.Add("-UserMayNotChangePassword");
        if (opts.passwordExpires == true) parts.Add("-PasswordNeverExpires:$false");
        else parts.Add("-PasswordNeverExpires");
        await _ps.RunVoidAsync(string.Join(' ', parts), 15000, 0);
    }

    public async Task UpdateUserAsync(string name, UpdateUserOpts opts)
    {
        if (!PsArgs.ValidateName(name)) throw Errors.InvalidParam("用户名非法");
        var parts = new List<string> { "Set-LocalUser", $"-Name {PsArgs.PsQuote(name)}" };
        if (opts.fullName is not null) parts.Add($"-FullName {PsArgs.PsQuote(opts.fullName)}");
        if (opts.description is not null) parts.Add($"-Description {PsArgs.PsQuote(opts.description)}");
        if (opts.enabled == false) parts.Add("-Disabled");
        else if (opts.enabled == true) parts.Add("-Enabled");
        if (opts.passwordChangeable == false) parts.Add("-UserMayNotChangePassword");
        else if (opts.passwordChangeable == true) parts.Add("-UserMayChangePassword");
        if (opts.passwordExpires == true) parts.Add("-PasswordNeverExpires:$false");
        else if (opts.passwordExpires == false) parts.Add("-PasswordNeverExpires");
        await _ps.RunVoidAsync(string.Join(' ', parts), 15000, 0);
    }

    // 系统保护：内置 Administrator/Guest/DefaultAccount 等不允许删除
    private static readonly string[] ProtectedUsers = { "administrator", "guest", "defaultaccount", "wdagutilityaccount" };

    public async Task DeleteUserAsync(string name)
    {
        if (!PsArgs.ValidateName(name)) throw Errors.InvalidParam("用户名非法");
        if (ProtectedUsers.Contains(name.ToLowerInvariant()))
        {
            throw Errors.InvalidParam($"系统内置用户 {name} 不允许删除");
        }
        await _ps.RunVoidAsync($"Remove-LocalUser -Name {PsArgs.PsQuote(name)}", 15000, 0);
    }

    public async Task SetUserPasswordAsync(string name, string password)
    {
        if (!PsArgs.ValidateName(name)) throw Errors.InvalidParam("用户名非法");
        if (string.IsNullOrEmpty(password)) throw Errors.InvalidParam("密码不能为空");
        if (password.Length > 127) throw Errors.InvalidParam("密码长度不能超过 127 字符");
        await _ps.RunVoidAsync(
            $"Set-LocalUser -Name {PsArgs.PsQuote(name)} -Password (ConvertTo-SecureString -AsPlainText -Force {PsArgs.PsQuote(password)})",
            15000, 0);
    }

    public async Task EnableUserAsync(string name)
    {
        if (!PsArgs.ValidateName(name)) throw Errors.InvalidParam("用户名非法");
        await _ps.RunVoidAsync($"Enable-LocalUser -Name {PsArgs.PsQuote(name)}", 15000, 0);
    }

    public async Task DisableUserAsync(string name)
    {
        if (!PsArgs.ValidateName(name)) throw Errors.InvalidParam("用户名非法");
        await _ps.RunVoidAsync($"Disable-LocalUser -Name {PsArgs.PsQuote(name)}", 15000, 0);
    }

    public async Task RenameUserAsync(string oldName, string newName)
    {
        if (!PsArgs.ValidateName(oldName)) throw Errors.InvalidParam("原用户名非法");
        if (!PsArgs.ValidateName(newName)) throw Errors.InvalidParam("新用户名非法");
        if (oldName == newName) return;
        await _ps.RunVoidAsync(
            $"Rename-LocalUser -Name {PsArgs.PsQuote(oldName)} -NewName {PsArgs.PsQuote(newName)}", 15000, 0);
    }

    // === 写域：组管理 ===

    public async Task CreateGroupAsync(CreateGroupOpts opts)
    {
        if (!PsArgs.ValidateName(opts.name)) throw Errors.InvalidParam("组名非法");
        var parts = new List<string> { "New-LocalGroup", $"-Name {PsArgs.PsQuote(opts.name)}" };
        if (opts.description is not null) parts.Add($"-Description {PsArgs.PsQuote(opts.description)}");
        await _ps.RunVoidAsync(string.Join(' ', parts), 15000, 0);
    }

    // 系统内置组保护列表（删除/重命名均禁止）
    private static readonly string[] ProtectedGroups =
    {
        "administrators", "users", "guests", "power users", "account operators", "server operators",
        "print operators", "backup operators", "replicator", "iis_iusrs", "network configuration operators",
        "performance monitor users", "performance log users", "remote desktop users", "system operators",
        "cryptographic operators", "event log readers", "certificate service dcom access",
    };

    public async Task DeleteGroupAsync(string name)
    {
        if (!PsArgs.ValidateName(name)) throw Errors.InvalidParam("组名非法");
        if (ProtectedGroups.Contains(name.ToLowerInvariant()))
        {
            throw Errors.InvalidParam($"系统内置组 {name} 不允许删除");
        }
        await _ps.RunVoidAsync($"Remove-LocalGroup -Name {PsArgs.PsQuote(name)}", 15000, 0);
    }

    public async Task UpdateGroupAsync(string name, string description)
    {
        if (!PsArgs.ValidateName(name)) throw Errors.InvalidParam("组名非法");
        await _ps.RunVoidAsync(
            $"Set-LocalGroup -Name {PsArgs.PsQuote(name)} -Description {PsArgs.PsQuote(description)}", 15000, 0);
    }

    public async Task RenameGroupAsync(string name, string newName)
    {
        if (!PsArgs.ValidateName(name)) throw Errors.InvalidParam("原组名非法");
        if (!PsArgs.ValidateName(newName)) throw Errors.InvalidParam("新组名非法");
        if (name == newName) return;
        if (ProtectedGroups.Contains(name.ToLowerInvariant()))
        {
            throw Errors.InvalidParam($"系统内置组 {name} 不允许重命名");
        }
        await _ps.RunVoidAsync(
            $"Rename-LocalGroup -Name {PsArgs.PsQuote(name)} -NewName {PsArgs.PsQuote(newName)}", 15000, 0);
    }

    public async Task AddGroupMemberAsync(string group, string member)
    {
        if (!PsArgs.ValidateName(group)) throw Errors.InvalidParam("组名非法");
        // A1：成员允许 DOMAIN\user、机器账户尾 $ 等形态（psQuote 单引号上下文内嵌入）
        if (!PsArgs.ValidateAccountName(member)) throw Errors.InvalidParam("成员名非法");
        await _ps.RunVoidAsync(
            $"Add-LocalGroupMember -Group {PsArgs.PsQuote(group)} -Member {PsArgs.PsQuote(member)}", 15000, 0);
    }

    public async Task RemoveGroupMemberAsync(string group, string member)
    {
        if (!PsArgs.ValidateName(group)) throw Errors.InvalidParam("组名非法");
        if (!PsArgs.ValidateAccountName(member)) throw Errors.InvalidParam("成员名非法");
        await _ps.RunVoidAsync(
            $"Remove-LocalGroupMember -Group {PsArgs.PsQuote(group)} -Member {PsArgs.PsQuote(member)}", 15000, 0);
    }

    // === 读域：SMB 共享权限 ===

    public async Task<IReadOnlyList<SharePermission>> GetSharePermissionsAsync(string shareName)
    {
        // 读取路径：允许系统共享尾 $（IPC$ 等，B-4）；setSharePermissions 写入保持严格
        if (!PsArgs.ValidateShareName(shareName)) throw Errors.InvalidParam("共享名非法");
        JsonElement r = await _ps.RunJsonAsync($"Get-SmbShareAccess -Name {PsArgs.PsQuote(shareName)}", 15000, 2);
        var result = new List<SharePermission>();
        foreach (JsonElement x in ToArray(r))
        {
            result.Add(new SharePermission
            {
                shareName = shareName,
                account = Str(Prop(x, "AccountName")),
                accountType = IntOf(Prop(x, "AccountType")) == 1 ? AccountType.User : AccountType.Group,
                access = AccessOf(IntOf(Prop(x, "AccessRight"))),
                deny = IntOf(Prop(x, "AccessControlType")) == 1,
            });
        }
        return result;
    }

    // TS: x.AccessRight === 0 ? 'Full' : x.AccessRight === 1 ? 'Change' : 'Read'
    private static ShareAccess AccessOf(int v) =>
        v == 0 ? ShareAccess.Full : v == 1 ? ShareAccess.Change : ShareAccess.Read;

    private static int IntOf(JsonElement el) =>
        el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n) ? n : -1;

    // 按用户名查询该用户在所有 SMB 共享上的权限（单次 PowerShell 批量查询）
    public async Task<IReadOnlyList<SharePermission>> GetUserSharePermissionsAsync(string username)
    {
        if (!PsArgs.ValidateName(username)) throw Errors.InvalidParam("用户名非法");
        JsonElement r = await _ps.RunJsonAsync(
            @"Get-SmbShare | ForEach-Object { $sn = $_.Name; Get-SmbShareAccess -Name $sn -ErrorAction SilentlyContinue | ForEach-Object { [PSCustomObject]@{ shareName = $sn; account = $_.AccountName; accessRight = $_.AccessRight; accountType = $_.AccountType; accessControlType = $_.AccessControlType } } } | Where-Object { $_.account -ieq "
            + PsArgs.PsQuote(username)
            + @" -or $_.account -ilike ""*\"
            + username
            + @""" }", 15000, 2);
        var result = new List<SharePermission>();
        foreach (JsonElement x in ToArray(r))
        {
            result.Add(new SharePermission
            {
                shareName = Str(Prop(x, "shareName")),
                account = Str(Prop(x, "account")),
                accountType = IntOf(Prop(x, "accountType")) == 1 ? AccountType.User : AccountType.Group,
                access = AccessOf(IntOf(Prop(x, "accessRight"))),
                deny = IntOf(Prop(x, "accessControlType")) == 1,
            });
        }
        return result;
    }

    // TS: `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`
    private static string PermText(SharePermission p) => $"{p.account}={p.access}{(p.deny ? "(deny)" : "")}";

    public async Task SetSharePermissionsAsync(string shareName, IReadOnlyList<SharePermission> perms)
    {
        if (!PsArgs.ValidateShareName(shareName)) throw Errors.InvalidParam("共享名非法");
        // 入参校验：account 必须合法，不静默跳过（避免用户误以为权限已设但实际未设）
        // A1：account 用账户校验（放行 DOMAIN\user、NT AUTHORITY\SYSTEM、尾 $）
        List<SharePermission> invalid = perms.Where(p => !PsArgs.ValidateAccountName(p.account)).ToList();
        if (invalid.Count > 0)
        {
            throw Errors.InvalidParam($"账号名非法：{string.Join(", ", invalid.Select(p => p.account))}");
        }

        // 事务补偿：先备份当前权限，若后续 Grant/Block 中途失败则回滚到原状态
        List<SharePermission> backup;
        try
        {
            backup = (await GetSharePermissionsAsync(shareName)).ToList();
        }
        catch
        {
            backup = new List<SharePermission>();
        }
        Logger.Info("user", "[setPermissions:smb] 已备份当前权限:", backup.Count, "条 →",
            backup.Count > 0 ? string.Join(", ", backup.Select(PermText)) : "(空)");

        // overwrite：先清空所有已授予与已拒绝项
        await _ps.RunVoidAsync(
            $@"Get-SmbShareAccess -Name {PsArgs.PsQuote(shareName)} | ForEach-Object {{ Revoke-SmbShareAccess -Name {PsArgs.PsQuote(shareName)} -AccountName $_.AccountName -Force }}",
            15000, 2);
        // Unblock 当前所有 Deny 项（Revoke 不一定清除 Block，需显式 Unblock）
        try
        {
            await _ps.RunVoidAsync(
                $@"Get-SmbShareAccess -Name {PsArgs.PsQuote(shareName)} | Where-Object {{ $_.AccessControlType -eq 1 }} | ForEach-Object {{ Unblock-SmbShareAccess -Name {PsArgs.PsQuote(shareName)} -AccountName $_.AccountName -Force }}",
                15000, 2);
        }
        catch
        {
            // 旧版 Windows 可能无 Unblock-SmbShareAccess cmdlet，降级：仅 Revoke 已足够清空
        }

        // 逐个 Grant/Block，收集失败项
        var failed = new List<string>();
        foreach (SharePermission p in perms)
        {
            if (p.deny || p.access == ShareAccess.NoAccess)
            {
                try
                {
                    await _ps.RunVoidAsync(
                        $@"Block-SmbShareAccess -Name {PsArgs.PsQuote(shareName)} -AccountName {PsArgs.PsQuote(p.account)} -Force",
                        15000, 2);
                }
                catch
                {
                    // 无 Block-SmbShareAccess 时降级为"不授予"（仅 Revoke 状态）
                    failed.Add(p.account);
                }
            }
            else
            {
                string right = p.access == ShareAccess.Full ? "Full" : p.access == ShareAccess.Change ? "Change" : "Read";
                try
                {
                    await _ps.RunVoidAsync(
                        $@"Grant-SmbShareAccess -Name {PsArgs.PsQuote(shareName)} -AccountName {PsArgs.PsQuote(p.account)} -AccessRight {right} -Force",
                        15000, 2);
                }
                catch
                {
                    failed.Add(p.account);
                }
            }
        }

        // 若有失败项：尝试回滚到备份状态，保证共享不处于"全空"危险状态
        if (failed.Count > 0)
        {
            Logger.Error("user", "[setPermissions:smb] 回滚触发！失败账号:", string.Join(", ", failed));
            // 查询回滚前的当前权限状态（部分授予后的残留状态）
            List<SharePermission> beforeRollback;
            try
            {
                beforeRollback = (await GetSharePermissionsAsync(shareName)).ToList();
            }
            catch
            {
                beforeRollback = new List<SharePermission>();
            }
            Logger.Info("user", "[setPermissions:smb] 回滚前权限状态:", beforeRollback.Count, "条 →",
                beforeRollback.Count > 0 ? string.Join(", ", beforeRollback.Select(PermText)) : "(空)");
            // 静默回滚：重新清空 + 重新授予备份权限
            try
            {
                await _ps.RunVoidAsync(
                    $@"Get-SmbShareAccess -Name {PsArgs.PsQuote(shareName)} | ForEach-Object {{ Revoke-SmbShareAccess -Name {PsArgs.PsQuote(shareName)} -AccountName $_.AccountName -Force }}",
                    15000, 2);
                foreach (SharePermission p in backup)
                {
                    if (p.deny)
                    {
                        await TryVoid($@"Block-SmbShareAccess -Name {PsArgs.PsQuote(shareName)} -AccountName {PsArgs.PsQuote(p.account)} -Force");
                    }
                    else
                    {
                        string right = p.access == ShareAccess.Full ? "Full" : p.access == ShareAccess.Change ? "Change" : "Read";
                        await TryVoid($@"Grant-SmbShareAccess -Name {PsArgs.PsQuote(shareName)} -AccountName {PsArgs.PsQuote(p.account)} -AccessRight {right} -Force");
                    }
                }
            }
            catch
            {
                // 回滚失败：已尽力，抛出原始错误
                Logger.Error("user", "[setPermissions:smb] 回滚过程出错！");
            }
            // 查询回滚后的权限状态，验证是否恢复成功
            List<SharePermission> afterRollback;
            try
            {
                afterRollback = (await GetSharePermissionsAsync(shareName)).ToList();
            }
            catch
            {
                afterRollback = new List<SharePermission>();
            }
            Logger.Info("user", "[setPermissions:smb] 回滚后权限状态:", afterRollback.Count, "条 →",
                afterRollback.Count > 0 ? string.Join(", ", afterRollback.Select(PermText)) : "(空)");
            throw Errors.CommandFailed(
                $"部分权限设置失败（{failed.Count} 个账号：{string.Join(", ", failed.Take(3))}{(failed.Count > 3 ? " 等" : "")}），已回滚到原状态");
        }
        // 操作日志：全部成功才记账，携带改前权限快照（撤销=恢复该快照）
        _store.AddJournal(new JournalEntryDraft
        {
            action = JournalAction.Permset,
            protocol = Protocol.Smb,
            name = shareName,
            detail = $"{perms.Count} 条授权已生效",
            undoable = true,
            snapshot = backup,
        });
    }

    // TS: runPowerShellVoid(cmd).catch(() => undefined)
    private async Task TryVoid(string cmd)
    {
        try
        {
            await _ps.RunVoidAsync(cmd, 15000, 2);
        }
        catch
        {
            // best-effort
        }
    }

    public async Task<NtfsAcl> GetNtfsPermissionsAsync(string path)
    {
        if (!PsArgs.ValidatePath(path)) throw Errors.InvalidParam("路径非法");
        JsonElement raw = await _ps.RunJsonAsync(
            $"Get-Acl -Path {PsArgs.PsQuote(path)} | Select-Object -ExpandProperty Access", 15000, 2);
        var entries = new List<NtfsAclEntry>();
        foreach (JsonElement a in ToArray(raw))
        {
            entries.Add(new NtfsAclEntry
            {
                account = Str(Prop(Prop(a, "IdentityReference"), "Value")) is { Length: > 0 } v ? v : Str(Prop(a, "IdentityReference")),
                rights = Str(Prop(a, "FileSystemRights")),
                type = IntOf(Prop(a, "AccessControlType")) == 0 ? NtfsAclType.Allow : NtfsAclType.Deny,
                inherited = Truthy(Prop(a, "IsInherited")),
            });
        }
        return new NtfsAcl { path = path, entries = entries };
    }
}
