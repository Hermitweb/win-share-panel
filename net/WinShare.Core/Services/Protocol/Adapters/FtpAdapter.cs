// 移植自 electron/services/protocol/adapters/ftpAdapter.ts（FTP/IIS 协议适配器）。
// 与 smbAdapter.ts 的「纯委托」不同：列表/创建/删除/更新/启停/权限在适配器内自带 PS 命令串
// （New-WebFtpSite / Remove-Item IIS:\Sites\… / Start-|Stop-Website / Get-|Set-WebConfigurationProperty /
// Clear-|Add-WebConfiguration），只有服务配置类方法（getServiceStatus/restartService/getConfig/setConfig/
// defaultConfig/restoreDefault）委托给同名服务 FtpService；配置节解锁复用 ensureFtpSectionsUnlocked。
// 命令串逐字复制 TS，参数一律过 PsGate 闸门。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.ProtocolServices; // ConfigJson：PS 回读 JS 语义取值
using WinShare.Core.Services.Shares; // PsGate：TS powershell.ts 闸门窄门面

namespace WinShare.Core.Services.Protocols.Adapters;

/// <summary>FTP（IIS）协议适配器（TS: ftpAdapter.ts ftpAdapter）。</summary>
public sealed class FtpAdapter : IProtocolAdapter
{
    private const int Timeout = PsRunner.DefaultTimeoutMs; // TS: DEFAULT_TIMEOUT = 15000

    // SSL 策略白名单（与 types.ts 声明一致），拼入命令前运行时校验，杜绝注入
    private static readonly IReadOnlySet<string> SslPolicies =
        new HashSet<string> { "SslAllow", "SslRequire", "SslRequireCredentials" };

    /// <summary>
    /// TS 模板字符串 ${psQuote(name)} 的替换点。
    /// PS 脚本体以 verbatim 常量保存（花括号不转义），运行时只替换站点名占位符，
    /// 保证命令串与 TS 逐字一致且不会被插值语义悄悄改写。
    /// </summary>
    private const string NameToken = "@@NAME@@";

    // TS: LIST_SCRIPT —— WebAdministration 没有 Get-WebFtpSite cmdlet，必须用
    // Get-Website + bindings.protocol='ftp' 过滤来列出 FTP 站点。
    private const string ListScript = @"Import-Module WebAdministration; Get-Website | Where-Object { $hasFtp=$false; try { foreach ($b in $_.bindings.Collection) { if ($b.protocol -eq 'ftp') { $hasFtp=$true; break } } } catch {}; $hasFtp } | ForEach-Object {
  $name = $_.Name
  $site = Get-Item ""IIS:\Sites\$name"" -ErrorAction SilentlyContinue
  $path = ''
  if ($site) { $path = $site.physicalPath }
  $port = 21
  try {
    $b = $_.bindings.Collection | Select-Object -First 1
    if ($b -and $b.bindingInformation) {
      $parts = $b.bindingInformation -split ':'
      if ($parts.Length -gt 1 -and $parts[1]) { $port = [int]$parts[1] }
    }
  } catch {}
  $ssl = ''
  try { $ssl = (Get-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath ""IIS:\Sites\$name"" -Name controlChannelPolicy -ErrorAction SilentlyContinue) } catch {}
  $anon = $false
  try { $anon = [bool](Get-WebConfigurationProperty -Filter 'ftpServer/security/authentication/anonymousAuthentication' -PSPath ""IIS:\Sites\$name"" -Name enabled -ErrorAction SilentlyContinue) } catch {}
  $basic = $false
  try { $basic = [bool](Get-WebConfigurationProperty -Filter 'ftpServer/security/authentication/basicAuthentication' -PSPath ""IIS:\Sites\$name"" -Name enabled -ErrorAction SilentlyContinue) } catch {}
  [PSCustomObject]@{ Name=$name; State=$_.State; PhysicalPath=$path; Port=$port; SslPolicy=$ssl; AnonymousEnabled=$anon; BasicEnabled=$basic }
}";

    // TS: fetchSite(name) 的读取脚本（单站点回读）
    private const string FetchScript = @"Import-Module WebAdministration; Get-Website | Where-Object { $_.Name -eq @@NAME@@ } | Where-Object { $hasFtp=$false; try { foreach ($b in $_.bindings.Collection) { if ($b.protocol -eq 'ftp') { $hasFtp=$true; break } } } catch {}; $hasFtp } | ForEach-Object {
        $n = $_.Name
        $site = Get-Item ""IIS:\Sites\$n"" -ErrorAction SilentlyContinue
        $path = ''; if ($site) { $path = $site.physicalPath }
        $port = 21
        try { $b = $_.bindings.Collection | Select-Object -First 1; if ($b.bindingInformation) { $parts = $b.bindingInformation -split ':'; if ($parts.Length -gt 1 -and $parts[1]) { $port = [int]$parts[1] } } } catch {}
        $ssl = ''
        try { $ssl = (Get-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath ""IIS:\Sites\$n"" -Name controlChannelPolicy -ErrorAction SilentlyContinue) } catch {}
        $anon = $false
        try { $anon = [bool](Get-WebConfigurationProperty -Filter 'ftpServer/security/authentication/anonymousAuthentication' -PSPath ""IIS:\Sites\$n"" -Name enabled -ErrorAction SilentlyContinue) } catch {}
        $basic = $false
        try { $basic = [bool](Get-WebConfigurationProperty -Filter 'ftpServer/security/authentication/basicAuthentication' -PSPath ""IIS:\Sites\$n"" -Name enabled -ErrorAction SilentlyContinue) } catch {}
        [PSCustomObject]@{ Name=$n; State=$_.State; PhysicalPath=$path; Port=$port; SslPolicy=$ssl; AnonymousEnabled=$anon; BasicEnabled=$basic }
      }";

    private readonly IFtpService _ftp;
    private readonly IPsInvoker _ps;
    private readonly IStateStore _store;

    public FtpAdapter(IFtpService? ftp = null, IPsInvoker? ps = null, IStateStore? store = null)
    {
        _ps = ps ?? PsRunner.Default;
        _ftp = ftp ?? new FtpService(_ps);
        _store = store ?? StateStore.Default;
    }

    public Protocol Protocol => Protocol.Ftp;

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
        permissionModel = PermissionModel.IisAuth,
    };

    // retries:0 避免未装 IIS 时无谓重试 2 次造成切 Tab 延迟
    public async Task<IReadOnlyList<Share>> ListSharesAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync(ListScript, Timeout, 0);
        return AdapterHelpers.Items(raw)
            .Where(r => !string.IsNullOrEmpty(ConfigJson.Str(r, "Name")))
            .Select(MapFtpSite)
            .ToList();
    }

    public async Task<Share> CreateShareAsync(CreateShareInput input)
    {
        Logger.Info("adapter:ftpAdapter", "[createShare:ftp] 适配器接收输入:", input.name, input.path,
            input.port, input.sslPolicy, input.authMode);
        if (!PsGate.ValidateName(input.name)) throw Errors.InvalidParam("站点名非法");
        if (!PsGate.ValidatePath(input.path)) throw Errors.InvalidParam("路径非法");
        string port = PortText(input.port);
        // 创建 FTP 站点（retries:0，端口冲突等失败为确定性错误，重试无意义）
        string createCmd =
            $"New-WebFtpSite -Name {PsGate.PsQuote(input.name)} -PhysicalPath {PsGate.PsQuote(input.path)} -Port {port} -Force";
        Logger.Info("adapter:ftpAdapter", "[createShare:ftp] 步骤 1/3 创建站点, PowerShell 命令:", createCmd);
        try
        {
            await _ps.RunVoidAsync(createCmd, Timeout, 0);
        }
        catch (Exception e)
        {
            Logger.Error("adapter:ftpAdapter", "[createShare:ftp] 步骤 1/3 创建站点失败:", e.Message);
            throw;
        }
        Logger.Info("adapter:ftpAdapter", "[createShare:ftp] 步骤 1/3 站点创建成功");
        // 端口冲突由 New-WebFtpSite 抛错；成功后配置 SSL 与认证（best-effort，不阻断创建）
        Logger.Info("adapter:ftpAdapter", "[createShare:ftp] 步骤 2/3 应用 SSL/认证配置, sslPolicy:", input.sslPolicy,
            "authMode:", input.authMode);
        try
        {
            await ApplyFtpConfigAsync(input.name, input.sslPolicy, input.authMode);
            Logger.Info("adapter:ftpAdapter", "[createShare:ftp] 步骤 2/3 配置应用完成");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:ftpAdapter", "[createShare:ftp] 步骤 2/3 配置应用失败（非致命）:", e.Message);
        }
        Logger.Info("adapter:ftpAdapter", "[createShare:ftp] 步骤 3/3 读取站点信息...");
        Share? site = await FetchSiteAsync(input.name);
        if (site is null)
        {
            Logger.Error("adapter:ftpAdapter", "[createShare:ftp] 步骤 3/3 站点创建后未能读取:", input.name);
            // 清理孤儿站点，避免端口占用残留
            Logger.Info("adapter:ftpAdapter", "[createShare:ftp] 清理孤儿站点...");
            await _ps.RunVoidAsync(
                $"Import-Module WebAdministration; try {{ Remove-Item {AdapterHelpers.IisPath(input.name)} -Recurse -Force -ErrorAction Stop }} catch {{}}",
                Timeout, 0);
            // 验证清理结果（firstOrNull 归一化：[] truthy 会误报"仍存在"）
            if (await SiteExistsAsync(input.name))
            {
                Logger.Error("adapter:ftpAdapter", "[createShare:ftp] 孤儿站点清理失败！站点仍存在:", input.name);
            }
            else
            {
                Logger.Info("adapter:ftpAdapter", "[createShare:ftp] 孤儿站点已确认清理:", input.name);
            }
            throw Errors.CommandFailed("FTP 站点创建后未能读取，已自动清理孤儿站点");
        }
        Logger.Info("adapter:ftpAdapter", "[createShare:ftp] 共享创建完成:", input.name);
        _store.AddJournal(new JournalEntryDraft
        {
            action = JournalAction.Create,
            protocol = Protocol.Ftp,
            name = input.name,
            detail = $"路径 {input.path}",
            undoable = false,
        });
        return site;
    }

    public async Task DeleteShareAsync(string name)
    {
        Logger.Info("adapter:ftpAdapter", "[deleteShare:ftp] 删除站点:", name);
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("站点名非法");
        // Import-Module 确保 IIS: PSDrive 存在（-NoProfile 下未自动加载）；
        // try/catch 包裹：站点不存在时不抛错（幂等删除）
        string cmd =
            $"Import-Module WebAdministration; try {{ Remove-Item {AdapterHelpers.IisPath(name)} -Recurse -Force -ErrorAction Stop }} catch {{}}";
        Logger.Info("adapter:ftpAdapter", "[deleteShare:ftp] PowerShell 命令:", cmd);
        try
        {
            await _ps.RunVoidAsync(cmd, Timeout, PsRunner.DefaultRetries);
            Logger.Info("adapter:ftpAdapter", "[deleteShare:ftp] 删除成功:", name);
            // 操作日志：FTP 站点配置（绑定/SSL/授权规则）无通用快照还原，
            // 因此记录留档但标记不可撤销——UI 会明示原因，不做假承诺
            _store.AddJournal(new JournalEntryDraft
            {
                action = JournalAction.Delete,
                protocol = Protocol.Ftp,
                name = name,
                detail = "站点已删除（FTP 站点配置不支持一键撤销，请按留档手工重建）",
                undoable = false,
            });
        }
        catch (Exception e)
        {
            Logger.Error("adapter:ftpAdapter", "[deleteShare:ftp] 删除失败:", name, e.Message);
            throw;
        }
    }

    public async Task<Share> UpdateShareAsync(string name, UpdateShareInput input)
    {
        Logger.Info("adapter:ftpAdapter", "[updateShare:ftp] 更新站点:", name, input.sslPolicy, input.authMode,
            input.physicalPath);
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("站点名非法");
        try
        {
            // 协议专门适配：FTP 站点支持修改本地物理路径（validatePath 校验后经单引号上下文嵌入）
            if (input.physicalPath is not null)
            {
                if (!PsGate.ValidatePath(input.physicalPath)) throw Errors.InvalidParam("路径非法");
                Logger.Info("adapter:ftpAdapter", "[updateShare:ftp] 更新物理路径:", name, "→", input.physicalPath);
                await _ps.RunVoidAsync(
                    $"Import-Module WebAdministration; Set-ItemProperty {AdapterHelpers.IisPath(name)} -Name physicalPath -Value {PsGate.PsQuote(input.physicalPath)}",
                    Timeout, 0);
            }
            await ApplyFtpConfigAsync(name, input.sslPolicy, input.authMode);
            Logger.Info("adapter:ftpAdapter", "[updateShare:ftp] 配置应用完成，正在读取站点信息...");
            Share? site = await FetchSiteAsync(name);
            if (site is null)
            {
                Logger.Error("adapter:ftpAdapter", "[updateShare:ftp] 站点更新后未能读取:", name);
                throw Errors.ShareNotFound(name);
            }
            Logger.Info("adapter:ftpAdapter", "[updateShare:ftp] 站点更新完成:", name);
            return site;
        }
        catch (Exception e)
        {
            Logger.Error("adapter:ftpAdapter", "[updateShare:ftp] 更新失败:", name, e.Message);
            throw;
        }
    }

    public async Task ToggleShareAsync(string name, bool enabled)
    {
        Logger.Info("adapter:ftpAdapter", "[toggleShare:ftp] 切换站点状态:", name, "→", enabled ? "启用" : "禁用");
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("站点名非法");
        // 先检查站点是否存在，不存在则抛错（firstOrNull 归一化：[] truthy 会误判"存在"）
        if (!await SiteExistsAsync(name))
        {
            Logger.Error("adapter:ftpAdapter", "[toggleShare:ftp] 站点不存在:", name);
            throw Errors.ShareNotFound(name);
        }
        // 用 Start-Website / Stop-Website（WebAdministration cmdlet，自动加载模块）
        // 不用 Get-Item + .Start()/.Stop()——后者在 FTP 站点上抛 0x800710D8
        // try/catch 容忍"已在目标状态"的非致命错误（如站点已启动时再 Start）
        string action = enabled ? "Start-Website" : "Stop-Website";
        string cmd = $"try {{ {action} -Name {PsGate.PsQuote(name)} -ErrorAction Stop }} catch {{}}";
        Logger.Info("adapter:ftpAdapter", "[toggleShare:ftp] PowerShell 命令:", cmd);
        try
        {
            await _ps.RunVoidAsync(cmd, Timeout, PsRunner.DefaultRetries);
            Logger.Info("adapter:ftpAdapter", "[toggleShare:ftp] 切换成功:", name, "→", enabled ? "启用" : "禁用");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:ftpAdapter", "[toggleShare:ftp] 切换失败:", name, e.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<SharePermission>> GetPermissionsAsync(string name)
    {
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("站点名非法");
        JsonElement raw = await _ps.RunJsonAsync(
            $"Get-WebConfiguration -Filter 'ftpServer/security/authorization/*' -PSPath {AdapterHelpers.IisPath(name)} -ErrorAction SilentlyContinue",
            Timeout, 0);
        List<SharePermission> list = new();
        foreach (JsonElement r in AdapterHelpers.Items(raw))
        {
            string? users = ConfigJson.JsString(r, "Users");
            string? roles = ConfigJson.JsString(r, "Roles");
            // TS: filter(r => r && (r.Users || r.Roles))
            if (string.IsNullOrEmpty(users) && string.IsNullOrEmpty(roles)) continue;
            bool isGroup = !string.IsNullOrEmpty(roles) && string.IsNullOrEmpty(users);
            // TS: perm.includes('write') → Change；否则 Read（read/空 均 Read）
            string perm = (ConfigJson.JsString(r, "Permissions") ?? "").ToLowerInvariant();
            list.Add(new SharePermission
            {
                shareName = name,
                account = NonEmpty(users) ?? NonEmpty(roles) ?? "*",
                accountType = isGroup ? AccountType.Group : AccountType.User,
                access = perm.Contains("write") ? ShareAccess.Change : ShareAccess.Read,
                deny = string.Equals(ConfigJson.JsString(r, "AccessType"), "deny", StringComparison.OrdinalIgnoreCase),
            });
        }
        return list;
    }

    public async Task SetPermissionsAsync(string name, IReadOnlyList<SharePermission> perms)
    {
        Logger.Info("adapter:ftpAdapter", "[setPermissions:ftp] 设置权限:", name, perms.Count, PermsText(perms));
        if (!PsGate.ValidateName(name)) throw Errors.InvalidParam("站点名非法");
        // authorization 配置节默认锁定，需先解锁（复用 FtpService 的统一解锁）
        await _ftp.EnsureFtpSectionsUnlockedAsync();
        // 事务补偿：先备份当前权限，若后续授予中途失败则回滚到原状态
        IReadOnlyList<SharePermission> backup = await TryGetPermissionsAsync(name);
        Logger.Info("adapter:ftpAdapter", "[setPermissions:ftp] 已备份当前权限:", backup.Count, "条 →",
            PermsText(backup));
        // 先清空已有授权规则，再按传入列表重建
        Logger.Info("adapter:ftpAdapter", "[setPermissions:ftp] 清空已有授权规则...");
        await _ps.RunVoidAsync(
            $"Clear-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath {AdapterHelpers.IisPath(name)} -ErrorAction SilentlyContinue",
            Timeout, 0);
        // 逐个授予，收集失败项（不再静默吞错）
        List<string> failed = new();
        foreach (SharePermission p in perms)
        {
            string cmd = AddRuleCommand(name, p, "Stop");
            Logger.Info("adapter:ftpAdapter", "[setPermissions:ftp] 授予权限, PowerShell 命令:", cmd);
            try
            {
                await _ps.RunVoidAsync(cmd, Timeout, 0);
            }
            catch (Exception e)
            {
                Logger.Error("adapter:ftpAdapter", "[setPermissions:ftp] 授予失败:", p.account, e.Message);
                failed.Add(p.account);
            }
        }
        // 若有失败项：回滚到备份状态
        if (failed.Count > 0)
        {
            Logger.Error("adapter:ftpAdapter", "[setPermissions:ftp] 回滚触发！失败账号:", string.Join(", ", failed));
            IReadOnlyList<SharePermission> beforeRollback = await TryGetPermissionsAsync(name);
            Logger.Info("adapter:ftpAdapter", "[setPermissions:ftp] 回滚前权限状态:", beforeRollback.Count, "条 →",
                PermsText(beforeRollback));
            await _ps.RunVoidAsync(
                $"Clear-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath {AdapterHelpers.IisPath(name)} -ErrorAction SilentlyContinue",
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
            Logger.Info("adapter:ftpAdapter", "[setPermissions:ftp] 回滚后权限状态:", afterRollback.Count, "条 →",
                PermsText(afterRollback));
            throw Errors.CommandFailed($"部分权限授予失败（{string.Join(", ", failed)}），已回滚到原始状态");
        }
        Logger.Info("adapter:ftpAdapter", "[setPermissions:ftp] 权限设置成功:", name);
    }

    // FTP 无原生会话 API（capabilities.supportsSessions=false）
    public Task<IReadOnlyList<ProtocolSession>> ListSessionsAsync() =>
        throw new NotSupportedException("FTP 不支持会话列表");

    public Task CloseSessionAsync(string sessionId) =>
        throw new NotSupportedException("FTP 不支持关闭会话");

    // TS 未实现（capabilities.supportsOpenFiles=false）
    public Task<IReadOnlyList<SmbOpenFile>> ListOpenFilesAsync() =>
        throw new NotSupportedException("FTP 不支持列出打开文件");

    public Task CloseFileAsync(string fileId) =>
        throw new NotSupportedException("FTP 不支持关闭打开文件");

    public Task<ServiceStatus> GetServiceStatusAsync() => _ftp.GetServiceStatusAsync();

    public async Task RestartServiceAsync()
    {
        Logger.Info("adapter:ftpAdapter", "[restartService:ftp] 重启 FTP 服务...");
        try
        {
            await _ftp.RestartServiceAsync();
            Logger.Info("adapter:ftpAdapter", "[restartService:ftp] 服务重启成功");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:ftpAdapter", "[restartService:ftp] 服务重启失败:", e.Message);
            throw;
        }
    }

    public async Task StopServiceAsync()
    {
        Logger.Info("adapter:ftpAdapter", "[stopService:ftp] 停止服务...");
        try
        {
            await _ftp.StopServiceAsync();
            Logger.Info("adapter:ftpAdapter", "[stopService:ftp] 服务已停止");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:ftpAdapter", "[stopService:ftp] 停止失败:", e.Message);
            throw;
        }
    }

    public async Task<object?> GetConfigAsync() => await _ftp.GetConfigAsync();

    public async Task SetConfigAsync(object? config)
    {
        Logger.Info("adapter:ftpAdapter", "[setConfig:ftp] 设置服务器配置");
        if (config is not FtpServerConfig typed) throw Errors.InvalidParam("FTP 配置对象类型不匹配");
        try
        {
            await _ftp.SetConfigAsync(typed);
            Logger.Info("adapter:ftpAdapter", "[setConfig:ftp] 配置设置成功");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:ftpAdapter", "[setConfig:ftp] 配置设置失败:", e.Message);
            throw;
        }
    }

    public object? DefaultConfig() => _ftp.DefaultConfig();

    public async Task<object?> RestoreDefaultAsync() => await _ftp.RestoreDefaultAsync();

    // ===== 内部助手 =====

    // TS: mapFtpSite(r: RawFtpSite)
    private static Share MapFtpSite(JsonElement r)
    {
        // TS: AnonymousEnabled ? 'anonymous' : BasicEnabled ? 'basic' : 'windows'
        FtpAuthMode authMode = ConfigJson.Truthy(r, "AnonymousEnabled") ? FtpAuthMode.Anonymous
            : ConfigJson.Truthy(r, "BasicEnabled") ? FtpAuthMode.Basic
            : FtpAuthMode.Windows;
        return new Share
        {
            name = ConfigJson.Str(r, "Name") ?? "",
            path = NonEmpty(ConfigJson.Str(r, "PhysicalPath")) ?? "",
            description = "",
            protocol = Protocol.Ftp,
            type = ShareType.Disk,
            hidden = false,
            encrypted = false,
            concurrentUsers = 0,
            // TS: r.State === 'Started' ? 'Enabled' : 'Disabled'
            status = ConfigJson.Str(r, "State") == "Started" ? ShareStatus.Enabled : ShareStatus.Disabled,
            cached = false,
            // TS: r.Port || 21
            port = ConfigJson.Number(r, "Port") is double d && d != 0 ? (int)d : 21,
            siteName = ConfigJson.Str(r, "Name"),
            sslPolicy = NormalizeSsl(ConfigJson.Str(r, "SslPolicy")),
            authMode = authMode,
        };
    }

    // TS: normalizeSsl —— /requirecredentials/i → SslRequireCredentials；/^sslrequire$/i → SslRequire；否则 SslAllow
    private static SslPolicy? NormalizeSsl(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Contains("requirecredentials", StringComparison.OrdinalIgnoreCase)) return SslPolicy.SslRequireCredentials;
        if (string.Equals(v, "sslrequire", StringComparison.OrdinalIgnoreCase)) return SslPolicy.SslRequire;
        return SslPolicy.SslAllow;
    }

    // TS: fetchSite(name) —— 用 Get-Website + FTP 绑定过滤 + 名称匹配，避免 -Name 找不到时抛终止性错误
    private async Task<Share?> FetchSiteAsync(string name)
    {
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(
                FetchScript.Replace(NameToken, PsGate.PsQuote(name)), Timeout, 0);
            foreach (JsonElement r in AdapterHelpers.Items(raw))
            {
                if (!string.IsNullOrEmpty(ConfigJson.Str(r, "Name"))) return MapFtpSite(r);
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

    // TS: applyFtpConfig(name, input) —— 应用 SSL 策略与认证模式配置（best-effort）
    // 站点已由 New-WebFtpSite 创建成功，SSL/认证配置属于增强项；
    // IIS 中 anonymousAuthentication/basicAuthentication 等配置节默认在服务器级别锁定，
    // 需先调用 EnsureFtpSectionsUnlocked 解锁，再用 try/catch + -ErrorAction Stop 包裹每个 cmdlet，
    // 确保非终止错误也能被 catch 捕获，不阻断创建流程。
    private async Task ApplyFtpConfigAsync(string name, SslPolicy? sslPolicy, FtpAuthMode? authMode)
    {
        string pspath = AdapterHelpers.IisPath(name);
        List<string> parts = new();
        string? policy = PsGate.PsEnum(sslPolicy?.ToString(), SslPolicies);
        if (policy is not null)
        {
            // 同时设置控制通道与数据通道策略（白名单校验 + psQuote 双重保险）
            string p = PsGate.PsQuote(policy);
            parts.Add(
                $"try {{ Set-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath {pspath} -Name controlChannelPolicy -Value {p} -ErrorAction Stop }} catch {{}}");
            parts.Add(
                $"try {{ Set-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath {pspath} -Name dataChannelPolicy -Value {p} -ErrorAction Stop }} catch {{}}");
        }
        if (authMode is FtpAuthMode mode)
        {
            // 启用/禁用匿名与基本认证
            string anonEnabled = mode == FtpAuthMode.Anonymous ? "$true" : "$false";
            string basicEnabled = mode is FtpAuthMode.Basic or FtpAuthMode.Windows ? "$true" : "$false";
            parts.Add(
                $"try {{ Set-WebConfigurationProperty -Filter 'ftpServer/security/authentication/anonymousAuthentication' -PSPath {pspath} -Name enabled -Value {anonEnabled} -ErrorAction Stop }} catch {{}}");
            parts.Add(
                $"try {{ Set-WebConfigurationProperty -Filter 'ftpServer/security/authentication/basicAuthentication' -PSPath {pspath} -Name enabled -Value {basicEnabled} -ErrorAction Stop }} catch {{}}");
        }
        if (parts.Count == 0) return;
        // 预解锁配置节（overrideModeDefault=Deny），避免 Set 静默失败
        await _ftp.EnsureFtpSectionsUnlockedAsync();
        try
        {
            // retries:0 避免配置节锁定时无谓重试造成延迟
            await _ps.RunVoidAsync(string.Join("; ", parts), Timeout, 0);
        }
        catch
        {
            // best-effort：配置应用失败（如配置节锁定）不应阻断站点创建/更新
        }
    }

    // TS: 逐条授权命令（用户用 users=，组用 roles=；permissions 由 access 位展开）
    private static string AddRuleCommand(string name, SharePermission p, string errorAction)
    {
        string accessType = p.deny ? "Deny" : "Allow";
        List<string> permBits = new();
        if (p.access is ShareAccess.Read or ShareAccess.Full or ShareAccess.Change) permBits.Add("Read");
        if (p.access is ShareAccess.Change or ShareAccess.Full) permBits.Add("Write");
        string permissions = string.Join(',', permBits);
        string userField = p.accountType == AccountType.Group
            ? $"roles={PsGate.PsQuote(p.account)}"
            : $"users={PsGate.PsQuote(p.account)}";
        return
            $"Add-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath {AdapterHelpers.IisPath(name)} -Value @{{accessType='{accessType}';{userField};permissions='{permissions}'}} -ErrorAction {errorAction}";
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

    // TS: -Port ${input.port || 21} —— 数值经闸门；0/未提供回落 21
    private static string PortText(int? value)
    {
        if (value is not int v || v == 0) return "21";
        return PsGate.PsNumber(v) ?? "21";
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string PermsText(IReadOnlyList<SharePermission> perms) =>
        perms.Count == 0
            ? "(空)"
            : string.Join(", ", perms.Select(p => $"{p.account}={p.access}{(p.deny ? "(deny)" : "")}"));
}
