// 移植自 electron/services/protocol/adapters/webdavAdapter.ts（WebDAV/IIS 协议适配器）。
// 与 smbAdapter.ts 的「纯委托」不同：列表/创建/删除/更新/启停/权限在适配器内自带 PS 命令串
// （New-/Remove-/Start-|Stop-Website、Get-|Set-WebConfigurationProperty、Clear-|Add-WebConfiguration），
// 只有服务配置类方法（getServiceStatus/restartService/getConfig/setConfig/defaultConfig/restoreDefault）
// 委托给同名服务 WebdavService；配置节解锁复用 ensureWebdavSectionsUnlocked。
// 命令串逐字复制 TS，参数一律过 PsGate 闸门。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.ProtocolServices; // ConfigJson：PS 回读 JS 语义取值
using WinShare.Core.Services.Shares; // PsGate：TS powershell.ts 闸门窄门面

namespace WinShare.Core.Services.Protocols.Adapters;

/// <summary>WebDAV 协议适配器（TS: webdavAdapter.ts webdavAdapter）。</summary>
public sealed class WebdavAdapter : IProtocolAdapter
{
    private const int Timeout = PsRunner.DefaultTimeoutMs; // TS: DEFAULT_TIMEOUT = 15000

    // WebDAV 配置节完整路径。
    // 注意：必须用完整路径 'system.webServer/webdav/authoring'，不能用短路径 'webdav/authoring'——
    // WebAdministration 模块的 Get/Set-WebConfigurationProperty 对 webdav sectionGroup 的短路径解析
    // 存在缺陷：读取恒返回空、写入静默失败（仅 WARNING "not found"），导致站点永远不被识别为 WebDAV。
    private const string AuthoringFilter = "system.webServer/webdav/authoring";
    private const string AuthoringRulesFilter = "system.webServer/webdav/authoring/authoringRules";
    private const string AnonAuthFilter = "system.webServer/security/authentication/anonymousAuthentication";

    /// <summary>
    /// TS 模板字符串 ${psQuote(name)} 的替换点。
    /// PS 脚本体以 verbatim 常量保存（花括号不转义），运行时只替换站点名占位符，
    /// 保证命令串与 TS 逐字一致且不会被插值语义悄悄改写。
    /// </summary>
    private const string NameToken = "@@NAME@@";

    // TS: LIST_SCRIPT —— 列出所有 WebDAV（authoring 已启用）的 IIS Web 站点
    private const string ListScript = @"Import-Module WebAdministration; Get-Website | ForEach-Object {
  $name = $_.Name
  $authoring = $false
  try { $ae = Get-WebConfiguration -Filter 'system.webServer/webdav/authoring' -PSPath ""IIS:\Sites\$name"" -ErrorAction SilentlyContinue; if ($ae) { $authoring = [bool]$ae.enabled } } catch {}
  if ($authoring) {
    $port = 80
    try {
      $b = $_.bindings.Collection | Select-Object -First 1
      if ($b -and $b.bindingInformation) { $parts = $b.bindingInformation -split ':'; if ($parts.Length -gt 1 -and $parts[1]) { $port = [int]$parts[1] } }
    } catch {}
    $anon = $false
    try { $an = Get-WebConfiguration -Filter 'system.webServer/security/authentication/anonymousAuthentication' -PSPath ""IIS:\Sites\$name"" -ErrorAction SilentlyContinue; if ($an) { $anon = [bool]$an.enabled } } catch {}
    [PSCustomObject]@{ Name=$name; State=$_.State; PhysicalPath=$_.physicalPath; Port=$port; AuthoringEnabled=$authoring; AnonymousEnabled=$anon }
  }
}";

    // TS: fetchSite(name) 的读取脚本（单站点回读）
    private const string FetchScript = @"Import-Module WebAdministration; Get-Website -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq @@NAME@@ } | ForEach-Object {
        $n = $_.Name
        $port = 80
        try { $b = $_.bindings.Collection | Select-Object -First 1; if ($b.bindingInformation) { $parts = $b.bindingInformation -split ':'; if ($parts.Length -gt 1 -and $parts[1]) { $port = [int]$parts[1] } } } catch {}
        $authoring = $false
        try { $ae = Get-WebConfiguration -Filter 'system.webServer/webdav/authoring' -PSPath ""IIS:\Sites\$n"" -ErrorAction SilentlyContinue; if ($ae) { $authoring = [bool]$ae.enabled } } catch {}
        $anon = $false
        try { $an = Get-WebConfiguration -Filter 'system.webServer/security/authentication/anonymousAuthentication' -PSPath ""IIS:\Sites\$n"" -ErrorAction SilentlyContinue; if ($an) { $anon = [bool]$an.enabled } } catch {}
        [PSCustomObject]@{ Name=$n; State=$_.State; PhysicalPath=$_.physicalPath; Port=$port; AuthoringEnabled=$authoring; AnonymousEnabled=$anon }
      }";

    // TS: enableAuthoring(name) 的启用+回读验证脚本
    private const string EnableAuthoringScript = @"try { Set-WebConfigurationProperty -Filter 'system.webServer/webdav/authoring' -PSPath @@NAME@@ -Name enabled -Value $true -ErrorAction Stop } catch {}; $v = $false; try { $ae = Get-WebConfiguration -Filter 'system.webServer/webdav/authoring' -PSPath @@NAME@@ -ErrorAction SilentlyContinue; if ($ae) { $v = [bool]$ae.enabled } } catch {}; $v";

    private readonly IWebdavService _webdav;
    private readonly IPsInvoker _ps;
    private readonly IStateStore _store;

    public WebdavAdapter(IWebdavService? webdav = null, IPsInvoker? ps = null, IStateStore? store = null)
    {
        _ps = ps ?? PsRunner.Default;
        _webdav = webdav ?? new WebdavService(_ps);
        _store = store ?? StateStore.Default;
    }

    public Protocol Protocol => Protocol.Webdav;

    // TS: capabilities —— 无原生会话 API（supportsSessions=false），无 openFiles
    public ProtocolCapabilities Capabilities { get; } = new()
    {
        supportsCreate = true,
        supportsUpdate = true,
        supportsDelete = true,
        supportsToggle = true,
        supportsPermissions = true,
        supportsSessions = false,
        supportsOpenFiles = false,
        supportsServerConfig = true,
        supportsRestart = true,
        permissionModel = PermissionModel.WebdavRules,
    };

    // retries:0 避免未装 IIS 时无谓重试 2 次造成切 Tab 延迟
    public async Task<IReadOnlyList<Share>> ListSharesAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync(ListScript, Timeout, 0);
        return AdapterHelpers.Items(raw)
            .Where(r => !string.IsNullOrEmpty(ConfigJson.Str(r, "Name")))
            .Select(MapWebdavSite)
            .ToList();
    }

    public async Task<Share> CreateShareAsync(CreateShareInput input)
    {
        Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 适配器接收输入:", input.name, input.path,
            input.port, input.anonymousEnabled);
        if (!PsGate.ValidateName(input.name)) throw Errors.InvalidParam("站点名非法");
        if (!PsGate.ValidatePath(input.path)) throw Errors.InvalidParam("路径非法");
        string port = PortText(input.port);
        // 创建 Web 站点（WebDAV 寄生于 Web 站点）
        string createCmd =
            $"New-Website -Name {PsGate.PsQuote(input.name)} -PhysicalPath {PsGate.PsQuote(input.path)} -Port {port} -Force";
        Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 步骤 1/4 创建 Web 站点, PowerShell 命令:", createCmd);
        try
        {
            await _ps.RunVoidAsync(createCmd, Timeout, 0);
        }
        catch (Exception e)
        {
            Logger.Error("adapter:webdavAdapter", "[createShare:webdav] 步骤 1/4 创建站点失败:", e.Message);
            throw;
        }
        Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 步骤 1/4 站点创建成功");
        // 启用 WebDAV authoring 并验证是否真正生效。
        // 需先解锁配置节（overrideModeDefault=Deny），再用完整 filter 路径写入。
        // 若未能启用（功能未装/解锁失败），清理孤儿站点并给出明确提示，避免 IIS 残留。
        Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 步骤 2/4 启用 WebDAV authoring...");
        bool authoringOk = await EnableAuthoringAsync(input.name);
        if (!authoringOk)
        {
            Logger.Error("adapter:webdavAdapter", "[createShare:webdav] 步骤 2/4 authoring 启用失败，清理孤儿站点...");
            // 孤儿站点清理：用 Remove-Website（WebAdministration cmdlet，自动加载模块）
            // 而非 Remove-Item 'IIS:\Sites\...'（依赖 IIS: PSDrive，-NoProfile 下未 Import 时不存在）
            await _ps.RunVoidAsync(
                $"try {{ Remove-Website -Name {PsGate.PsQuote(input.name)} -ErrorAction Stop }} catch {{}}",
                Timeout, 0);
            throw Errors.CommandFailed(
                "WebDAV authoring 启用失败。可能原因：1) WebDAV 发布功能未安装（服务器：Web-WebDAV；客户端：IIS-WebDAV）；2) 配置节锁定且解锁失败（需管理员权限）。请确认后重试");
        }
        Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 步骤 2/4 authoring 启用成功");
        if (input.anonymousEnabled is not null)
        {
            string? anon = PsGate.PsBool(input.anonymousEnabled);
            if (anon is not null)
            {
                Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 步骤 3/4 配置匿名访问, anonymousEnabled:",
                    input.anonymousEnabled);
                // 写站点级 anonymousAuthentication 前需解锁配置节（overrideModeDefault=Deny）
                await _webdav.EnsureWebdavSectionsUnlockedAsync();
                await _ps.RunVoidAsync(
                    $"try {{ Set-WebConfigurationProperty -Filter '{AnonAuthFilter}' -PSPath {AdapterHelpers.IisPath(input.name)} -Name enabled -Value {anon} -ErrorAction Stop }} catch {{}}",
                    Timeout, 0);
                Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 步骤 3/4 匿名访问配置完成");
            }
            else
            {
                Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 步骤 3/4 跳过匿名访问（未启用）");
            }
        }
        else
        {
            Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 步骤 3/4 跳过匿名访问（未指定）");
        }
        Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 步骤 4/4 读取站点信息...");
        Share? site = await FetchSiteAsync(input.name);
        if (site is null)
        {
            Logger.Error("adapter:webdavAdapter", "[createShare:webdav] 步骤 4/4 站点创建后未能读取:", input.name);
            // 清理孤儿站点，避免端口占用残留
            Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 清理孤儿站点...");
            await _ps.RunVoidAsync(
                $"try {{ Remove-Website -Name {PsGate.PsQuote(input.name)} -ErrorAction Stop }} catch {{}}",
                Timeout, 0);
            // 验证清理结果（firstOrNull 归一化：[] truthy 会误报"仍存在"）
            if (await SiteExistsAsync(input.name))
            {
                Logger.Error("adapter:webdavAdapter", "[createShare:webdav] 孤儿站点清理失败！站点仍存在:", input.name);
            }
            else
            {
                Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 孤儿站点已确认清理:", input.name);
            }
            throw Errors.CommandFailed("WebDAV 站点创建后未能读取，已自动清理孤儿站点");
        }
        Logger.Info("adapter:webdavAdapter", "[createShare:webdav] 共享创建完成:", input.name);
        _store.AddJournal(new JournalEntryDraft
        {
            action = JournalAction.Create,
            protocol = Protocol.Webdav,
            name = input.name,
            detail = $"路径 {input.path}",
            undoable = false,
        });
        return site;
    }

    public async Task DeleteShareAsync(string name)
    {
        Logger.Info("adapter:webdavAdapter", "[deleteShare:webdav] 删除站点:", name);
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("站点名非法");
        // 用 Remove-Website（WebAdministration cmdlet，自动加载模块，-Name 接受字符串）
        // 而非 Remove-Item 'IIS:\Sites\...'（依赖 IIS: PSDrive，-NoProfile 下未 Import 时不存在）
        // try/catch 包裹：站点不存在时不抛错（幂等删除）
        string cmd = $"try {{ Remove-Website -Name {PsGate.PsQuote(name)} -ErrorAction Stop }} catch {{}}";
        Logger.Info("adapter:webdavAdapter", "[deleteShare:webdav] PowerShell 命令:", cmd);
        try
        {
            await _ps.RunVoidAsync(cmd, Timeout, PsRunner.DefaultRetries);
            Logger.Info("adapter:webdavAdapter", "[deleteShare:webdav] 删除成功:", name);
            // 操作日志：WebDAV 站点（绑定/授权规则）无还原通道，留档但标记不可撤销
            _store.AddJournal(new JournalEntryDraft
            {
                action = JournalAction.Delete,
                protocol = Protocol.Webdav,
                name = name,
                detail = "站点已删除（WebDAV 站点配置不支持一键撤销，请按留档手工重建）",
                undoable = false,
            });
        }
        catch (Exception e)
        {
            Logger.Error("adapter:webdavAdapter", "[deleteShare:webdav] 删除失败:", name, e.Message);
            throw;
        }
    }

    public async Task<Share> UpdateShareAsync(string name, UpdateShareInput input)
    {
        Logger.Info("adapter:webdavAdapter", "[updateShare:webdav] 更新站点:", name, input.anonymousEnabled);
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("站点名非法");
        try
        {
            if (input.anonymousEnabled is not null)
            {
                string? anon = PsGate.PsBool(input.anonymousEnabled);
                if (anon is not null)
                {
                    Logger.Info("adapter:webdavAdapter", "[updateShare:webdav] 配置匿名访问, anonymousEnabled:",
                        input.anonymousEnabled);
                    // 写站点级 anonymousAuthentication 前需解锁配置节（overrideModeDefault=Deny）
                    await _webdav.EnsureWebdavSectionsUnlockedAsync();
                    await _ps.RunVoidAsync(
                        $"try {{ Set-WebConfigurationProperty -Filter '{AnonAuthFilter}' -PSPath {AdapterHelpers.IisPath(name)} -Name enabled -Value {anon} -ErrorAction Stop }} catch {{}}",
                        Timeout, 0);
                    Logger.Info("adapter:webdavAdapter", "[updateShare:webdav] 匿名访问配置完成");
                }
            }
            // 协议专门适配：站点级 WebDAV 发布(authoring)开关——创建时已支持，更新路径补齐
            if (input.authoringEnabled is not null)
            {
                string? auth = PsGate.PsBool(input.authoringEnabled);
                if (auth is not null)
                {
                    Logger.Info("adapter:webdavAdapter", "[updateShare:webdav] 切换站点 authoring:", name, "→",
                        input.authoringEnabled);
                    await _webdav.EnsureWebdavSectionsUnlockedAsync();
                    await _ps.RunVoidAsync(
                        $"try {{ Set-WebConfigurationProperty -Filter '{AuthoringFilter}' -PSPath {AdapterHelpers.IisPath(name)} -Name enabled -Value {auth} -ErrorAction Stop }} catch {{}}",
                        Timeout, 0);
                    Logger.Info("adapter:webdavAdapter", "[updateShare:webdav] authoring 切换完成");
                }
            }
            Logger.Info("adapter:webdavAdapter", "[updateShare:webdav] 正在读取站点信息...");
            Share? site = await FetchSiteAsync(name);
            if (site is null)
            {
                Logger.Error("adapter:webdavAdapter", "[updateShare:webdav] 站点更新后未能读取:", name);
                throw Errors.ShareNotFound(name);
            }
            Logger.Info("adapter:webdavAdapter", "[updateShare:webdav] 站点更新完成:", name);
            return site;
        }
        catch (Exception e)
        {
            Logger.Error("adapter:webdavAdapter", "[updateShare:webdav] 更新失败:", name, e.Message);
            throw;
        }
    }

    public async Task ToggleShareAsync(string name, bool enabled)
    {
        Logger.Info("adapter:webdavAdapter", "[toggleShare:webdav] 切换站点状态:", name, "→", enabled ? "启用" : "禁用");
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("站点名非法");
        // 先检查站点是否存在，不存在则抛错（firstOrNull 归一化：[] truthy 会误判"存在"）
        if (!await SiteExistsAsync(name))
        {
            Logger.Error("adapter:webdavAdapter", "[toggleShare:webdav] 站点不存在:", name);
            throw Errors.ShareNotFound(name);
        }
        // 用 Start-Website / Stop-Website（WebAdministration cmdlet，自动加载模块）
        // 而非 Get-Item 'IIS:\Sites\...' + .Start()/.Stop()（依赖 IIS: PSDrive）
        // try/catch 容忍"已在目标状态"的非致命错误（如站点已启动时再 Start）
        string action = enabled ? "Start-Website" : "Stop-Website";
        string cmd = $"try {{ {action} -Name {PsGate.PsQuote(name)} -ErrorAction Stop }} catch {{}}";
        Logger.Info("adapter:webdavAdapter", "[toggleShare:webdav] PowerShell 命令:", cmd);
        try
        {
            await _ps.RunVoidAsync(cmd, Timeout, PsRunner.DefaultRetries);
            Logger.Info("adapter:webdavAdapter", "[toggleShare:webdav] 切换成功:", name, "→", enabled ? "启用" : "禁用");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:webdavAdapter", "[toggleShare:webdav] 切换失败:", name, e.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<SharePermission>> GetPermissionsAsync(string name)
    {
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("站点名非法");
        JsonElement raw = await _ps.RunJsonAsync(
            $"Get-WebConfiguration -Filter '{AuthoringRulesFilter}/*' -PSPath {AdapterHelpers.IisPath(name)} -ErrorAction SilentlyContinue",
            Timeout, 0);
        List<SharePermission> list = new();
        foreach (JsonElement r in AdapterHelpers.Items(raw))
        {
            string? users = ConfigJson.JsString(r, "Users");
            string? roles = ConfigJson.JsString(r, "Roles");
            // TS: filter(r => r && (r.Users || r.Roles))
            if (string.IsNullOrEmpty(users) && string.IsNullOrEmpty(roles)) continue;
            bool isGroup = !string.IsNullOrEmpty(roles) && string.IsNullOrEmpty(users);
            // TS: access 含 'source' → Full；含 'write' → Change；否则 Read
            string access = (ConfigJson.JsString(r, "Access") ?? "").ToLowerInvariant();
            ShareAccess mapped = access.Contains("source") ? ShareAccess.Full
                : access.Contains("write") ? ShareAccess.Change
                : ShareAccess.Read;
            list.Add(new SharePermission
            {
                shareName = name,
                account = NonEmpty(users) ?? NonEmpty(roles) ?? "*",
                accountType = isGroup ? AccountType.Group : AccountType.User,
                access = mapped,
                deny = false,
            });
        }
        return list;
    }

    public async Task SetPermissionsAsync(string name, IReadOnlyList<SharePermission> perms)
    {
        Logger.Info("adapter:webdavAdapter", "[setPermissions:webdav] 设置权限:", name, perms.Count, PermsText(perms));
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("站点名非法");
        // authoringRules 配置节默认锁定，需先解锁（复用 WebdavService 的统一解锁）
        await _webdav.EnsureWebdavSectionsUnlockedAsync();
        // 事务补偿：先备份当前权限，若后续授予中途失败则回滚到原状态
        IReadOnlyList<SharePermission> backup = await TryGetPermissionsAsync(name);
        Logger.Info("adapter:webdavAdapter", "[setPermissions:webdav] 已备份当前权限:", backup.Count, "条 →",
            PermsText(backup));
        Logger.Info("adapter:webdavAdapter", "[setPermissions:webdav] 清空已有授权规则...");
        await _ps.RunVoidAsync(
            $"Clear-WebConfiguration -Filter '{AuthoringRulesFilter}' -PSPath {AdapterHelpers.IisPath(name)} -ErrorAction SilentlyContinue",
            Timeout, 0);
        // 逐个授予，收集失败项（不再静默吞错）
        List<string> failed = new();
        foreach (SharePermission p in perms)
        {
            string cmd = AddRuleCommand(name, p, "Stop");
            Logger.Info("adapter:webdavAdapter", "[setPermissions:webdav] 授予权限, PowerShell 命令:", cmd);
            try
            {
                await _ps.RunVoidAsync(cmd, Timeout, 0);
            }
            catch (Exception e)
            {
                Logger.Error("adapter:webdavAdapter", "[setPermissions:webdav] 授予失败:", p.account, e.Message);
                failed.Add(p.account);
            }
        }
        // 若有失败项：回滚到备份状态
        if (failed.Count > 0)
        {
            Logger.Error("adapter:webdavAdapter", "[setPermissions:webdav] 回滚触发！失败账号:",
                string.Join(", ", failed));
            IReadOnlyList<SharePermission> beforeRollback = await TryGetPermissionsAsync(name);
            Logger.Info("adapter:webdavAdapter", "[setPermissions:webdav] 回滚前权限状态:", beforeRollback.Count, "条 →",
                PermsText(beforeRollback));
            await _ps.RunVoidAsync(
                $"Clear-WebConfiguration -Filter '{AuthoringRulesFilter}' -PSPath {AdapterHelpers.IisPath(name)} -ErrorAction SilentlyContinue",
                Timeout, 0);
            foreach (SharePermission p in backup)
            {
                try
                {
                    await _ps.RunVoidAsync(AddRuleCommand(name, p, "SilentlyContinue"), Timeout, 0);
                }
                catch
                {
                    // TS: .catch(() => {})
                }
            }
            IReadOnlyList<SharePermission> afterRollback = await TryGetPermissionsAsync(name);
            Logger.Info("adapter:webdavAdapter", "[setPermissions:webdav] 回滚后权限状态:", afterRollback.Count, "条 →",
                PermsText(afterRollback));
            throw Errors.CommandFailed($"部分权限授予失败（{string.Join(", ", failed)}），已回滚到原始状态");
        }
        Logger.Info("adapter:webdavAdapter", "[setPermissions:webdav] 权限设置成功:", name);
    }

    // WebDAV 无原生会话 API（capabilities.supportsSessions=false）
    public Task<IReadOnlyList<ProtocolSession>> ListSessionsAsync() =>
        throw new NotSupportedException("WebDAV 不支持会话列表");

    public Task CloseSessionAsync(string sessionId) =>
        throw new NotSupportedException("WebDAV 不支持关闭会话");

    // TS 未实现（capabilities.supportsOpenFiles=false）
    public Task<IReadOnlyList<SmbOpenFile>> ListOpenFilesAsync() =>
        throw new NotSupportedException("WebDAV 不支持列出打开文件");

    public Task CloseFileAsync(string fileId) =>
        throw new NotSupportedException("WebDAV 不支持关闭打开文件");

    public Task<ServiceStatus> GetServiceStatusAsync() => _webdav.GetServiceStatusAsync();

    public async Task RestartServiceAsync()
    {
        Logger.Info("adapter:webdavAdapter", "[restartService:webdav] 重启 WebDAV 服务...");
        try
        {
            await _webdav.RestartServiceAsync();
            Logger.Info("adapter:webdavAdapter", "[restartService:webdav] 服务重启成功");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:webdavAdapter", "[restartService:webdav] 服务重启失败:", e.Message);
            throw;
        }
    }

    public async Task StopServiceAsync()
    {
        Logger.Info("adapter:webdavAdapter", "[stopService:webdav] 停止服务...");
        try
        {
            await _webdav.StopServiceAsync();
            Logger.Info("adapter:webdavAdapter", "[stopService:webdav] 服务已停止");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:webdavAdapter", "[stopService:webdav] 停止失败:", e.Message);
            throw;
        }
    }

    public async Task<object?> GetConfigAsync() => await _webdav.GetConfigAsync();

    public async Task SetConfigAsync(object? config)
    {
        Logger.Info("adapter:webdavAdapter", "[setConfig:webdav] 设置服务器配置");
        if (config is not WebdavServerConfig typed) throw Errors.InvalidParam("WebDAV 配置对象类型不匹配");
        try
        {
            await _webdav.SetConfigAsync(typed);
            Logger.Info("adapter:webdavAdapter", "[setConfig:webdav] 配置设置成功");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:webdavAdapter", "[setConfig:webdav] 配置设置失败:", e.Message);
            throw;
        }
    }

    public object? DefaultConfig() => _webdav.DefaultConfig();

    public async Task<object?> RestoreDefaultAsync() => await _webdav.RestoreDefaultAsync();

    // ===== 内部助手 =====

    // TS: mapWebdavSite(r: RawWebdavSite)
    private static Share MapWebdavSite(JsonElement r) => new()
    {
        name = ConfigJson.Str(r, "Name") ?? "",
        path = NonEmpty(ConfigJson.Str(r, "PhysicalPath")) ?? "",
        description = "",
        protocol = Protocol.Webdav,
        type = ShareType.Disk,
        hidden = false,
        encrypted = false,
        concurrentUsers = 0,
        // TS: r.State === 'Started' ? 'Enabled' : 'Disabled'
        status = ConfigJson.Str(r, "State") == "Started" ? ShareStatus.Enabled : ShareStatus.Disabled,
        cached = false,
        // TS: r.Port || 80
        port = ConfigJson.Number(r, "Port") is double d && d != 0 ? (int)d : 80,
        siteName = ConfigJson.Str(r, "Name"),
        anonymousEnabled = ConfigJson.Truthy(r, "AnonymousEnabled"),
        authoringEnabled = ConfigJson.Truthy(r, "AuthoringEnabled"),
    };

    // TS: enableAuthoring(name) —— 启用 WebDAV authoring 并回读验证是否真正生效
    private async Task<bool> EnableAuthoringAsync(string name)
    {
        // 先解锁配置节（overrideModeDefault=Deny）——复用 WebdavService 的统一解锁
        await _webdav.EnsureWebdavSectionsUnlockedAsync();
        try
        {
            JsonElement ok = await _ps.RunJsonAsync(
                EnableAuthoringScript.Replace(NameToken, AdapterHelpers.IisPath(name)), Timeout,
                PsRunner.DefaultRetries);
            return AdapterHelpers.JsTruthy(ok);
        }
        catch
        {
            return false;
        }
    }

    // TS: fetchSite(name) —— 用 Get-Website | Where-Object 过滤，避免 -Name 找不到时抛终止性错误
    private async Task<Share?> FetchSiteAsync(string name)
    {
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(
                FetchScript.Replace(NameToken, PsGate.PsQuote(name)), Timeout, 0);
            foreach (JsonElement r in AdapterHelpers.Items(raw))
            {
                if (!string.IsNullOrEmpty(ConfigJson.Str(r, "Name"))) return MapWebdavSite(r);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // TS: firstOrNull(await runPowerShell(...).catch(() => null))；!exists 是 JS 真值判断
    private async Task<bool> SiteExistsAsync(string name)
    {
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(
                $"Get-Website | Where-Object {{ $_.Name -eq {PsGate.PsQuote(name)} }} | Select-Object -First 1 -ExpandProperty Name -ErrorAction SilentlyContinue",
                Timeout, 0);
            return AdapterHelpers.JsTruthy(AdapterHelpers.FirstOrNull(raw));
        }
        catch
        {
            return false;
        }
    }

    // TS: 逐条授权规则命令（用户用 users=，组用 roles=；access 由 access 位展开）
    private static string AddRuleCommand(string name, SharePermission p, string errorAction)
    {
        List<string> bits = new();
        if (p.access is ShareAccess.Read or ShareAccess.Change or ShareAccess.Full) bits.Add("Read");
        if (p.access is ShareAccess.Change or ShareAccess.Full) bits.Add("Write");
        if (p.access == ShareAccess.Full) bits.Add("Source");
        string access = string.Join(',', bits);
        string userField = p.accountType == AccountType.Group
            ? $"roles={PsGate.PsQuote(p.account)}"
            : $"users={PsGate.PsQuote(p.account)}";
        return
            $"Add-WebConfiguration -Filter '{AuthoringRulesFilter}' -PSPath {AdapterHelpers.IisPath(name)} -Value @{{{userField};path='*';access='{access}'}} -ErrorAction {errorAction}";
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

    // TS: -Port ${input.port || 80} —— 数值经闸门；0/未提供回落 80
    private static string PortText(int? value)
    {
        if (value is not int v || v == 0) return "80";
        return PsGate.PsNumber(v) ?? "80";
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string PermsText(IReadOnlyList<SharePermission> perms) =>
        perms.Count == 0
            ? "(空)"
            : string.Join(", ", perms.Select(p => $"{p.account}={p.access}{(p.deny ? "(deny)" : "")}"));
}
