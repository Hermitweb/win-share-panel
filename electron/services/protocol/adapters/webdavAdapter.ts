import type { ProtocolAdapter } from '../ProtocolAdapter'
import type {
  Share,
  SharePermission,
  ServiceStatus,
  WebdavServerConfig,
  CreateShareInput,
  UpdateShareInput
} from '../../../types'
import { runPowerShell, runPowerShellVoid, psQuote, psEscapeSingle, psBool, validateName, validatePath } from '../../../lib/powershell'
import { Errors } from '../../../lib/errors'
import * as webdav from '../../webdav'

interface RawWebdavSite {
  Name: string
  State: string
  PhysicalPath: string
  Port: number
  AuthoringEnabled: boolean
  AnonymousEnabled: boolean
}

interface RawWebdavRule {
  Users: string
  Roles: string
  Path: string
  Access: string
}

// WebDAV 配置节完整路径。
// 注意：必须用完整路径 'system.webServer/webdav/authoring'，不能用短路径 'webdav/authoring'——
// WebAdministration 模块的 Get/Set-WebConfigurationProperty 对 webdav sectionGroup 的短路径解析
// 存在缺陷：读取恒返回空、写入静默失败（仅 WARNING "not found"），导致站点永远不被识别为 WebDAV。
const AUTHORING_FILTER = 'system.webServer/webdav/authoring'
const AUTHORING_RULES_FILTER = 'system.webServer/webdav/authoring/authoringRules'
const ANON_AUTH_FILTER = 'system.webServer/security/authentication/anonymousAuthentication'

// 构造 IIS 站点路径：'IIS:\Sites\{name}'，单引号包裹防止 $/反引号被插值
function iisPath(name: string): string {
  return `'IIS:\\Sites\\${psEscapeSingle(name)}'`
}

// 列出所有 WebDAV（authoring 已启用）的 IIS Web 站点
// Import-Module 确保 IIS: PSDrive 与 WebAdministration cmdlet 可用（-NoProfile 下未自动加载）
const LIST_SCRIPT = `Import-Module WebAdministration; Get-Website | ForEach-Object {
  $name = $_.Name
  $authoring = $false
  try { $ae = Get-WebConfiguration -Filter '${AUTHORING_FILTER}' -PSPath "IIS:\\Sites\\$name" -ErrorAction SilentlyContinue; if ($ae) { $authoring = [bool]$ae.enabled } } catch {}
  if ($authoring) {
    $port = 80
    try {
      $b = $_.bindings.Collection | Select-Object -First 1
      if ($b -and $b.bindingInformation) { $parts = $b.bindingInformation -split ':'; if ($parts.Length -gt 1 -and $parts[1]) { $port = [int]$parts[1] } }
    } catch {}
    $anon = $false
    try { $an = Get-WebConfiguration -Filter '${ANON_AUTH_FILTER}' -PSPath "IIS:\\Sites\\$name" -ErrorAction SilentlyContinue; if ($an) { $anon = [bool]$an.enabled } } catch {}
    [PSCustomObject]@{ Name=$name; State=$_.State; PhysicalPath=$_.physicalPath; Port=$port; AuthoringEnabled=$authoring; AnonymousEnabled=$anon }
  }
}`

function mapWebdavSite(r: RawWebdavSite): Share {
  return {
    name: r.Name,
    path: r.PhysicalPath || '',
    description: '',
    protocol: 'webdav',
    type: 'Disk',
    hidden: false,
    encrypted: false,
    concurrentUsers: 0,
    status: r.State === 'Started' ? 'Enabled' : 'Disabled',
    cached: false,
    port: r.Port || 80,
    siteName: r.Name,
    anonymousEnabled: !!r.AnonymousEnabled,
    authoringEnabled: !!r.AuthoringEnabled
  }
}

// ensureWebdavSectionsUnlocked 已提取至 electron/services/webdav.ts 并导出
// （复用：服务器级配置写入与站点级 authoring 写入均需先解锁配置节）
// 通过 `import * as webdav from '../../webdav'` 调用 webdav.ensureWebdavSectionsUnlocked()

export const webdavAdapter: ProtocolAdapter = {
  protocol: 'webdav',
  capabilities: {
    supportsCreate: true,
    supportsUpdate: true,
    supportsDelete: true,
    supportsToggle: true,
    supportsPermissions: true,
    supportsSessions: false,
    supportsOpenFiles: false,
    supportsServerConfig: true,
    supportsRestart: true,
    permissionModel: 'webdav-rules'
  },

  async listShares(): Promise<Share[]> {
    // retries:0 避免未装 IIS 时无谓重试 2 次造成切 Tab 延迟
    const raw = await runPowerShell<RawWebdavSite | RawWebdavSite[]>(
      LIST_SCRIPT,
      { retries: 0 }
    )
    const arr = Array.isArray(raw) ? raw : [raw]
    return arr.filter((r) => r && r.Name).map(mapWebdavSite)
  },

  async createShare(input: CreateShareInput): Promise<Share> {
    console.log('[createShare:webdav] 适配器接收输入:', {
      name: input.name,
      path: input.path,
      port: input.port,
      anonymousEnabled: input.anonymousEnabled
    })
    if (!validateName(input.name)) throw Errors.invalidParam('站点名非法')
    if (!validatePath(input.path)) throw Errors.invalidParam('路径非法')
    const port = input.port || 80
    // 创建 Web 站点（WebDAV 寄生于 Web 站点）
    const createCmd = `New-Website -Name ${psQuote(input.name)} -PhysicalPath ${psQuote(input.path)} -Port ${port} -Force`
    console.log('[createShare:webdav] 步骤 1/4 创建 Web 站点, PowerShell 命令:', createCmd)
    try {
      await runPowerShellVoid(createCmd, { retries: 0 })
    } catch (e) {
      console.error('[createShare:webdav] 步骤 1/4 创建站点失败:', (e as Error).message)
      throw e
    }
    console.log('[createShare:webdav] 步骤 1/4 站点创建成功')
    // 启用 WebDAV authoring 并验证是否真正生效。
    // 需先解锁配置节（overrideModeDefault=Deny），再用完整 filter 路径写入。
    // 若未能启用（功能未装/解锁失败），清理孤儿站点并给出明确提示，避免 IIS 残留。
    console.log('[createShare:webdav] 步骤 2/4 启用 WebDAV authoring...')
    const authoringOk = await enableAuthoring(input.name)
    if (!authoringOk) {
      console.error('[createShare:webdav] 步骤 2/4 authoring 启用失败，清理孤儿站点...')
      // 孤儿站点清理：用 Remove-Website（WebAdministration cmdlet，自动加载模块）
      // 而非 Remove-Item 'IIS:\Sites\...'（依赖 IIS: PSDrive，-NoProfile 下未 Import 时不存在）
      await runPowerShellVoid(
        `try { Remove-Website -Name ${psQuote(input.name)} -ErrorAction Stop } catch {}`,
        { retries: 0 }
      )
      throw Errors.commandFailed(
        'WebDAV authoring 启用失败。可能原因：1) WebDAV 发布功能未安装（服务器：Web-WebDAV；客户端：IIS-WebDAV）；2) 配置节锁定且解锁失败（需管理员权限）。请确认后重试'
      )
    }
    console.log('[createShare:webdav] 步骤 2/4 authoring 启用成功')
    if (input.anonymousEnabled !== undefined) {
      const anon = psBool(input.anonymousEnabled)
      if (anon) {
        console.log('[createShare:webdav] 步骤 3/4 配置匿名访问, anonymousEnabled:', input.anonymousEnabled)
        // 写站点级 anonymousAuthentication 前需解锁配置节（overrideModeDefault=Deny）
        await webdav.ensureWebdavSectionsUnlocked()
        await runPowerShellVoid(
          `try { Set-WebConfigurationProperty -Filter '${ANON_AUTH_FILTER}' -PSPath ${iisPath(
            input.name
          )} -Name enabled -Value ${anon} -ErrorAction Stop } catch {}`,
          { retries: 0 }
        )
        console.log('[createShare:webdav] 步骤 3/4 匿名访问配置完成')
      } else {
        console.log('[createShare:webdav] 步骤 3/4 跳过匿名访问（未启用）')
      }
    } else {
      console.log('[createShare:webdav] 步骤 3/4 跳过匿名访问（未指定）')
    }
    console.log('[createShare:webdav] 步骤 4/4 读取站点信息...')
    const site = await fetchSite(input.name)
    if (!site) {
      console.error('[createShare:webdav] 步骤 4/4 站点创建后未能读取:', input.name)
      // 清理孤儿站点，避免端口占用残留
      console.log('[createShare:webdav] 清理孤儿站点...')
      await runPowerShellVoid(
        `try { Remove-Website -Name ${psQuote(input.name)} -ErrorAction Stop } catch {}`,
        { retries: 0 }
      )
      // 验证清理结果
      const stillExists = await runPowerShell<string>(
        `Get-Website | Where-Object { $_.Name -eq ${psQuote(input.name)} } | Select-Object -First 1 -ExpandProperty Name -ErrorAction SilentlyContinue`,
        { retries: 0 }
      ).catch(() => null)
      if (stillExists) {
        console.error('[createShare:webdav] 孤儿站点清理失败！站点仍存在:', input.name)
      } else {
        console.log('[createShare:webdav] 孤儿站点已确认清理:', input.name)
      }
      throw Errors.commandFailed('WebDAV 站点创建后未能读取，已自动清理孤儿站点')
    }
    console.log('[createShare:webdav] 共享创建完成:', input.name)
    return site
  },

  async deleteShare(name: string): Promise<void> {
    console.log('[deleteShare:webdav] 删除站点:', name)
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    // 用 Remove-Website（WebAdministration cmdlet，自动加载模块，-Name 接受字符串）
    // 而非 Remove-Item 'IIS:\Sites\...'（依赖 IIS: PSDrive，-NoProfile 下未 Import 时不存在）
    // try/catch 包裹：站点不存在时不抛错（幂等删除）
    const cmd = `try { Remove-Website -Name ${psQuote(name)} -ErrorAction Stop } catch {}`
    console.log('[deleteShare:webdav] PowerShell 命令:', cmd)
    try {
      await runPowerShellVoid(cmd)
      console.log('[deleteShare:webdav] 删除成功:', name)
    } catch (e) {
      console.error('[deleteShare:webdav] 删除失败:', name, (e as Error).message)
      throw e
    }
  },

  async updateShare(name: string, input: UpdateShareInput): Promise<Share> {
    console.log('[updateShare:webdav] 更新站点:', name, {
      anonymousEnabled: input.anonymousEnabled
    })
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    try {
      if (input.anonymousEnabled !== undefined) {
        const anon = psBool(input.anonymousEnabled)
        if (anon) {
          console.log('[updateShare:webdav] 配置匿名访问, anonymousEnabled:', input.anonymousEnabled)
          // 写站点级 anonymousAuthentication 前需解锁配置节（overrideModeDefault=Deny）
          await webdav.ensureWebdavSectionsUnlocked()
          await runPowerShellVoid(
            `try { Set-WebConfigurationProperty -Filter '${ANON_AUTH_FILTER}' -PSPath ${iisPath(
              name
            )} -Name enabled -Value ${anon} -ErrorAction Stop } catch {}`,
            { retries: 0 }
          )
          console.log('[updateShare:webdav] 匿名访问配置完成')
        }
      }
      console.log('[updateShare:webdav] 正在读取站点信息...')
      const site = await fetchSite(name)
      if (!site) {
        console.error('[updateShare:webdav] 站点更新后未能读取:', name)
        throw Errors.shareNotFound(name)
      }
      console.log('[updateShare:webdav] 站点更新完成:', name)
      return site
    } catch (e) {
      console.error('[updateShare:webdav] 更新失败:', name, (e as Error).message)
      throw e
    }
  },

  async toggleShare(name: string, enabled: boolean): Promise<void> {
    console.log('[toggleShare:webdav] 切换站点状态:', name, '→', enabled ? '启用' : '禁用')
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    // 先检查站点是否存在，不存在则抛错（不再静默吞错返回假成功）
    const exists = await runPowerShell<string>(
      `Get-Website | Where-Object { $_.Name -eq ${psQuote(name)} } | Select-Object -First 1 -ExpandProperty Name -ErrorAction SilentlyContinue`,
      { retries: 0 }
    ).catch(() => null)
    if (!exists) {
      console.error('[toggleShare:webdav] 站点不存在:', name)
      throw Errors.shareNotFound(name)
    }
    // 用 Start-Website / Stop-Website（WebAdministration cmdlet，自动加载模块）
    // 而非 Get-Item 'IIS:\Sites\...' + .Start()/.Stop()（依赖 IIS: PSDrive）
    // try/catch 容忍"已在目标状态"的非致命错误（如站点已启动时再 Start）
    const action = enabled ? 'Start-Website' : 'Stop-Website'
    const cmd = `try { ${action} -Name ${psQuote(name)} -ErrorAction Stop } catch {}`
    console.log('[toggleShare:webdav] PowerShell 命令:', cmd)
    try {
      await runPowerShellVoid(cmd)
      console.log('[toggleShare:webdav] 切换成功:', name, '→', enabled ? '启用' : '禁用')
    } catch (e) {
      console.error('[toggleShare:webdav] 切换失败:', name, (e as Error).message)
      throw e
    }
  },

  async getPermissions(name: string): Promise<SharePermission[]> {
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    const raw = await runPowerShell<RawWebdavRule | RawWebdavRule[]>(
      `Get-WebConfiguration -Filter '${AUTHORING_RULES_FILTER}/*' -PSPath ${iisPath(
        name
      )} -ErrorAction SilentlyContinue`,
      { retries: 0 }
    )
    const arr = (Array.isArray(raw) ? raw : [raw]).filter((r) => r && (r.Users || r.Roles))
    return arr.map((r) => {
      const isGroup = !!r.Roles && !r.Users
      const access = String(r.Access || '').toLowerCase()
      let mapped: SharePermission['access'] = 'Read'
      if (access.includes('source')) mapped = 'Full'
      else if (access.includes('write')) mapped = 'Change'
      else mapped = 'Read'
      return {
        shareName: name,
        account: r.Users || r.Roles || '*',
        accountType: isGroup ? 'Group' : 'User',
        access: mapped,
        deny: false
      }
    })
  },

  async setPermissions(name: string, perms: SharePermission[]): Promise<void> {
    console.log('[setPermissions:webdav] 设置权限:', name, { 权限条数: perms.length, 权限: perms.map(p => `${p.account}=${p.access}`) })
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    // authoringRules 配置节默认锁定，需先解锁（复用 webdav.ts 的统一解锁）
    await webdav.ensureWebdavSectionsUnlocked()
    // 事务补偿：先备份当前权限，若后续授予中途失败则回滚到原状态
    const backup = this.getPermissions ? await this.getPermissions(name).catch(() => [] as SharePermission[]) : []
    console.log('[setPermissions:webdav] 已备份当前权限:', backup.length, '条 →', backup.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
    console.log('[setPermissions:webdav] 清空已有授权规则...')
    await runPowerShellVoid(
      `Clear-WebConfiguration -Filter '${AUTHORING_RULES_FILTER}' -PSPath ${iisPath(
        name
      )} -ErrorAction SilentlyContinue`,
      { retries: 0 }
    )
    // 逐个授予，收集失败项（不再静默吞错）
    const failed: string[] = []
    for (const p of perms) {
      const bits: string[] = []
      if (p.access === 'Read' || p.access === 'Change' || p.access === 'Full') bits.push('Read')
      if (p.access === 'Change' || p.access === 'Full') bits.push('Write')
      if (p.access === 'Full') bits.push('Source')
      const access = bits.join(',')
      // 用户授权用 users，组授权用 roles
      const userField = p.accountType === 'Group' ? `roles=${psQuote(p.account)}` : `users=${psQuote(p.account)}`
      const cmd = `Add-WebConfiguration -Filter '${AUTHORING_RULES_FILTER}' -PSPath ${iisPath(name)} -Value @{${userField};path='*';access='${access}'} -ErrorAction Stop`
      console.log('[setPermissions:webdav] 授予权限, PowerShell 命令:', cmd)
      try {
        await runPowerShellVoid(cmd, { retries: 0 })
      } catch (e) {
        console.error('[setPermissions:webdav] 授予失败:', p.account, (e as Error).message)
        failed.push(p.account)
      }
    }
    // 若有失败项：回滚到备份状态
    if (failed.length > 0) {
      console.error('[setPermissions:webdav] 回滚触发！失败账号:', failed.join(', '))
      // 查询回滚前的当前权限状态（部分授予后的残留状态）
      const beforeRollback = this.getPermissions ? await this.getPermissions(name).catch(() => [] as SharePermission[]) : []
      console.log('[setPermissions:webdav] 回滚前权限状态:', beforeRollback.length, '条 →', beforeRollback.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
      await runPowerShellVoid(
        `Clear-WebConfiguration -Filter '${AUTHORING_RULES_FILTER}' -PSPath ${iisPath(
          name
        )} -ErrorAction SilentlyContinue`,
        { retries: 0 }
      )
      for (const p of backup) {
        const bits: string[] = []
        if (p.access === 'Read' || p.access === 'Change' || p.access === 'Full') bits.push('Read')
        if (p.access === 'Change' || p.access === 'Full') bits.push('Write')
        if (p.access === 'Full') bits.push('Source')
        const access = bits.join(',')
        const userField = p.accountType === 'Group' ? `roles=${psQuote(p.account)}` : `users=${psQuote(p.account)}`
        await runPowerShellVoid(
          `Add-WebConfiguration -Filter '${AUTHORING_RULES_FILTER}' -PSPath ${iisPath(name)} -Value @{${userField};path='*';access='${access}'} -ErrorAction SilentlyContinue`,
          { retries: 0 }
        ).catch(() => {})
      }
      // 查询回滚后的权限状态，验证是否恢复成功
      const afterRollback = this.getPermissions ? await this.getPermissions(name).catch(() => [] as SharePermission[]) : []
      console.log('[setPermissions:webdav] 回滚后权限状态:', afterRollback.length, '条 →', afterRollback.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
      throw Errors.commandFailed(`部分权限授予失败（${failed.join(', ')}），已回滚到原始状态`)
    }
    console.log('[setPermissions:webdav] 权限设置成功:', name)
  },

  // WebDAV 无原生会话 API（capabilities.supportsSessions=false）
  async getServiceStatus(): Promise<ServiceStatus> {
    return webdav.getServiceStatus()
  },

  async restartService(): Promise<void> {
    console.log('[restartService:webdav] 重启 WebDAV 服务...')
    try {
      await webdav.restartService()
      console.log('[restartService:webdav] 服务重启成功')
    } catch (e) {
      console.error('[restartService:webdav] 服务重启失败:', (e as Error).message)
      throw e
    }
  },

  async getConfig(): Promise<WebdavServerConfig> {
    return webdav.getConfig()
  },

  async setConfig(config: Partial<WebdavServerConfig>): Promise<void> {
    console.log('[setConfig:webdav] 设置服务器配置:', Object.keys(config))
    try {
      await webdav.setConfig(config)
      console.log('[setConfig:webdav] 配置设置成功')
    } catch (e) {
      console.error('[setConfig:webdav] 配置设置失败:', (e as Error).message)
      throw e
    }
  },

  defaultConfig(): WebdavServerConfig {
    return webdav.defaultConfig()
  },

  async restoreDefault(): Promise<WebdavServerConfig> {
    return webdav.restoreDefault()
  }
}

// 启用 WebDAV authoring 并验证是否真正生效
// 返回 false 表示未能启用（功能未装 / 配置节锁定且解锁失败 / 写入失败）
async function enableAuthoring(name: string): Promise<boolean> {
  // 先解锁配置节（overrideModeDefault=Deny）——复用 webdav.ts 的统一解锁
  await webdav.ensureWebdavSectionsUnlocked()
  try {
    const ok = await runPowerShell<boolean>(
      `try { Set-WebConfigurationProperty -Filter '${AUTHORING_FILTER}' -PSPath ${iisPath(name)} -Name enabled -Value $true -ErrorAction Stop } catch {}; $v = $false; try { $ae = Get-WebConfiguration -Filter '${AUTHORING_FILTER}' -PSPath ${iisPath(name)} -ErrorAction SilentlyContinue; if ($ae) { $v = [bool]$ae.enabled } } catch {}; $v`
    )
    return !!ok
  } catch {
    return false
  }
}

// 读取单个 WebDAV 站点
// 用 Get-Website | Where-Object 过滤，避免 -Name 找不到时抛终止性错误导致读取失败
async function fetchSite(name: string): Promise<Share | null> {
  try {
    const raw = await runPowerShell<RawWebdavSite | RawWebdavSite[]>(
      `Import-Module WebAdministration; Get-Website -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq ${psQuote(name)} } | ForEach-Object {
        $n = $_.Name
        $port = 80
        try { $b = $_.bindings.Collection | Select-Object -First 1; if ($b.bindingInformation) { $parts = $b.bindingInformation -split ':'; if ($parts.Length -gt 1 -and $parts[1]) { $port = [int]$parts[1] } } } catch {}
        $authoring = $false
        try { $ae = Get-WebConfiguration -Filter '${AUTHORING_FILTER}' -PSPath "IIS:\\Sites\\$n" -ErrorAction SilentlyContinue; if ($ae) { $authoring = [bool]$ae.enabled } } catch {}
        $anon = $false
        try { $an = Get-WebConfiguration -Filter '${ANON_AUTH_FILTER}' -PSPath "IIS:\\Sites\\$n" -ErrorAction SilentlyContinue; if ($an) { $anon = [bool]$an.enabled } } catch {}
        [PSCustomObject]@{ Name=$n; State=$_.State; PhysicalPath=$_.physicalPath; Port=$port; AuthoringEnabled=$authoring; AnonymousEnabled=$anon }
      }`,
      { retries: 0 }
    )
    const arr = Array.isArray(raw) ? raw : [raw]
    const r = arr.find((x) => x && x.Name)
    if (!r) return null
    return mapWebdavSite(r)
  } catch {
    return null
  }
}
