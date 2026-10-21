// 移植自 electron/services/preset.ts（权限预设管理：resolveAccount / listPresets / getPreset /
// savePreset / updatePreset / deletePreset / duplicatePreset / applyPreset / exportPresets / importPresets）。
// 命令串从 TS 原文件 1:1 复制；ALLOWED_ACCESS 白名单、内置模板保护、导入护栏（2MB/500 条）全部保留语义。
// applyPreset 复用 user.setSharePermissions 的事务补偿逻辑——C# 侧经 <see cref="IUserService"/> 注入
// （TS 直接 import './user'；契约方法为 SetSharePermissionsAsync）。
using System.Globalization;
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Ps;

namespace WinShare.Core.Services.Shares;

/// <summary><see cref="IPresetService"/> 实现（TS: electron/services/preset.ts）。</summary>
public sealed class PresetService : IPresetService
{
    /// <summary>TS: ALLOWED_ACCESS —— access 白名单：防止恶意 preset 注入 PowerShell 命令。</summary>
    private static readonly HashSet<string> AllowedAccess = new() { "Full", "Change", "Read" };

    /// <summary>TS: importPresets 上限 —— 2MB / 500 条。</summary>
    private const int MaxJsonBytes = 2 * 1024 * 1024;
    private const int MaxImportItems = 500;

    /// <summary>TS: BUILTIN —— 内置模板定义（作为恢复默认的基准）。</summary>
    private static readonly List<PermissionPreset> Builtin = new()
    {
        new PermissionPreset
        {
            id = "builtin-readonly",
            name = "只读团队",
            description = "所有人只读",
            builtIn = true,
            category = "基础",
            entries = new[] { new PresetEntry { account = "{Everyone}", accountType = AccountType.Group, access = ShareAccess.Read, deny = false } },
        },
        new PermissionPreset
        {
            id = "builtin-rw",
            name = "读写协作",
            description = "当前用户可改写，所有人只读",
            builtIn = true,
            category = "基础",
            entries = new[]
            {
                new PresetEntry { account = "{CurrentUser}", accountType = AccountType.User, access = ShareAccess.Change, deny = false },
                new PresetEntry { account = "{Everyone}", accountType = AccountType.Group, access = ShareAccess.Read, deny = false },
            },
        },
        new PermissionPreset
        {
            id = "builtin-admin",
            name = "管理员全权",
            description = "管理员与当前用户完全控制",
            builtIn = true,
            category = "安全",
            entries = new[]
            {
                new PresetEntry { account = "{Administrators}", accountType = AccountType.Group, access = ShareAccess.Full, deny = false },
                new PresetEntry { account = "{CurrentUser}", accountType = AccountType.User, access = ShareAccess.Full, deny = false },
            },
        },
        new PermissionPreset
        {
            id = "builtin-private",
            name = "私有独占",
            description = "仅当前用户完全控制，其他人显式拒绝",
            builtIn = true,
            category = "安全",
            entries = new[]
            {
                new PresetEntry { account = "{CurrentUser}", accountType = AccountType.User, access = ShareAccess.Full, deny = false },
                new PresetEntry { account = "{Everyone}", accountType = AccountType.Group, access = ShareAccess.Read, deny = true },
            },
        },
        new PermissionPreset
        {
            id = "builtin-exchange",
            name = "交换区",
            description = "所有人可改写，管理员完全控制",
            builtIn = true,
            category = "协作",
            entries = new[]
            {
                new PresetEntry { account = "{Administrators}", accountType = AccountType.Group, access = ShareAccess.Full, deny = false },
                new PresetEntry { account = "{Everyone}", accountType = AccountType.Group, access = ShareAccess.Change, deny = false },
            },
        },
    };

    private readonly IUserService? _users;

    public PresetService(IUserService? users = null) => _users = users;

    // === TS: resolveAccount(account)——内置名 → 本机全限定账号（同步） ===

    /// <summary>TS: resolveAccount(account)。</summary>
    public string ResolveAccount(string account) => account switch
    {
        "{Everyone}" => "Everyone",
        "{Administrators}" => "Administrators",
        // TS: process.env.USERNAME || process.env.USER || ''
        "{CurrentUser}" => Environment.GetEnvironmentVariable("USERNAME")
            ?? Environment.GetEnvironmentVariable("USER")
            ?? "",
        _ => account,
    };

    /// <summary>TS: listPresets()——内置 + 自定义。</summary>
    public Task<IReadOnlyList<PermissionPreset>> ListPresetsAsync()
    {
        List<PermissionPreset> all = new(Builtin);
        all.AddRange(ReadCustom());
        return Task.FromResult<IReadOnlyList<PermissionPreset>>(all);
    }

    /// <summary>TS: getPreset(id)。</summary>
    public async Task<PermissionPreset?> GetPresetAsync(string id)
    {
        IReadOnlyList<PermissionPreset> all = await ListPresetsAsync();
        return all.FirstOrDefault(p => p.id == id);
    }

    /// <summary>TS: savePreset(preset)。</summary>
    public async Task SavePresetAsync(PermissionPreset preset)
    {
        if (!PsGate.ValidateName(preset.name)) throw Errors.InvalidParam("模板名非法");
        if (preset.name.Length > 40) throw Errors.InvalidParam("模板名长度不能超过 40 字符");
        // 内置模板 ID 不允许被自定义覆盖
        if (Builtin.Any(p => p.id == preset.id))
        {
            throw Errors.BuiltinProtected();
        }
        ValidateEntries(preset.entries ?? Array.Empty<PresetEntry>());

        List<PermissionPreset> list = ReadCustom();
        string now = NowIso();
        int idx = list.FindIndex(p => p.id == preset.id);
        PermissionPreset item = preset with
        {
            builtIn = false,
            entries = preset.entries ?? Array.Empty<PresetEntry>(),
            category = string.IsNullOrEmpty(preset.category) ? "自定义" : preset.category,
            updatedAt = now,
            createdAt = idx >= 0 ? (list[idx].createdAt ?? now) : now,
        };
        if (idx >= 0) list[idx] = item;
        else list.Add(item);
        WriteCustom(list);
        await Task.CompletedTask;
    }

    /// <summary>TS: updatePreset(id, updates: Partial&lt;PermissionPreset&gt;)。</summary>
    public async Task UpdatePresetAsync(string id, PermissionPreset updates)
    {
        if (Builtin.Any(p => p.id == id))
        {
            throw Errors.BuiltinProtected();
        }
        List<PermissionPreset> list = ReadCustom();
        int idx = list.FindIndex(p => p.id == id);
        if (idx < 0) throw Errors.PresetNotFound(id);

        PermissionPreset merged = Merge(list[idx], updates) with
        {
            id = id, // 不允许改 ID
            builtIn = false,
            updatedAt = NowIso(),
        };
        if (merged.name is not null && !PsGate.ValidateName(merged.name))
        {
            throw Errors.InvalidParam("模板名非法");
        }
        if (merged.entries is not null) ValidateEntries(merged.entries);
        list[idx] = merged;
        WriteCustom(list);
        await Task.CompletedTask;
    }

    /// <summary>TS: { ...list[idx], ...updates }——浅合并（null 字段表示未提供，不覆盖）。</summary>
    private static PermissionPreset Merge(PermissionPreset current, PermissionPreset updates) => new()
    {
        id = current.id,
        name = updates.name ?? current.name,
        description = updates.description ?? current.description,
        builtIn = current.builtIn,
        entries = updates.entries ?? current.entries,
        category = updates.category ?? current.category,
        createdAt = current.createdAt,
        updatedAt = current.updatedAt,
    };

    /// <summary>TS: deletePreset(id)。</summary>
    public async Task DeletePresetAsync(string id)
    {
        if (Builtin.Any(p => p.id == id)) throw Errors.BuiltinProtected();
        List<PermissionPreset> list = ReadCustom();
        int idx = list.FindIndex(p => p.id == id);
        if (idx < 0) throw Errors.PresetNotFound(id);
        list.RemoveAt(idx);
        WriteCustom(list);
        await Task.CompletedTask;
    }

    /// <summary>TS: duplicatePreset(id, newName?)——复制内置模板为自定义模板（便于基于内置模板二次编辑）。</summary>
    public async Task<PermissionPreset> DuplicatePresetAsync(string id, string? newName = null)
    {
        IReadOnlyList<PermissionPreset> all = await ListPresetsAsync();
        PermissionPreset? src = all.FirstOrDefault(p => p.id == id);
        if (src is null) throw Errors.PresetNotFound(id);
        string name = string.IsNullOrEmpty(newName) ? $"{src.name} 副本" : newName!;
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("模板名非法");
        PermissionPreset newPreset = new()
        {
            id = $"custom-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            name = name,
            description = src.description,
            builtIn = false,
            category = "自定义",
            entries = src.entries.Select(e => e with { }).ToList(),
            createdAt = NowIso(),
            updatedAt = NowIso(),
        };
        List<PermissionPreset> list = ReadCustom();
        list.Add(newPreset);
        WriteCustom(list);
        return newPreset;
    }

    /// <summary>
    /// TS: applyPreset(shareName, presetId, mode)——复用 user.setSharePermissions 的事务补偿逻辑
    /// （含备份+回滚）。C# 侧经 <see cref="IUserService"/> 注入；未注入时抛错（写域不静默降级）。
    /// </summary>
    public async Task ApplyPresetAsync(string shareName, string presetId, ApplyPresetMode mode)
    {
        if (!PsGate.ValidateName(shareName)) throw Errors.InvalidParam("共享名非法");
        IReadOnlyList<PermissionPreset> all = await ListPresetsAsync();
        PermissionPreset? preset = all.FirstOrDefault(p => p.id == presetId);
        if (preset is null) throw Errors.PresetNotFound(presetId);

        // 安全校验：access 必须在白名单内，防止恶意 preset 注入 PowerShell 命令
        foreach (PresetEntry e in preset.entries)
        {
            if (!AllowedAccess.Contains(e.access.ToString()))
            {
                throw Errors.InvalidParam($"预设条目 access 非法：{e.access}");
            }
        }

        // 构造 SharePermission[]：merge 模式需先读取现有权限再合并
        List<SharePermission> targetPerms;
        if (mode == ApplyPresetMode.Merge)
        {
            // merge：保留现有权限，仅追加 preset 中的条目（同账号后者覆盖前者）
            IReadOnlyList<SharePermission> existing;
            try
            {
                existing = _users is null
                    ? Array.Empty<SharePermission>()
                    : await _users.GetSharePermissionsAsync(shareName);
            }
            catch
            {
                // TS: getSharePermissions(shareName).catch(() => [] as SharePermission[])
                existing = Array.Empty<SharePermission>();
            }
            Dictionary<string, SharePermission> map = new();
            foreach (SharePermission p in existing) map[p.account] = p;
            foreach (PresetEntry e in preset.entries)
            {
                string acct = ResolveAccount(e.account);
                if (string.IsNullOrEmpty(acct)) continue;
                map[acct] = new SharePermission
                {
                    shareName = shareName,
                    account = acct,
                    accountType = e.accountType,
                    access = e.access,
                    deny = e.deny ?? false,
                };
            }
            targetPerms = map.Values.ToList();
        }
        else
        {
            // overwrite：仅应用 preset 条目
            targetPerms = new List<SharePermission>();
            foreach (PresetEntry e in preset.entries)
            {
                string acct = ResolveAccount(e.account);
                if (string.IsNullOrEmpty(acct)) continue;
                targetPerms.Add(new SharePermission
                {
                    shareName = shareName,
                    account = acct,
                    accountType = e.accountType,
                    access = e.access,
                    deny = e.deny ?? false,
                });
            }
        }

        // A1：resolved account 用账户校验放行域账号反斜杠/尾 $，否则内置模板（Everyone 等）
        // 或含域账号的自定义模板在 apply 时仍会被 validateName 拒绝
        foreach (SharePermission p in targetPerms)
        {
            if (!PsGate.ValidateAccountName(p.account))
            {
                throw Errors.InvalidParam($"预设条目账号非法：{p.account}");
            }
        }
        if (_users is null)
        {
            throw Errors.CommandFailed("权限写入通道未装配（IUserService 缺失），无法应用预设");
        }
        await _users.SetSharePermissionsAsync(shareName, targetPerms);
    }

    /// <summary>TS: exportPresets()——自定义预设 JSON 导出（便于备份）。</summary>
    public Task<string> ExportPresetsAsync()
    {
        List<PermissionPreset> list = ReadCustom();
        // TS: JSON.stringify({ exportedAt, version: 1, presets: list }, null, 2)
        string json = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["exportedAt"] = NowIso(),
                ["version"] = 1,
                ["presets"] = list,
            },
            JsonOpts.Indented);
        return Task.FromResult(json);
    }

    /// <summary>TS: importPresets(json)——合并模式：不覆盖同名；2MB/500 条护栏。</summary>
    public async Task<ImportReport> ImportPresetsAsync(string json)
    {
        // R-1 导入护栏：超大内容先行拒绝
        if (json is null || json.Length > MaxJsonBytes)
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
            || !data.TryGetProperty("presets", out JsonElement presetsEl)
            || presetsEl.ValueKind != JsonValueKind.Array)
        {
            throw Errors.InvalidParam("导入文件格式错误：缺少 presets 字段");
        }
        if (presetsEl.GetArrayLength() > MaxImportItems)
        {
            throw Errors.InvalidParam("导入条目超过 500 上限，请拆分后分批导入");
        }

        List<PermissionPreset> list = ReadCustom();
        HashSet<string> existingIds = list.Select(p => p.id).ToHashSet();
        HashSet<string> existingNames = list.Select(p => p.name).ToHashSet();
        int imported = 0;
        int skipped = 0;
        List<string> errors = new();

        foreach (JsonElement p in presetsEl.EnumerateArray())
        {
            string pName = Str(p, "name") ?? "";
            string pId = Str(p, "id") ?? "";
            try
            {
                if (string.IsNullOrEmpty(pName) || !PsGate.ValidateName(pName))
                {
                    skipped++;
                    errors.Add($"跳过非法模板名：{(pName.Length > 0 ? pName : "(空名)")}");
                    continue;
                }
                if (existingIds.Contains(pId) || existingNames.Contains(pName))
                {
                    skipped++;
                    errors.Add($"跳过重复模板：{pName}");
                    continue;
                }
                List<PresetEntry> entries = ParseEntries(p);
                ValidateEntries(entries);
                PermissionPreset newPreset = new()
                {
                    id = string.IsNullOrEmpty(pId) ? $"custom-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{imported}" : pId,
                    name = pName,
                    description = Str(p, "description") ?? "",
                    builtIn = false,
                    category = Str(p, "category") is { Length: > 0 } c ? c : "导入",
                    entries = entries,
                    createdAt = NowIso(),
                    updatedAt = NowIso(),
                };
                list.Add(newPreset);
                existingIds.Add(newPreset.id);
                existingNames.Add(newPreset.name);
                imported++;
            }
            catch (Exception e)
            {
                skipped++;
                errors.Add($"{(pName.Length > 0 ? pName : "(空名)")}: {e.Message}");
            }
        }

        if (imported > 0) WriteCustom(list);
        return await Task.FromResult(new ImportReport { imported = imported, skipped = skipped, errors = errors });
    }

    // === TS: validateEntries(entries)——条目校验：防止恶意 preset 注入 PowerShell 命令 ===
    private void ValidateEntries(IReadOnlyList<PresetEntry> entries)
    {
        foreach (PresetEntry e in entries)
        {
            if (!AllowedAccess.Contains(e.access.ToString()))
            {
                throw Errors.InvalidParam($"预设条目 access 非法：{e.access}");
            }
            // account 需为合法名称或占位符
            string resolved = ResolveAccount(e.account);
            if (string.IsNullOrEmpty(resolved) || !PsGate.ValidateName(resolved))
            {
                throw Errors.InvalidParam($"预设条目账号非法：{e.account}");
            }
            if (e.accountType != AccountType.User && e.accountType != AccountType.Group)
            {
                throw Errors.InvalidParam($"预设条目 accountType 非法：{e.accountType}");
            }
        }
    }

    // === TS: readCustom()/writeCustom(list)——自定义预设持久化（presets.json） ===

    private static readonly JsonSerializerOptions CustomOpts = JsonCompat.Indented;

    private static List<PermissionPreset> ReadCustom()
    {
        try
        {
            string file = ShareData.PresetFile();
            if (!File.Exists(file)) return new List<PermissionPreset>();
            return JsonSerializer.Deserialize<List<PermissionPreset>>(File.ReadAllText(file))
                ?? new List<PermissionPreset>();
        }
        catch
        {
            return new List<PermissionPreset>();
        }
    }

    private static void WriteCustom(List<PermissionPreset> list)
    {
        string dir = ShareData.DataDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(ShareData.PresetFile(), JsonSerializer.Serialize(list, CustomOpts));
    }

    private static string NowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>导入 JSON 的 entries 数组 → PresetEntry[]（TS 直接信任文件内容，C# 侧按形状收敛）。</summary>
    private static List<PresetEntry> ParseEntries(JsonElement p)
    {
        if (!p.TryGetProperty("entries", out JsonElement el) || el.ValueKind != JsonValueKind.Array)
        {
            return new List<PresetEntry>();
        }
        List<PresetEntry> list = new();
        foreach (JsonElement e in el.EnumerateArray())
        {
            list.Add(new PresetEntry
            {
                account = Str(e, "account") ?? "",
                accountType = Str(e, "accountType") == "User" ? AccountType.User : AccountType.Group,
                access = Str(e, "access") switch
                {
                    "Full" => ShareAccess.Full,
                    "Change" => ShareAccess.Change,
                    "Read" => ShareAccess.Read,
                    "NoAccess" => ShareAccess.NoAccess,
                    _ => ShareAccess.Read,
                },
                deny = e.TryGetProperty("deny", out JsonElement d) && d.ValueKind == JsonValueKind.True,
            });
        }
        return list;
    }
}
