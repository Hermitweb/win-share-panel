// 移植自 electron/services/protocol/adapters/nfsAdapter.ts（NFS 协议适配器）。
// 与 smbAdapter.ts 的「纯委托」不同：列表/创建/删除/更新/权限/会话/关闭会话在适配器内自带
// PS 命令串（New-/Set-/Remove-/Grant-/Revoke-NfsSharePermission、Get-/Disconnect-NfsSession），
// 只有服务配置类方法（getServiceStatus/restartService/getConfig/setConfig/defaultConfig/restoreDefault）
// 委托给同名服务 NfsService。命令串逐字复制 TS，参数一律过 PsGate 闸门。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.ProtocolServices; // ConfigJson：PS 回读 JS 语义取值
using WinShare.Core.Services.Shares; // PsGate：TS powershell.ts 闸门窄门面

namespace WinShare.Core.Services.Protocols.Adapters;

/// <summary>NFS 协议适配器（TS: nfsAdapter.ts nfsAdapter）。</summary>
public sealed class NfsAdapter : IProtocolAdapter
{
    private const int Timeout = PsRunner.DefaultTimeoutMs; // TS: DEFAULT_TIMEOUT = 15000

    // 枚举白名单（与 types.ts 声明一致），拼入命令前运行时校验，杜绝注入
    private static readonly IReadOnlySet<string> NfsAuthModes =
        new HashSet<string> { "krb5", "krb5i", "krb5p", "sys" };

    private static readonly IReadOnlySet<string> NfsPermissions = new HashSet<string> { "ro", "rw" };

    private readonly INfsService _nfs;
    private readonly IPsInvoker _ps;
    private readonly IStateStore _store;

    public NfsAdapter(INfsService? nfs = null, IPsInvoker? ps = null, IStateStore? store = null)
    {
        _ps = ps ?? PsRunner.Default;
        _nfs = nfs ?? new NfsService(_ps);
        _store = store ?? StateStore.Default;
    }

    public Protocol Protocol => Protocol.Nfs;

    // TS: capabilities —— 无 toggle（NFS 共享无启停概念）、无 openFiles
    public ProtocolCapabilities Capabilities { get; } = new()
    {
        supportsCreate = true,
        supportsUpdate = true,
        supportsDelete = true,
        supportsToggle = false,
        supportsPermissions = true,
        supportsSessions = true,
        supportsOpenFiles = false,
        supportsServerConfig = true,
        supportsRestart = true,
        permissionModel = PermissionModel.NfsKrb,
    };

    // TS: listShares() —— retries:0 避免未装 NFS 角色时无谓重试 2 次造成切 Tab 延迟
    public async Task<IReadOnlyList<Share>> ListSharesAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync("Get-NfsShare", Timeout, 0);
        return AdapterHelpers.Items(raw).Select(MapNfsShare).ToList();
    }

    public async Task<Share> CreateShareAsync(CreateShareInput input)
    {
        Logger.Info("adapter:nfsAdapter", "[createShare:nfs] 适配器接收输入:", input.name, input.path,
            AuthenticationText(input.authentication), PermText(input.nfsPermission));
        if (!PsGate.ValidateName(input.name)) throw Errors.InvalidParam("共享名非法");
        if (!PsGate.ValidatePath(input.path)) throw Errors.InvalidParam("路径非法");

        List<string> parts = new()
        {
            "New-NfsShare",
            $"-Name {PsGate.PsQuote(input.name)}",
            $"-Path {PsGate.PsQuote(input.path)}",
        };
        // 认证模式数组：逐项白名单校验后拼接（TS: authRaw.map(psEnum).filter(!!)）
        List<string> auth = new();
        foreach (NfsAuthMethod method in input.authentication ?? new[] { NfsAuthMethod.Sys })
        {
            if (PsGate.PsEnum(AuthText(method), NfsAuthModes) is string a) auth.Add(a);
        }
        parts.Add($"-Authentication {(auth.Count > 0 ? string.Join(',', auth) : "sys")}");
        parts.Add($"-Permission {PsGate.PsEnum(PermOrNull(input.nfsPermission), NfsPermissions) ?? "rw"}");
        if (PsGate.PsBool(input.enableUnmappedAccess) is string uma) parts.Add($"-EnableUnmappedAccess {uma}");
        if (PsGate.PsBool(input.allowRootAccess) is string ra) parts.Add($"-AllowRootAccess {ra}");
        if (PsGate.PsNumber(input.anonymousUid) is string uid) parts.Add($"-AnonymousUid {uid}");
        if (PsGate.PsNumber(input.anonymousGid) is string gid) parts.Add($"-AnonymousGid {gid}");
        string cmd = string.Join(' ', parts);
        Logger.Info("adapter:nfsAdapter", "[createShare:nfs] PowerShell 命令:", cmd);
        try
        {
            await _ps.RunVoidAsync(cmd, Timeout, PsRunner.DefaultRetries);
            Logger.Info("adapter:nfsAdapter", "[createShare:nfs] New-NfsShare 执行成功，正在读取共享信息...");
            JsonElement readback = await _ps.RunJsonAsync(
                $"Get-NfsShare -Name {PsGate.PsQuote(input.name)}", Timeout, PsRunner.DefaultRetries);
            JsonElement share = ConfigJson.Obj(readback);
            // B-2：回读判空守卫（同 share.ts）——回读不到即判失败，走孤儿清理路径重抛
            if (string.IsNullOrEmpty(ConfigJson.Str(share, "Name")))
            {
                throw Errors.CommandFailed("NFS 共享创建已执行但未能回读到该共享，请检查执行权限（需管理员）或 NFS 服务状态");
            }
            Logger.Info("adapter:nfsAdapter", "[createShare:nfs] 共享创建完成:", input.name);
            _store.AddJournal(new JournalEntryDraft
            {
                action = JournalAction.Create,
                protocol = Protocol.Nfs,
                name = input.name,
                detail = $"路径 {input.path}",
                undoable = false,
            });
            return MapNfsShare(share);
        }
        catch (Exception e)
        {
            Logger.Error("adapter:nfsAdapter", "[createShare:nfs] 创建失败:", input.name, e.Message);
            // 清理可能的孤儿共享（New-NfsShare 成功但 Get-NfsShare 失败的情况）
            Logger.Info("adapter:nfsAdapter", "[createShare:nfs] 尝试清理可能的孤儿共享...");
            await _ps.RunVoidAsync(
                $"try {{ Remove-NfsShare -Name {PsGate.PsQuote(input.name)} -Force -ErrorAction Stop }} catch {{}}",
                Timeout, 0);
            // 验证清理结果（TS: .catch(() => null) → 视为已清理）
            bool stillExists = false;
            try
            {
                JsonElement probe = await _ps.RunJsonAsync(
                    $"try {{ Get-NfsShare -Name {PsGate.PsQuote(input.name)} -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Name }} catch {{}}",
                    Timeout, 0);
                stillExists = AdapterHelpers.JsTruthy(AdapterHelpers.FirstOrNull(probe));
            }
            catch
            {
                stillExists = false;
            }
            if (stillExists)
            {
                Logger.Error("adapter:nfsAdapter", "[createShare:nfs] 孤儿共享清理失败！共享仍存在:", input.name);
            }
            else
            {
                Logger.Info("adapter:nfsAdapter", "[createShare:nfs] 孤儿共享已确认清理:", input.name);
            }
            throw;
        }
    }

    public async Task DeleteShareAsync(string name)
    {
        Logger.Info("adapter:nfsAdapter", "[deleteShare:nfs] 删除共享:", name);
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("共享名非法");
        string cmd = $"Remove-NfsShare -Name {PsGate.PsQuote(name)} -Force";
        Logger.Info("adapter:nfsAdapter", "[deleteShare:nfs] PowerShell 命令:", cmd);
        try
        {
            await _ps.RunVoidAsync(cmd, Timeout, PsRunner.DefaultRetries);
            Logger.Info("adapter:nfsAdapter", "[deleteShare:nfs] 删除成功:", name);
            // 操作日志：NFS 共享参数（认证模式/权限/UID映射）无还原通道，留档但标记不可撤销
            _store.AddJournal(new JournalEntryDraft
            {
                action = JournalAction.Delete,
                protocol = Protocol.Nfs,
                name = name,
                detail = "共享已删除（NFS 共享参数不支持一键撤销，请按留档手工重建）",
                undoable = false,
            });
        }
        catch (Exception e)
        {
            Logger.Error("adapter:nfsAdapter", "[deleteShare:nfs] 删除失败:", name, e.Message);
            throw;
        }
    }

    public async Task<Share> UpdateShareAsync(string name, UpdateShareInput input)
    {
        Logger.Info("adapter:nfsAdapter", "[updateShare:nfs] 更新共享:", name,
            PermText(input.nfsPermission), input.allowRootAccess, input.enableUnmappedAccess);
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("共享名非法");
        List<string> parts = new() { "Set-NfsShare", $"-Name {PsGate.PsQuote(name)}" };
        if (PsGate.PsEnum(PermOrNull(input.nfsPermission), NfsPermissions) is string perm) parts.Add($"-Permission {perm}");
        if (PsGate.PsBool(input.allowRootAccess) is string ra) parts.Add($"-AllowRootAccess {ra}");
        if (PsGate.PsBool(input.enableUnmappedAccess) is string uma) parts.Add($"-EnableUnmappedAccess {uma}");
        parts.Add("-Confirm:$false");
        string cmd = string.Join(' ', parts);
        Logger.Info("adapter:nfsAdapter", "[updateShare:nfs] PowerShell 命令:", cmd);
        try
        {
            await _ps.RunVoidAsync(cmd, Timeout, PsRunner.DefaultRetries);
            Logger.Info("adapter:nfsAdapter", "[updateShare:nfs] 更新成功，正在读取共享信息...");
            JsonElement raw = await _ps.RunJsonAsync(
                $"Get-NfsShare -Name {PsGate.PsQuote(name)}", Timeout, PsRunner.DefaultRetries);
            Logger.Info("adapter:nfsAdapter", "[updateShare:nfs] 共享更新完成:", name);
            return MapNfsShare(ConfigJson.Obj(raw));
        }
        catch (Exception e)
        {
            Logger.Error("adapter:nfsAdapter", "[updateShare:nfs] 更新失败:", name, e.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<SharePermission>> GetPermissionsAsync(string name)
    {
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("共享名非法");
        // retries:0 避免未装 NFS 时无谓重试
        JsonElement raw = await _ps.RunJsonAsync(
            $"Get-NfsSharePermission -Name {PsGate.PsQuote(name)}", Timeout, 0);
        return AdapterHelpers.Items(raw).Select(x => new SharePermission
        {
            shareName = name,
            account = ConfigJson.JsString(x, "ClientName") ?? "",
            accountType = AccountType.Group,
            access = ConfigJson.Str(x, "Permission") == "rw" ? ShareAccess.Change : ShareAccess.Read,
            // TS: String(x.Type).toLowerCase() === 'deny'
            deny = string.Equals(ConfigJson.JsString(x, "Type"), "deny", StringComparison.OrdinalIgnoreCase),
        }).ToList();
    }

    public async Task SetPermissionsAsync(string name, IReadOnlyList<SharePermission> perms)
    {
        Logger.Info("adapter:nfsAdapter", "[setPermissions:nfs] 设置权限:", name, perms.Count, PermsText(perms));
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("共享名非法");
        // 事务补偿：先备份当前权限，若后续授予中途失败则回滚到原状态
        IReadOnlyList<SharePermission> backup = await TryGetPermissionsAsync(name);
        Logger.Info("adapter:nfsAdapter", "[setPermissions:nfs] 已备份当前权限:", backup.Count, "条 →", PermsText(backup));
        // 先清空所有已配置权限
        try
        {
            JsonElement existing = await _ps.RunJsonAsync(
                $"Get-NfsSharePermission -Name {PsGate.PsQuote(name)}", Timeout, PsRunner.DefaultRetries);
            List<JsonElement> arr = AdapterHelpers.Items(existing);
            Logger.Info("adapter:nfsAdapter", "[setPermissions:nfs] 清空已有权限:", arr.Count, "条");
            foreach (JsonElement e in arr)
            {
                await _ps.RunVoidAsync(
                    $"Revoke-NfsSharePermission -Name {PsGate.PsQuote(name)} -ClientName {PsGate.PsQuote(ConfigJson.JsString(e, "ClientName") ?? "")} -Confirm:$false",
                    Timeout, PsRunner.DefaultRetries);
            }
        }
        catch
        {
            // 清空失败不阻塞
        }
        // 逐个授予，收集失败项（NoAccess 无对应授予语义——前面已清空全部权限，跳过即可）
        List<string> failed = new();
        foreach (SharePermission p in perms)
        {
            if (p.access == ShareAccess.NoAccess) continue;
            string perm = p.access is ShareAccess.Full or ShareAccess.Change ? "rw" : "ro";
            string denyFlag = p.deny ? "Deny" : "Allow";
            string cmd =
                $"Grant-NfsSharePermission -Name {PsGate.PsQuote(name)} -ClientName {PsGate.PsQuote(p.account)} -Permission {perm} -Type {denyFlag} -Confirm:$false";
            Logger.Info("adapter:nfsAdapter", "[setPermissions:nfs] 授予权限, PowerShell 命令:", cmd);
            try
            {
                await _ps.RunVoidAsync(cmd, Timeout, PsRunner.DefaultRetries);
            }
            catch (Exception e)
            {
                Logger.Error("adapter:nfsAdapter", "[setPermissions:nfs] 授予失败:", p.account, e.Message);
                failed.Add(p.account);
            }
        }
        // 若有失败项：回滚到备份状态，避免共享处于部分权限的危险状态
        if (failed.Count > 0)
        {
            Logger.Error("adapter:nfsAdapter", "[setPermissions:nfs] 回滚触发！失败账号:", string.Join(", ", failed));
            IReadOnlyList<SharePermission> beforeRollback = await TryGetPermissionsAsync(name);
            Logger.Info("adapter:nfsAdapter", "[setPermissions:nfs] 回滚前权限状态:", beforeRollback.Count, "条 →",
                PermsText(beforeRollback));
            // 清空当前（可能部分已授予）
            try
            {
                JsonElement current = await _ps.RunJsonAsync(
                    $"Get-NfsSharePermission -Name {PsGate.PsQuote(name)}", Timeout, PsRunner.DefaultRetries);
                foreach (JsonElement c in AdapterHelpers.Items(current))
                {
                    await _ps.RunVoidAsync(
                        $"Revoke-NfsSharePermission -Name {PsGate.PsQuote(name)} -ClientName {PsGate.PsQuote(ConfigJson.JsString(c, "ClientName") ?? "")} -Confirm:$false",
                        Timeout, PsRunner.DefaultRetries);
                }
            }
            catch
            {
                // best-effort：回滚清空权限失败不阻断恢复备份授权
            }
            // 恢复备份权限
            foreach (SharePermission p in backup)
            {
                if (p.access == ShareAccess.NoAccess) continue;
                string perm = p.access is ShareAccess.Full or ShareAccess.Change ? "rw" : "ro";
                string denyFlag = p.deny ? "Deny" : "Allow";
                try
                {
                    await _ps.RunVoidAsync(
                        $"Grant-NfsSharePermission -Name {PsGate.PsQuote(name)} -ClientName {PsGate.PsQuote(p.account)} -Permission {perm} -Type {denyFlag} -Confirm:$false",
                        Timeout, PsRunner.DefaultRetries);
                }
                catch
                {
                    // TS: .catch(() => {})
                }
            }
            IReadOnlyList<SharePermission> afterRollback = await TryGetPermissionsAsync(name);
            Logger.Info("adapter:nfsAdapter", "[setPermissions:nfs] 回滚后权限状态:", afterRollback.Count, "条 →",
                PermsText(afterRollback));
            throw Errors.CommandFailed($"部分权限授予失败（{string.Join(", ", failed)}），已回滚到原始状态");
        }
        Logger.Info("adapter:nfsAdapter", "[setPermissions:nfs] 权限设置成功:", name);
    }

    // MSFT_NfsSession 仅含 SessionId/NetworkName/State/ClientId，
    // 无用户名、开始时间、打开文件、流量等字段，相应字段留空/置 0
    public async Task<IReadOnlyList<ProtocolSession>> ListSessionsAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync("Get-NfsSession", Timeout, 0);
        return AdapterHelpers.Items(raw).Select(s => new ProtocolSession
        {
            protocol = Protocol.Nfs,
            sessionId = NonEmpty(ConfigJson.JsString(s, "SessionId")) ?? ConfigJson.JsString(s, "ClientId") ?? "",
            // TS: s.ClientId !== undefined ? `Client ${s.ClientId}` : ''
            clientUserName = ConfigJson.Prop(s, "ClientId") is null ? "" : $"Client {ConfigJson.JsString(s, "ClientId")}",
            clientComputerName = NonEmpty(ConfigJson.Str(s, "NetworkName")) ?? "",
            sessionStartTime = "",
            clientOpenFiles = 0,
            clientIdleTime = 0,
            bytesReceived = 0,
            bytesSent = 0,
        }).ToList();
    }

    // NFS 关闭会话：Disconnect-NfsSession（需 SessionId，来自 Get-NfsSession 输出）
    public async Task CloseSessionAsync(string sessionId)
    {
        Logger.Info("adapter:nfsAdapter", "[closeSession:nfs] 关闭会话:", sessionId);
        string cmd = $"Disconnect-NfsSession -SessionId {PsGate.PsQuote(sessionId)} -Confirm:$false";
        Logger.Info("adapter:nfsAdapter", "[closeSession:nfs] PowerShell 命令:", cmd);
        try
        {
            await _ps.RunVoidAsync(cmd, Timeout, PsRunner.DefaultRetries);
            Logger.Info("adapter:nfsAdapter", "[closeSession:nfs] 会话关闭成功:", sessionId);
        }
        catch (Exception e)
        {
            Logger.Error("adapter:nfsAdapter", "[closeSession:nfs] 会话关闭失败:", sessionId, e.Message);
            throw;
        }
    }

    // TS 未实现（capabilities.supportsToggle=false）：契约要求实现体抛 NotSupportedException
    public Task ToggleShareAsync(string name, bool enabled) =>
        throw new NotSupportedException("NFS 共享不支持启用/禁用切换");

    // TS 未实现（capabilities.supportsOpenFiles=false）
    public Task<IReadOnlyList<SmbOpenFile>> ListOpenFilesAsync() =>
        throw new NotSupportedException("NFS 不支持列出打开文件");

    public Task CloseFileAsync(string fileId) =>
        throw new NotSupportedException("NFS 不支持关闭打开文件");

    public Task<ServiceStatus> GetServiceStatusAsync() => _nfs.GetServiceStatusAsync();

    public async Task RestartServiceAsync()
    {
        Logger.Info("adapter:nfsAdapter", "[restartService:nfs] 重启 NFS 服务...");
        try
        {
            await _nfs.RestartServiceAsync();
            Logger.Info("adapter:nfsAdapter", "[restartService:nfs] 服务重启成功");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:nfsAdapter", "[restartService:nfs] 服务重启失败:", e.Message);
            throw;
        }
    }

    public async Task StopServiceAsync()
    {
        Logger.Info("adapter:nfsAdapter", "[stopService:nfs] 停止服务...");
        try
        {
            await _nfs.StopServiceAsync();
            Logger.Info("adapter:nfsAdapter", "[stopService:nfs] 服务已停止");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:nfsAdapter", "[stopService:nfs] 停止失败:", e.Message);
            throw;
        }
    }

    public async Task<object?> GetConfigAsync() => await _nfs.GetConfigAsync();

    public async Task SetConfigAsync(object? config)
    {
        Logger.Info("adapter:nfsAdapter", "[setConfig:nfs] 设置服务器配置");
        if (config is not NfsServerConfig typed) throw Errors.InvalidParam("NFS 配置对象类型不匹配");
        try
        {
            await _nfs.SetConfigAsync(typed);
            Logger.Info("adapter:nfsAdapter", "[setConfig:nfs] 配置设置成功");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:nfsAdapter", "[setConfig:nfs] 配置设置失败:", e.Message);
            throw;
        }
    }

    public object? DefaultConfig() => _nfs.DefaultConfig();

    public async Task<object?> RestoreDefaultAsync() => await _nfs.RestoreDefaultAsync();

    // ===== 内部助手 =====

    // TS: mapNfsShare(r: RawNfsShare)
    private static Share MapNfsShare(JsonElement r)
    {
        string permission = ConfigJson.Str(r, "Permission") ?? "";
        return new Share
        {
            name = ConfigJson.Str(r, "Name") ?? "",
            path = ConfigJson.Str(r, "Path") ?? "",
            description = "",
            protocol = Protocol.Nfs,
            type = ShareType.Disk,
            hidden = false,
            encrypted = false,
            concurrentUsers = 0,
            status = ConfigJson.Truthy(r, "Online") ? ShareStatus.Enabled : ShareStatus.Disabled,
            cached = false,
            networkName = ConfigJson.Str(r, "NetworkName"),
            authentication = MapAuthentication(ConfigJson.Prop(r, "Authentication")),
            // TS: (r.Permission || 'rw') === 'ro' ? 'ro' : 'rw'
            nfsPermission = (permission.Length == 0 ? "rw" : permission) == "ro" ? NfsPermission.Ro : NfsPermission.Rw,
            anonymousUid = ConfigJson.Number(r, "AnonymousUid") is double uid ? (int)uid : null,
            anonymousGid = ConfigJson.Number(r, "AnonymousGid") is double gid ? (int)gid : null,
            enableUnmappedAccess = ConfigJson.Truthy(r, "EnableUnmappedAccess"),
            allowRootAccess = ConfigJson.Truthy(r, "AllowRootAccess"),
        };
    }

    /// <summary>
    /// TS: toArray(v) —— 缺省/空 → []；单值 → [v]；数组原样。
    /// 元素为 'krb5'|'krb5i'|'krb5p'|'sys' 字符串，逐项解析为契约枚举（C# 枚举无法承载任意串，非法值丢弃）。
    /// </summary>
    private static IReadOnlyList<NfsAuthMethod> MapAuthentication(JsonElement? el)
    {
        List<NfsAuthMethod> list = new();
        foreach (JsonElement item in AdapterHelpers.Items(el ?? default))
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            if (Enum.TryParse(item.GetString(), ignoreCase: true, out NfsAuthMethod method)) list.Add(method);
        }
        return list;
    }

    // TS: this.getPermissions(name).catch(() => [])
    private async Task<IReadOnlyList<SharePermission>> TryGetPermissionsAsync(string name)
    {
        try
        {
            return await GetPermissionsAsync(name);
        }
        catch
        {
            return Array.Empty<SharePermission>();
        }
    }

    // 契约枚举 → TS 命令串字面量（TS: 'krb5'|'krb5i'|'krb5p'|'sys'）
    private static string AuthText(NfsAuthMethod m) => m switch
    {
        NfsAuthMethod.Krb5 => "krb5",
        NfsAuthMethod.Krb5i => "krb5i",
        NfsAuthMethod.Krb5p => "krb5p",
        _ => "sys",
    };

    // TS: 未提供（undefined）→ null（调用方跳过该参数）；'ro'|'rw' 原样
    private static string? PermOrNull(NfsPermission? p) => p switch
    {
        null => null,
        NfsPermission.Ro => "ro",
        _ => "rw",
    };

    private static string PermText(NfsPermission? p) => PermOrNull(p) ?? "(未改)";

    private static string AuthenticationText(IReadOnlyList<NfsAuthMethod>? methods) =>
        methods is null ? "(未指定)" : string.Join(",", methods.Select(AuthText));

    // TS: 日志模板 `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`.join(', ') || '(空)'
    private static string PermsText(IReadOnlyList<SharePermission> perms) =>
        perms.Count == 0
            ? "(空)"
            : string.Join(", ", perms.Select(p => $"{p.account}={p.access}{(p.deny ? "(deny)" : "")}"));

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
