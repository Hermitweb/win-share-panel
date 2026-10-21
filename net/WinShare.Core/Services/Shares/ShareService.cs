// 移植自 electron/services/share.ts（SMB 共享域：list/get/create/update/delete/restore/toggle/
// permissions/connections/openFiles/closeFiles/exportConfig/importConfig）。
// 命令串从 TS 原文件 1:1 复制（含参数占位与注释语义）；TS 模板字符串改为 C# 插值 + PsGate/PsArgs 闸门承载。
// 执行通道：Lib/PsRunner（PoolSingleton 常驻池）；注入闸门：Ps/PsArgs（与 powershell.ts 同语义）。
using System.Globalization;
using System.Text;
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Ps;

namespace WinShare.Core.Services.Shares;

/// <summary>
/// <see cref="IShareService"/> 实现（TS: electron/services/share.ts）。
/// PS 通道经 <see cref="IPsInvoker"/> 注入（TS vi.mock('../lib/powershell') 的 C# 等价）。
/// </summary>
public sealed class ShareService : IShareService
{
    // === TS: 枚举白名单（与 types.ts 声明一致），拼入命令前运行时校验，杜绝注入 ===
    private static readonly HashSet<string> CachingModes = new() { "None", "Manual", "Documents", "Programs", "BranchCache" };
    private static readonly HashSet<string> FolderEnumModes = new() { "AccessBased", "Unrestricted" };

    // TS: SHARE_TYPE_MAP —— ShareType 数值 → 契约枚举
    private static readonly Dictionary<int, ShareType> ShareTypeMap = new()
    {
        [0] = ShareType.Disk,
        [1] = ShareType.IPC,
        [2] = ShareType.Printer,
        [3] = ShareType.Special,
    };

    // TS: SYSTEM_SPECIAL_SHARES —— 系统特殊共享（ADMIN$/IPC$/C$ 等）：禁止删除与启停
    // （toggleShare 的禁用分支会真删共享）
    private static readonly HashSet<string> SystemSpecialShares = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin$", "ipc$", "c$", "print$", "fax$", "rdp-tcp$", "rdp-tls$",
    };

    private readonly IPsInvoker _ps;
    private readonly IStateStore? _store;

    public ShareService(IPsInvoker? ps = null, IStateStore? store = null)
    {
        _ps = ps ?? PsRunner.Default;
        _store = store;
    }

    // === TS: mapShare(r: RawShare): Share ===
    private static Share MapShare(JsonElement r)
    {
        return new Share
        {
            name = Str(r, "Name") ?? "",
            path = Str(r, "Path") ?? "",
            description = Str(r, "Description") ?? "",
            protocol = Protocol.Smb,
            // TS: type: r.Special ? 'Special' : SHARE_TYPE_MAP[r.ShareType] || 'Disk'
            type = Bool(r, "Special") ? ShareType.Special
                : ShareTypeMap.TryGetValue(Int(r, "ShareType"), out ShareType st) ? st : ShareType.Disk,
            hidden = Bool(r, "Hidden"),
            concurrentUsers = Int(r, "ConcurrentUsers"),
            status = ShareStatus.Enabled,
            cached = Bool(r, "Cached"),
            encrypted = Bool(r, "Encrypted"),
        };
    }

    // === JSON 字段取值助手（TS 的 r.X || '' / !!r.X 语义；缺失即默认值） ===
    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string prop)
    {
        if (e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out int n))
        {
            return n;
        }
        return 0;
    }

    /// <summary>TS: listShares()——retries:0 避免未装/降级时无谓重试造成切 Tab 延迟。</summary>
    public async Task<IReadOnlyList<Share>> ListSharesAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync("Get-SmbShare", 15000, 0);
        return Enumerate(raw).Select(MapShare).ToList();
    }

    /// <summary>TS: getShare(name)。</summary>
    public async Task<Share> GetShareAsync(string name)
    {
        if (!PsGate.ValidateShareName(name)) throw Errors.InvalidParam("共享名非法");
        JsonElement raw = await _ps.RunJsonAsync($"Get-SmbShare -Name {PsGate.PsQuote(name)}", 15000, 2);
        JsonElement? one = PsGate.FirstOrNull(raw);
        if (one is null || string.IsNullOrEmpty(Str(one.Value, "Name")))
        {
            throw Errors.ShareNotFound(name);
        }
        return MapShare(one.Value);
    }

    /// <summary>
    /// TS: createShare(opts)——命令逐字移植；写操作（本层不真跑，测试以假 IPsInvoker 断言命令串）。
    /// </summary>
    public async Task<Share> CreateShareAsync(CreateShareOpts opts)
    {
        if (!PsGate.ValidateName(opts.name)) throw Errors.InvalidParam("共享名非法");
        if (!PsGate.ValidatePath(opts.path)) throw Errors.InvalidParam("路径非法");
        // A1：访问控制账户逐条账户校验（放行 DOMAIN\user、Everyone 等合法形态，非法即时拒绝）
        foreach (IReadOnlyList<string>? list in new[] { opts.fullAccess, opts.changeAccess, opts.readAccess, opts.noAccess })
        {
            foreach (string acct in list ?? Array.Empty<string>())
            {
                if (!PsGate.ValidateAccountName(acct)) throw Errors.InvalidParam($"账号名非法：{acct}");
            }
        }
        // 校验路径必须存在（New-SmbShare 对不存在路径会创建空文件夹，可能不符预期）
        List<string> parts = new() { "New-SmbShare", $"-Name {PsGate.PsQuote(opts.name)}", $"-Path {PsGate.PsQuote(opts.path)}" };
        if (!string.IsNullOrEmpty(opts.description)) parts.Add($"-Description {PsGate.PsQuote(opts.description)}");
        if (opts.encrypted == true || opts.encryptData == true) parts.Add("-EncryptData $true");
        if (opts.fullAccess?.Count > 0) parts.Add($"-FullAccess {JoinQuoted(opts.fullAccess)}");
        if (opts.changeAccess?.Count > 0) parts.Add($"-ChangeAccess {JoinQuoted(opts.changeAccess)}");
        if (opts.readAccess?.Count > 0) parts.Add($"-ReadAccess {JoinQuoted(opts.readAccess)}");
        if (opts.noAccess?.Count > 0) parts.Add($"-NoAccess {JoinQuoted(opts.noAccess)}");
        // 高级选项（运行时类型校验，防止 IPC 传入非法值注入）
        if (opts.concurrentUserLimit is not null)
        {
            string? n = PsGate.PsNumber(opts.concurrentUserLimit);
            if (n is not null && opts.concurrentUserLimit is int lim && lim > 0) parts.Add($"-ConcurrentUserLimit {n}");
        }
        string? cm = PsGate.PsEnum(EnumName(opts.cachingMode), CachingModes);
        if (cm is not null) parts.Add($"-CachingMode {cm}");
        string? fem = PsGate.PsEnum(EnumName(opts.folderEnumerationMode), FolderEnumModes);
        if (fem is not null) parts.Add($"-FolderEnumerationMode {fem}");
        if (opts.shareShadowCopy == true)
        {
            parts.Add("-ShareShadowCopy");
        }
        string cmd = string.Join(' ', parts);
        Logger.Info("share", "[createShare:smb] 输入参数:", opts);
        Logger.Info("share", "[createShare:smb] PowerShell 命令:", cmd);
        try
        {
            await _ps.RunVoidAsync(cmd, 15000, 2);
            Logger.Info("share", "[createShare:smb] New-SmbShare 执行成功，正在读取共享信息...");
            JsonElement raw = await _ps.RunJsonAsync($"Get-SmbShare -Name {PsGate.PsQuote(opts.name)}", 15000, 2);
            // B-2：回读判空守卫——写命令错误被吞时此处拿到空结果仍"成功"返回垃圾对象；
            // 回读不到即判失败，走孤儿清理路径重抛。
            JsonElement? one = PsGate.FirstOrNull(raw);
            if (one is null || string.IsNullOrEmpty(Str(one.Value, "Name")))
            {
                throw Errors.CommandFailed(
                    "共享创建已执行但未能回读到该共享，请检查执行权限（需管理员）或系统服务状态");
            }
            Logger.Info("share", "[createShare:smb] 共享创建完成:", opts.name);
            AddJournal(JournalAction.Create, opts.name, $"路径 {opts.path}", undoable: false);
            return MapShare(one.Value);
        }
        catch (Exception e)
        {
            Logger.Error("share", "[createShare:smb] 创建失败:", opts.name, e.Message);
            // 清理可能的孤儿共享（New-SmbShare 成功但 Get-SmbShare 失败的情况）
            Logger.Info("share", "[createShare:smb] 尝试清理可能的孤儿共享...");
            try
            {
                await _ps.RunVoidAsync(
                    $"try {{ Remove-SmbShare -Name {PsGate.PsQuote(opts.name)} -Force -ErrorAction Stop }} catch {{}}", 15000, 0);
            }
            catch
            {
                // TS 清理调用不带 catch（异常经 next 的 finally 语义上抛）——但清理失败不应掩盖
                // 原始错误，故此处吞掉清理异常、原样重抛原始失败（与 TS rethrow e 等价）。
            }
            // 验证清理结果（单值期望查询：空数组是 truthy，必须归一化，否则误报"清理失败"）
            bool stillExists;
            try
            {
                JsonElement found = await _ps.RunJsonAsync(
                    $"try {{ Get-SmbShare -Name {PsGate.PsQuote(opts.name)} -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Name }} catch {{}}", 15000, 0);
                JsonElement? one = PsGate.FirstOrNull(found);
                stillExists = one is not null;
            }
            catch
            {
                stillExists = false; // TS: .catch(() => null) → found=null → stillExists=false
            }
            if (stillExists)
            {
                Logger.Error("share", "[createShare:smb] 孤儿共享清理失败！共享仍存在:", opts.name);
            }
            else
            {
                Logger.Info("share", "[createShare:smb] 孤儿共享已确认清理:", opts.name);
            }
            throw;
        }
    }

    /// <summary>TS: updateShare(name, opts)。</summary>
    public async Task<Share> UpdateShareAsync(string name, UpdateShareOpts opts)
    {
        if (!PsGate.ValidateShareName(name)) throw Errors.InvalidParam("共享名非法");
        List<string> parts = new() { "Set-SmbShare", $"-Name {PsGate.PsQuote(name)}" };
        if (opts.description is not null) parts.Add($"-Description {PsGate.PsQuote(opts.description)}");
        if (opts.concurrentUserLimit is not null)
        {
            string? n = PsGate.PsNumber(opts.concurrentUserLimit);
            if (n is not null) parts.Add($"-ConcurrentUserLimit {n}");
        }
        string? cm = PsGate.PsEnum(EnumName(opts.cachingMode), CachingModes);
        if (cm is not null) parts.Add($"-CachingMode {cm}");
        string? fem = PsGate.PsEnum(EnumName(opts.folderEnumerationMode), FolderEnumModes);
        if (fem is not null) parts.Add($"-FolderEnumerationMode {fem}");
        string? enc = PsGate.PsBool(opts.encryptData);
        if (enc is not null) parts.Add($"-EncryptData {enc}");
        // 协议专门适配：隐藏共享可改（Set-SmbShare -Hidden；运行时布尔校验防注入）
        string? hid = PsGate.PsBool(opts.hidden);
        if (hid is not null) parts.Add($"-Hidden {hid}");
        if (opts.cached is not null)
        {
            // Cached 在 SMB 中通过 CachingMode 控制（Cached=$true 等价于 CachingMode=Manual）
            if (opts.cached == true)
            {
                if (cm is null) parts.Add("-CachingMode Manual");
            }
            else if (cm is null)
            {
                parts.Add("-CachingMode None");
            }
        }
        parts.Add("-Force");
        await _ps.RunVoidAsync(string.Join(' ', parts), 15000, 2);
        JsonElement raw = await _ps.RunJsonAsync($"Get-SmbShare -Name {PsGate.PsQuote(name)}", 15000, 2);
        return MapShare(PsGate.FirstOrNull(raw) ?? raw);
    }

    /// <summary>TS: deleteShare(name)。</summary>
    public async Task DeleteShareAsync(string name)
    {
        if (!PsGate.ValidateShareName(name)) throw Errors.InvalidParam("共享名非法");
        // 系统特殊共享（ADMIN$, IPC$, C$ 等）禁止删除
        if (SystemSpecialShares.Contains(name))
        {
            throw Errors.InvalidParam($"系统特殊共享 {name} 不允许删除");
        }
        // 操作日志（撤销底座）：删除前尽力捕获快照（失败不阻塞删除本身）
        ShareSnapshot? snapshot = null;
        try
        {
            IReadOnlyList<Share> shares = await ListSharesAsync();
            Share? s = shares.FirstOrDefault(x => x.name == name);
            if (s is not null)
            {
                IReadOnlyList<SharePermission> perms = await GetSharePermissionsAsync(name);
                ShareAdvanced advanced = await GetShareAdvancedAsync(name);
                snapshot = new ShareSnapshot
                {
                    name = name,
                    path = s.path,
                    description = s.description,
                    permissions = perms,
                    encrypted = s.encrypted,
                    concurrentUserLimit = advanced.concurrentUserLimit,
                    cachingMode = advanced.cachingMode,
                    folderEnumerationMode = advanced.folderEnumerationMode,
                };
            }
        }
        catch
        {
            snapshot = null;
        }
        await _ps.RunVoidAsync($"Remove-SmbShare -Name {PsGate.PsQuote(name)} -Force", 15000, 2);
        AddJournal(
            JournalAction.Delete,
            name,
            snapshot is not null
                ? $"路径 {snapshot.path}，{snapshot.permissions.Count} 条授权已存档"
                : "快照捕获失败",
            undoable: snapshot is not null,
            snapshot: snapshot);
    }

    /// <summary>TS: restoreShare(r: DisabledRecord)——入参形状=ShareSnapshot（toggle 恢复与 journal 撤销共用）。</summary>
    public async Task RestoreShareAsync(ShareSnapshot snapshot)
    {
        IReadOnlyList<SharePermission> allow = snapshot.permissions.Where(p => !p.deny).ToList();
        // M-6：恢复路径失败给出场景化提示（区别于新建失败），保留底层原因摘要
        await CreateShareAsync(new CreateShareOpts
        {
            name = snapshot.name,
            path = snapshot.path,
            description = snapshot.description,
            fullAccess = allow.Where(p => p.access == ShareAccess.Full).Select(p => p.account).ToList(),
            changeAccess = allow.Where(p => p.access == ShareAccess.Change).Select(p => p.account).ToList(),
            readAccess = allow.Where(p => p.access == ShareAccess.Read).Select(p => p.account).ToList(),
            encrypted = snapshot.encrypted,
            concurrentUserLimit = snapshot.concurrentUserLimit,
            cachingMode = snapshot.cachingMode,
            folderEnumerationMode = snapshot.folderEnumerationMode,
        });
        // 补回 deny 条目（New-SmbShare 仅支持 allow 列表，deny 需 Block-SmbShareAccess）
        foreach (SharePermission p in snapshot.permissions.Where(p => p.deny && !string.IsNullOrEmpty(p.account)))
        {
            try
            {
                await _ps.RunVoidAsync(
                    $"Block-SmbShareAccess -Name {PsGate.PsQuote(snapshot.name)} -AccountName {PsGate.PsQuote(p.account)} -Force", 15000, 0);
            }
            catch
            {
                // best-effort：单个 deny 失败不阻断恢复
            }
        }
    }

    /// <summary>TS: toggleShare(name, enabled)。</summary>
    public async Task ToggleShareAsync(string name, bool enabled)
    {
        if (!PsGate.ValidateShareName(name)) throw Errors.InvalidParam("共享名非法");
        // B-4：禁用分支会 Remove-SmbShare——系统特殊共享（IPC$ 等）必须同样禁止启停，
        // 否则放宽尾 $ 校验后存在被真实删除的风险（此前仅被过严的 validateName"意外"挡住）
        if (SystemSpecialShares.Contains(name))
        {
            throw Errors.InvalidParam($"系统特殊共享 {name} 不允许启停");
        }
        if (!enabled)
        {
            IReadOnlyList<Share> shares = await ListSharesAsync();
            Share? s = shares.FirstOrDefault(x => x.name == name);
            if (s is null) throw Errors.ShareNotFound(name);
            IReadOnlyList<SharePermission> perms = await GetSharePermissionsAsync(name);
            // 捕获高级选项，以便恢复时还原（避免回退为默认值）
            ShareAdvanced advanced = await GetShareAdvancedAsync(name);
            // 先删除共享，成功后再落盘禁用记录，避免 Remove 失败时残留脏记录
            await _ps.RunVoidAsync($"Remove-SmbShare -Name {PsGate.PsQuote(name)} -Force", 15000, 2);
            List<ShareSnapshot> list = ReadDisabled();
            ShareSnapshot rec = new()
            {
                name = name,
                path = s.path,
                description = s.description,
                permissions = perms,
                encrypted = s.encrypted,
                concurrentUserLimit = advanced.concurrentUserLimit,
                cachingMode = advanced.cachingMode,
                folderEnumerationMode = advanced.folderEnumerationMode,
            };
            if (!list.Any(x => x.name == name))
            {
                list.Add(rec);
                WriteDisabled(list);
            }
            AddJournal(
                JournalAction.ToggleOff,
                name,
                $"禁用（{perms.Count} 条授权已存档，可恢复）",
                undoable: true,
                snapshot: rec);
        }
        else
        {
            List<ShareSnapshot> list = ReadDisabled();
            int idx = list.FindIndex(x => x.name == name);
            if (idx < 0) throw Errors.CommandFailed("未找到该共享的禁用记录，无法恢复");
            ShareSnapshot r = list[idx];
            // 复用 restoreShare（journal 撤销同路径）；M-6 场景化提示在包装层保留
            try
            {
                await RestoreShareAsync(r);
            }
            catch (Exception e)
            {
                string msg = e.Message.Length > 200 ? e.Message[..200] : e.Message;
                throw Errors.CommandFailed($"恢复共享\"{name}\"失败：{msg}");
            }
            list.RemoveAt(idx);
            WriteDisabled(list);
        }
    }

    /// <summary>
    /// TS: getShareAdvanced(name)（内部辅助，非导出）——读取高级选项用于禁用/恢复时保留配置。
    /// </summary>
    private async Task<ShareAdvanced> GetShareAdvancedAsync(string name)
    {
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(
                $"Get-SmbShare -Name {PsGate.PsQuote(name)} | Select-Object ConcurrentUserLimit, CachingMode, FolderEnumerationMode", 15000, 0);
            JsonElement? one = PsGate.FirstOrNull(raw);
            if (one is null) return new ShareAdvanced();
            JsonElement e = one.Value;
            int? cul = null;
            if (e.TryGetProperty("ConcurrentUserLimit", out JsonElement culEl) && culEl.ValueKind == JsonValueKind.Number
                && culEl.TryGetInt32(out int culVal) && culVal > 0)
            {
                cul = culVal;
            }
            // 经白名单校验，确保仅合法枚举值回填
            string? cm = PsGate.PsEnum(Str(e, "CachingMode"), CachingModes);
            string? fem = PsGate.PsEnum(Str(e, "FolderEnumerationMode"), FolderEnumModes);
            return new ShareAdvanced
            {
                concurrentUserLimit = cul,
                cachingMode = cm is null ? null : ParseCachingMode(cm),
                folderEnumerationMode = fem is null ? null : ParseFolderEnumMode(fem),
            };
        }
        catch
        {
            return new ShareAdvanced();
        }
    }

    private sealed record ShareAdvanced
    {
        public int? concurrentUserLimit { get; init; }
        public CachingMode? cachingMode { get; init; }
        public FolderEnumerationMode? folderEnumerationMode { get; init; }
    }

    /// <summary>TS: getSharePermissions(name)。</summary>
    public async Task<IReadOnlyList<SharePermission>> GetSharePermissionsAsync(string name)
    {
        if (!PsGate.ValidateShareName(name)) throw Errors.InvalidParam("共享名非法");
        JsonElement r = await _ps.RunJsonAsync($"Get-SmbShareAccess -Name {PsGate.PsQuote(name)}", 15000, 2);
        return Enumerate(r).Select(x => new SharePermission
        {
            shareName = name,
            account = Str(x, "AccountName") ?? "",
            // TS: x.AccountType === 1 ? 'User' : 'Group'
            accountType = Int(x, "AccountType") == 1 ? AccountType.User : AccountType.Group,
            // TS: x.AccessRight === 0 ? 'Full' : x.AccessRight === 1 ? 'Change' : 'Read'
            access = Int(x, "AccessRight") switch
            {
                0 => ShareAccess.Full,
                1 => ShareAccess.Change,
                _ => ShareAccess.Read,
            },
            deny = Int(x, "AccessControlType") == 1,
        }).ToList();
    }

    /// <summary>TS: getShareConnections(name)——共享详情面板连接信息。</summary>
    public async Task<ShareConnections> GetShareConnectionsAsync(string name)
    {
        if (!PsGate.ValidateShareName(name)) throw Errors.InvalidParam("共享名非法");
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(
                $"Get-SmbConnection | Where-Object {{ $_.ShareName -eq {PsGate.PsQuote(name)} }} | Select-Object ClientUserName, ClientComputerName", 15000, 2);
            List<ClientConnection> clientConnections = Enumerate(raw).Select(c => new ClientConnection
            {
                clientUserName = Str(c, "ClientUserName") ?? "",
                clientComputerName = Str(c, "ClientComputerName") ?? "",
                openFiles = 0,
            }).ToList();
            return new ShareConnections { concurrentUsers = clientConnections.Count, clientConnections = clientConnections };
        }
        catch
        {
            return new ShareConnections { concurrentUsers = 0, clientConnections = Array.Empty<ClientConnection>() };
        }
    }

    /// <summary>TS: getShareOpenFiles(name)。</summary>
    public async Task<IReadOnlyList<ShareOpenFile>> GetShareOpenFilesAsync(string name)
    {
        if (!PsGate.ValidateShareName(name)) throw Errors.InvalidParam("共享名非法");
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(
                $"Get-SmbOpenFile | Where-Object {{ $_.Path -like {PsGate.PsQuote($"*{name}*")} }} | Select-Object FileId, Path, ClientUserName, ClientComputerName, LockCount", 15000, 2);
            return Enumerate(raw).Select(f => new ShareOpenFile
            {
                fileId = NumLong(f, "FileId"),
                path = Str(f, "Path") ?? "",
                clientUserName = Str(f, "ClientUserName") ?? "",
                clientComputerName = Str(f, "ClientComputerName") ?? "",
                lockCount = Int(f, "LockCount"),
            }).ToList();
        }
        catch
        {
            return Array.Empty<ShareOpenFile>();
        }
    }

    /// <summary>TS: closeShareOpenFiles(name)。</summary>
    public async Task<CloseOpenFilesResult> CloseShareOpenFilesAsync(string name)
    {
        if (!PsGate.ValidateShareName(name)) throw Errors.InvalidParam("共享名非法");
        try
        {
            IReadOnlyList<ShareOpenFile> files = await GetShareOpenFilesAsync(name);
            int closed = 0;
            int failed = 0;
            foreach (ShareOpenFile f in files)
            {
                try
                {
                    await _ps.RunVoidAsync($"Close-SmbOpenFile -FileId {f.fileId.ToString(CultureInfo.InvariantCulture)} -Force", 15000, 0);
                    closed++;
                }
                catch
                {
                    failed++;
                }
            }
            return new CloseOpenFilesResult { closed = closed, failed = failed };
        }
        catch (Exception e)
        {
            string msg = e.Message.Length > 200 ? e.Message[..200] : e.Message;
            throw Errors.CommandFailed($"关闭共享打开文件失败：{msg}");
        }
    }

    /// <summary>TS: exportConfig()——导出全部共享配置 JSON（跳过 Special/IPC）。</summary>
    public async Task<string> ExportConfigAsync()
    {
        IReadOnlyList<Share> shares = await ListSharesAsync();
        List<Dictionary<string, object?>> configs = new();
        foreach (Share s in shares)
        {
            if (s.type == ShareType.Special || s.type == ShareType.IPC) continue;
            IReadOnlyList<SharePermission> perms = await GetSharePermissionsAsync(s.name);
            configs.Add(ShareToDict(s, perms));
        }
        // TS: JSON.stringify({ exportedAt, shares }, null, 2)
        return JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["exportedAt"] = NowIso(), ["shares"] = configs },
            JsonOpts.Indented);
    }

    /// <summary>TS: importConfig(json)——R-1 导入护栏 + 逐条 createShare。</summary>
    public async Task<ImportReport> ImportConfigAsync(string json)
    {
        // R-1 导入护栏：超大内容先行拒绝、非法 JSON 友好报错。
        if (json is null || json.Length > 2 * 1024 * 1024)
        {
            throw Errors.InvalidParam("导入内容不合法或超过 2MB 上限");
        }
        JsonElement data;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            data = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Errors.InvalidParam("导入文件格式错误：JSON 解析失败");
        }
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("shares", out JsonElement sharesEl)
            || sharesEl.ValueKind != JsonValueKind.Array)
        {
            throw Errors.InvalidParam("导入文件格式错误");
        }
        if (sharesEl.GetArrayLength() > 500)
        {
            throw Errors.InvalidParam("导入条目超过 500 上限，请拆分后分批导入");
        }
        int imported = 0;
        int skipped = 0;
        List<string> errors = new();
        foreach (JsonElement s in sharesEl.EnumerateArray())
        {
            string sName = Str(s, "name") ?? "";
            string sPath = Str(s, "path") ?? "";
            // 安全校验：name 与 path 均需合法，防止导入恶意配置注入非法共享名/路径
            if (!PsGate.ValidateName(sName) || !PsGate.ValidatePath(sPath))
            {
                skipped++;
                errors.Add($"跳过非法条目：{(sName.Length > 0 ? sName : "(空名)")}");
                continue;
            }
            try
            {
                List<SharePermission> perms = ParsePermissions(s.TryGetProperty("permissions", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.Array
                    ? pEl : null);
                List<string> full = perms.Where(p => p.access == ShareAccess.Full && !p.deny).Select(p => p.account).ToList();
                List<string> change = perms.Where(p => p.access == ShareAccess.Change && !p.deny).Select(p => p.account).ToList();
                List<string> read = perms.Where(p => p.access == ShareAccess.Read && !p.deny).Select(p => p.account).ToList();
                await CreateShareAsync(new CreateShareOpts
                {
                    name = sName,
                    path = sPath,
                    description = Str(s, "description"),
                    fullAccess = full,
                    changeAccess = change,
                    readAccess = read,
                    encrypted = Bool(s, "encrypted"),
                });
                imported++;
            }
            catch (Exception e)
            {
                skipped++;
                errors.Add($"{sName}: {e.Message}");
            }
        }
        return new ImportReport { imported = imported, skipped = skipped, errors = errors };
    }

    // ===== TS: share.ts 本地持久（disabled.json 读写） =====

    private static readonly JsonSerializerOptions DisabledOpts = JsonCompat.Indented;

    private static List<ShareSnapshot> ReadDisabled()
    {
        try
        {
            string file = ShareData.DisabledFile();
            if (!File.Exists(file)) return new List<ShareSnapshot>();
            return JsonSerializer.Deserialize<List<ShareSnapshot>>(File.ReadAllText(file)) ?? new List<ShareSnapshot>();
        }
        catch
        {
            return new List<ShareSnapshot>();
        }
    }

    private static void WriteDisabled(List<ShareSnapshot> list)
    {
        string dir = ShareData.DataDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(ShareData.DisabledFile(), JsonSerializer.Serialize(list, DisabledOpts));
    }

    // ===== 内部助手 =====

    /// <summary>TS: addJournal({...})——注入 IStateStore 时记账（未注入则跳过，保持服务可独立测试）。</summary>
    private void AddJournal(JournalAction action, string name, string detail, bool undoable, object? snapshot = null)
    {
        _store?.AddJournal(new JournalEntryDraft
        {
            action = action,
            protocol = Protocol.Smb,
            name = name,
            detail = detail,
            undoable = undoable,
            snapshot = snapshot,
        });
    }

    /// <summary>TS: JSON.parse 返回数组或单值——runPowerShell 语义统一归一化为枚举。</summary>
    private static IEnumerable<JsonElement> Enumerate(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in el.EnumerateArray()) yield return item;
        }
        else
        {
            yield return el;
        }
    }

    /// <summary>TS: opts.fullAccess.map(psQuote).join(',')。</summary>
    private static string JoinQuoted(IReadOnlyList<string> values) =>
        string.Join(',', values.Select(PsGate.PsQuote));

    /// <summary>TS: Number(f.FileId) || 0 —— 数值型字段容错（字符串数字也接受）。</summary>
    private static long NumLong(JsonElement e, string prop)
    {
        if (e.TryGetProperty(prop, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long sn)) return sn;
        }
        return 0;
    }

    /// <summary>契约枚举 → PS 裸值名（与 TS 字符串字面量同形）。</summary>
    internal static string? EnumName<T>(T? value) where T : struct, Enum =>
        value.HasValue ? value.Value.ToString() : null;

    private static CachingMode? ParseCachingMode(string s) =>
        Enum.TryParse(s, ignoreCase: true, out CachingMode m) ? m : null;

    private static FolderEnumerationMode? ParseFolderEnumMode(string s) =>
        Enum.TryParse(s, ignoreCase: true, out FolderEnumerationMode m) ? m : null;

    private static string NowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>TS: exportConfig 里 configs.push({ ...s, permissions: perms })——DTO 字段原样带出。</summary>
    private static Dictionary<string, object?> ShareToDict(Share s, IReadOnlyList<SharePermission> perms) => new()
    {
        ["name"] = s.name,
        ["path"] = s.path,
        ["description"] = s.description,
        ["protocol"] = s.protocol.ToString(),
        ["type"] = s.type.ToString(),
        ["hidden"] = s.hidden,
        ["concurrentUsers"] = s.concurrentUsers,
        ["status"] = s.status.ToString(),
        ["cached"] = s.cached,
        ["encrypted"] = s.encrypted,
        ["permissions"] = perms.Select(PermissionToDict).ToList(),
    };

    private static Dictionary<string, object?> PermissionToDict(SharePermission p) => new()
    {
        ["shareName"] = p.shareName,
        ["account"] = p.account,
        ["accountType"] = p.accountType.ToString(),
        ["access"] = p.access.ToString(),
        ["deny"] = p.deny,
    };

    private static List<SharePermission> ParsePermissions(JsonElement? el)
    {
        if (el is null) return new List<SharePermission>();
        List<SharePermission> list = new();
        foreach (JsonElement p in el.Value.EnumerateArray())
        {
            list.Add(new SharePermission
            {
                shareName = Str(p, "shareName") ?? "",
                account = Str(p, "account") ?? "",
                accountType = Str(p, "accountType") == "User" ? AccountType.User : AccountType.Group,
                access = Str(p, "access") switch
                {
                    "Full" => ShareAccess.Full,
                    "Change" => ShareAccess.Change,
                    "Read" => ShareAccess.Read,
                    _ => ShareAccess.Read,
                },
                deny = Bool(p, "deny"),
            });
        }
        return list;
    }
}

/// <summary>JSON 序列化选项（TS JSON.stringify(..., null, 2) 的 2 空格缩进对齐）。</summary>
internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Indented = JsonCompat.Indented;
}
