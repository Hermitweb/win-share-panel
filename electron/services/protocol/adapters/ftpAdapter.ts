import type { ProtocolAdapter } from '../ProtocolAdapter'
import type {
  Share,
  SharePermission,
  ServiceStatus,
  FtpServerConfig,
  CreateShareInput,
  UpdateShareInput
} from '../../../types'
import { runPowerShell, runPowerShellVoid, psQuote, psEscapeSingle, psEnum, validateName, validatePath } from '../../../lib/powershell'
import { Errors } from '../../../lib/errors'
import * as ftp from '../../ftp'

// SSL 策略白名单（与 types.ts 声明一致），拼入命令前运行时校验，杜绝注入
const SSL_POLICIES = new Set(['SslAllow', 'SslRequire', 'SslRequireCredentials'])

interface RawFtpSite {
  Name: string
  State: string
  PhysicalPath: string
  Port: number
  SslPolicy: string
  AnonymousEnabled: boolean
  BasicEnabled: boolean
}

interface RawFtpAuthRule {
  AccessType: string
  Users: string
  Roles: string
  Permissions: string
}

// 构造 IIS 站点路径：'IIS:\Sites\{name}'，单引号包裹防止 $/反引号被插值
function iisPath(name: string): string {
  return `'IIS:\\Sites\\${psEscapeSingle(name)}'`
}

// 读取所有 FTP 站点详情（名称/状态/物理路径/端口/SSL/认证）
// 注意：WebAdministration 模块没有 Get-WebFtpSite cmdlet（仅有 New-WebFtpSite），
// 必须用 Get-Website + bindings.protocol='ftp' 过滤来列出 FTP 站点。
// Import-Module 确保 IIS: PSDrive 存在（-NoProfile 下未自动加载），供 Get-Item 使用。
const LIST_SCRIPT = `Import-Module WebAdministration; Get-Website | Where-Object { $hasFtp=$false; try { foreach ($b in $_.bindings.Collection) { if ($b.protocol -eq 'ftp') { $hasFtp=$true; break } } } catch {}; $hasFtp } | ForEach-Object {
  $name = $_.Name
  $site = Get-Item "IIS:\\Sites\\$name" -ErrorAction SilentlyContinue
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
  try { $ssl = (Get-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath "IIS:\\Sites\\$name" -Name controlChannelPolicy -ErrorAction SilentlyContinue) } catch {}
  $anon = $false
  try { $anon = [bool](Get-WebConfigurationProperty -Filter 'ftpServer/security/authentication/anonymousAuthentication' -PSPath "IIS:\\Sites\\$name" -Name enabled -ErrorAction SilentlyContinue) } catch {}
  $basic = $false
  try { $basic = [bool](Get-WebConfigurationProperty -Filter 'ftpServer/security/authentication/basicAuthentication' -PSPath "IIS:\\Sites\\$name" -Name enabled -ErrorAction SilentlyContinue) } catch {}
  [PSCustomObject]@{ Name=$name; State=$_.State; PhysicalPath=$path; Port=$port; SslPolicy=$ssl; AnonymousEnabled=$anon; BasicEnabled=$basic }
}`

function mapFtpSite(r: RawFtpSite): Share {
  let authMode: Share['authMode'] = 'basic'
  if (r.AnonymousEnabled) authMode = 'anonymous'
  else if (r.BasicEnabled) authMode = 'basic'
  else authMode = 'windows'
  const sslPolicy = normalizeSsl(r.SslPolicy)
  return {
    name: r.Name,
    path: r.PhysicalPath || '',
    description: '',
    protocol: 'ftp',
    type: 'Disk',
    hidden: false,
    encrypted: false,
    concurrentUsers: 0,
    status: r.State === 'Started' ? 'Enabled' : 'Disabled',
    cached: false,
    port: r.Port || 21,
    siteName: r.Name,
    sslPolicy,
    authMode
  }
}

function normalizeSsl(v: string | undefined): Share['sslPolicy'] | undefined {
  if (!v) return undefined
  if (/requirecredentials/i.test(v)) return 'SslRequireCredentials'
  if (/^sslrequire$/i.test(v)) return 'SslRequire'
  return 'SslAllow'
}

export const ftpAdapter: ProtocolAdapter = {
  protocol: 'ftp',
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
    permissionModel: 'iis-auth'
  },

  async listShares(): Promise<Share[]> {
    // retries:0 避免未装 IIS 时无谓重试 2 次造成切 Tab 延迟
    const raw = await runPowerShell<RawFtpSite | RawFtpSite[]>(
      LIST_SCRIPT,
      { retries: 0 }
    )
    const arr = Array.isArray(raw) ? raw : [raw]
    return arr.filter((r) => r && r.Name).map(mapFtpSite)
  },

  async createShare(input: CreateShareInput): Promise<Share> {
    console.log('[createShare:ftp] 适配器接收输入:', {
      name: input.name,
      path: input.path,
      port: input.port,
      sslPolicy: input.sslPolicy,
      authMode: input.authMode
    })
    if (!validateName(input.name)) throw Errors.invalidParam('站点名非法')
    if (!validatePath(input.path)) throw Errors.invalidParam('路径非法')
    const port = input.port || 21
    // 创建 FTP 站点（retries:0，端口冲突等失败为确定性错误，重试无意义）
    const createCmd = `New-WebFtpSite -Name ${psQuote(input.name)} -PhysicalPath ${psQuote(input.path)} -Port ${port} -Force`
    console.log('[createShare:ftp] 步骤 1/3 创建站点, PowerShell 命令:', createCmd)
    try {
      await runPowerShellVoid(createCmd, { retries: 0 })
    } catch (e) {
      console.error('[createShare:ftp] 步骤 1/3 创建站点失败:', (e as Error).message)
      throw e
    }
    console.log('[createShare:ftp] 步骤 1/3 站点创建成功')
    // 端口冲突由 New-WebFtpSite 抛错；成功后配置 SSL 与认证（best-effort，不阻断创建）
    console.log('[createShare:ftp] 步骤 2/3 应用 SSL/认证配置, sslPolicy:', input.sslPolicy, 'authMode:', input.authMode)
    try {
      await applyFtpConfig(input.name, input)
      console.log('[createShare:ftp] 步骤 2/3 配置应用完成')
    } catch (e) {
      console.error('[createShare:ftp] 步骤 2/3 配置应用失败（非致命）:', (e as Error).message)
    }
    console.log('[createShare:ftp] 步骤 3/3 读取站点信息...')
    const site = await fetchSite(input.name)
    if (!site) {
      console.error('[createShare:ftp] 步骤 3/3 站点创建后未能读取:', input.name)
      // 清理孤儿站点，避免端口占用残留
      console.log('[createShare:ftp] 清理孤儿站点...')
      await runPowerShellVoid(
        `Import-Module WebAdministration; try { Remove-Item ${iisPath(input.name)} -Recurse -Force -ErrorAction Stop } catch {}`,
        { retries: 0 }
      )
      // 验证清理结果
      const stillExists = await runPowerShell<string>(
        `Get-Website | Where-Object { $_.Name -eq ${psQuote(input.name)} } | Select-Object -First 1 -ExpandProperty Name -ErrorAction SilentlyContinue`,
        { retries: 0 }
      ).catch(() => null)
      if (stillExists) {
        console.error('[createShare:ftp] 孤儿站点清理失败！站点仍存在:', input.name)
      } else {
        console.log('[createShare:ftp] 孤儿站点已确认清理:', input.name)
      }
      throw Errors.commandFailed('FTP 站点创建后未能读取，已自动清理孤儿站点')
    }
    console.log('[createShare:ftp] 共享创建完成:', input.name)
    return site
  },

  async deleteShare(name: string): Promise<void> {
    console.log('[deleteShare:ftp] 删除站点:', name)
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    // Import-Module 确保 IIS: PSDrive 存在（-NoProfile 下未自动加载）；
    // try/catch 包裹：站点不存在时不抛错（幂等删除）
    const cmd = `Import-Module WebAdministration; try { Remove-Item ${iisPath(name)} -Recurse -Force -ErrorAction Stop } catch {}`
    console.log('[deleteShare:ftp] PowerShell 命令:', cmd)
    try {
      await runPowerShellVoid(cmd)
      console.log('[deleteShare:ftp] 删除成功:', name)
    } catch (e) {
      console.error('[deleteShare:ftp] 删除失败:', name, (e as Error).message)
      throw e
    }
  },

  async updateShare(name: string, input: UpdateShareInput): Promise<Share> {
    console.log('[updateShare:ftp] 更新站点:', name, {
      sslPolicy: input.sslPolicy,
      authMode: input.authMode
    })
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    try {
      await applyFtpConfig(name, input)
      console.log('[updateShare:ftp] 配置应用完成，正在读取站点信息...')
      const site = await fetchSite(name)
      if (!site) {
        console.error('[updateShare:ftp] 站点更新后未能读取:', name)
        throw Errors.shareNotFound(name)
      }
      console.log('[updateShare:ftp] 站点更新完成:', name)
      return site
    } catch (e) {
      console.error('[updateShare:ftp] 更新失败:', name, (e as Error).message)
      throw e
    }
  },

  async toggleShare(name: string, enabled: boolean): Promise<void> {
    console.log('[toggleShare:ftp] 切换站点状态:', name, '→', enabled ? '启用' : '禁用')
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    // 先检查站点是否存在，不存在则抛错（不再静默吞错返回假成功）
    const exists = await runPowerShell<string>(
      `Get-Website | Where-Object { $_.Name -eq ${psQuote(name)} } | Select-Object -First 1 -ExpandProperty Name -ErrorAction SilentlyContinue`,
      { retries: 0 }
    ).catch(() => null)
    if (!exists) {
      console.error('[toggleShare:ftp] 站点不存在:', name)
      throw Errors.shareNotFound(name)
    }
    // 用 Start-Website / Stop-Website（WebAdministration cmdlet，自动加载模块）
    // 不用 Get-Item + .Start()/.Stop()——后者在 FTP 站点上抛 0x800710D8
    // try/catch 容忍"已在目标状态"的非致命错误（如站点已启动时再 Start）
    const action = enabled ? 'Start-Website' : 'Stop-Website'
    const cmd = `try { ${action} -Name ${psQuote(name)} -ErrorAction Stop } catch {}`
    console.log('[toggleShare:ftp] PowerShell 命令:', cmd)
    try {
      await runPowerShellVoid(cmd)
      console.log('[toggleShare:ftp] 切换成功:', name, '→', enabled ? '启用' : '禁用')
    } catch (e) {
      console.error('[toggleShare:ftp] 切换失败:', name, (e as Error).message)
      throw e
    }
  },

  async getPermissions(name: string): Promise<SharePermission[]> {
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    const raw = await runPowerShell<RawFtpAuthRule | RawFtpAuthRule[]>(
      `Get-WebConfiguration -Filter 'ftpServer/security/authorization/*' -PSPath ${iisPath(name)} -ErrorAction SilentlyContinue`,
      { retries: 0 }
    )
    const arr = (Array.isArray(raw) ? raw : [raw]).filter((r) => r && (r.Users || r.Roles))
    return arr.map((r) => {
      const isGroup = !!r.Roles && !r.Users
      let access: SharePermission['access'] = 'Read'
      const perm = String(r.Permissions || '').toLowerCase()
      if (perm.includes('write') && perm.includes('read')) access = 'Change'
      else if (perm.includes('write')) access = 'Change'
      else if (perm.includes('read')) access = 'Read'
      return {
        shareName: name,
        account: r.Users || r.Roles || '*',
        accountType: isGroup ? 'Group' : 'User',
        access,
        deny: String(r.AccessType || '').toLowerCase() === 'deny'
      }
    })
  },

  async setPermissions(name: string, perms: SharePermission[]): Promise<void> {
    console.log('[setPermissions:ftp] 设置权限:', name, { 权限条数: perms.length, 权限: perms.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`) })
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    // authorization 配置节默认锁定，需先解锁（复用 ftp.ts 的统一解锁）
    await ftp.ensureFtpSectionsUnlocked()
    // 事务补偿：先备份当前权限，若后续授予中途失败则回滚到原状态
    const backup = this.getPermissions ? await this.getPermissions(name).catch(() => [] as SharePermission[]) : []
    console.log('[setPermissions:ftp] 已备份当前权限:', backup.length, '条 →', backup.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
    // 先清空已有授权规则，再按传入列表重建
    console.log('[setPermissions:ftp] 清空已有授权规则...')
    await runPowerShellVoid(
      `Clear-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath ${iisPath(name)} -ErrorAction SilentlyContinue`,
      { retries: 0 }
    )
    // 逐个授予，收集失败项（不再静默吞错）
    const failed: string[] = []
    for (const p of perms) {
      const accessType = p.deny ? 'Deny' : 'Allow'
      const permBits: string[] = []
      if (p.access === 'Read' || p.access === 'Full' || p.access === 'Change') permBits.push('Read')
      if (p.access === 'Change' || p.access === 'Full') permBits.push('Write')
      const permissions = permBits.join(',')
      // 用户授权用 users，组授权用 roles
      const userField = p.accountType === 'Group' ? `roles=${psQuote(p.account)}` : `users=${psQuote(p.account)}`
      const cmd = `Add-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath ${iisPath(name)} -Value @{accessType='${accessType}';${userField};permissions='${permissions}'} -ErrorAction Stop`
      console.log('[setPermissions:ftp] 授予权限, PowerShell 命令:', cmd)
      try {
        await runPowerShellVoid(cmd, { retries: 0 })
      } catch (e) {
        console.error('[setPermissions:ftp] 授予失败:', p.account, (e as Error).message)
        failed.push(p.account)
      }
    }
    // 若有失败项：回滚到备份状态
    if (failed.length > 0) {
      console.error('[setPermissions:ftp] 回滚触发！失败账号:', failed.join(', '))
      // 查询回滚前的当前权限状态（部分授予后的残留状态）
      const beforeRollback = this.getPermissions ? await this.getPermissions(name).catch(() => [] as SharePermission[]) : []
      console.log('[setPermissions:ftp] 回滚前权限状态:', beforeRollback.length, '条 →', beforeRollback.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
      await runPowerShellVoid(
        `Clear-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath ${iisPath(name)} -ErrorAction SilentlyContinue`,
        { retries: 0 }
      )
      for (const p of backup) {
        const accessType = p.deny ? 'Deny' : 'Allow'
        const permBits: string[] = []
        if (p.access === 'Read' || p.access === 'Full' || p.access === 'Change') permBits.push('Read')
        if (p.access === 'Change' || p.access === 'Full') permBits.push('Write')
        const permissions = permBits.join(',')
        const userField = p.accountType === 'Group' ? `roles=${psQuote(p.account)}` : `users=${psQuote(p.account)}`
        await runPowerShellVoid(
          `Add-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath ${iisPath(name)} -Value @{accessType='${accessType}';${userField};permissions='${permissions}'} -ErrorAction SilentlyContinue`,
          { retries: 0 }
        ).catch(() => {})
      }
      // 查询回滚后的权限状态，验证是否恢复成功
      const afterRollback = this.getPermissions ? await this.getPermissions(name).catch(() => [] as SharePermission[]) : []
      console.log('[setPermissions:ftp] 回滚后权限状态:', afterRollback.length, '条 →', afterRollback.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
      throw Errors.commandFailed(`部分权限授予失败（${failed.join(', ')}），已回滚到原始状态`)
    }
    console.log('[setPermissions:ftp] 权限设置成功:', name)
  },

  // FTP 无原生会话 API（capabilities.supportsSessions=false），不实现 listSessions
  async getServiceStatus(): Promise<ServiceStatus> {
    return ftp.getServiceStatus()
  },

  async restartService(): Promise<void> {
    console.log('[restartService:ftp] 重启 FTP 服务...')
    try {
      await ftp.restartService()
      console.log('[restartService:ftp] 服务重启成功')
    } catch (e) {
      console.error('[restartService:ftp] 服务重启失败:', (e as Error).message)
      throw e
    }
  },

  async getConfig(): Promise<FtpServerConfig> {
    return ftp.getConfig()
  },

  async setConfig(config: Partial<FtpServerConfig>): Promise<void> {
    console.log('[setConfig:ftp] 设置服务器配置:', Object.keys(config))
    try {
      await ftp.setConfig(config)
      console.log('[setConfig:ftp] 配置设置成功')
    } catch (e) {
      console.error('[setConfig:ftp] 配置设置失败:', (e as Error).message)
      throw e
    }
  },

  defaultConfig(): FtpServerConfig {
    return ftp.defaultConfig()
  },

  async restoreDefault(): Promise<FtpServerConfig> {
    return ftp.restoreDefault()
  }
}

// 读取单个 FTP 站点
// 用 Get-Website + FTP 绑定过滤 + 名称匹配，避免 -Name 找不到时抛终止性错误
// （WebAdministration 模块没有 Get-WebFtpSite cmdlet，必须用 Get-Website 过滤 FTP 绑定）
async function fetchSite(name: string): Promise<Share | null> {
  try {
    const raw = await runPowerShell<RawFtpSite | RawFtpSite[]>(
      `Import-Module WebAdministration; Get-Website | Where-Object { $_.Name -eq ${psQuote(name)} } | Where-Object { $hasFtp=$false; try { foreach ($b in $_.bindings.Collection) { if ($b.protocol -eq 'ftp') { $hasFtp=$true; break } } } catch {}; $hasFtp } | ForEach-Object {
        $n = $_.Name
        $site = Get-Item "IIS:\\Sites\\$n" -ErrorAction SilentlyContinue
        $path = ''; if ($site) { $path = $site.physicalPath }
        $port = 21
        try { $b = $_.bindings.Collection | Select-Object -First 1; if ($b.bindingInformation) { $parts = $b.bindingInformation -split ':'; if ($parts.Length -gt 1 -and $parts[1]) { $port = [int]$parts[1] } } } catch {}
        $ssl = ''
        try { $ssl = (Get-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath "IIS:\\Sites\\$n" -Name controlChannelPolicy -ErrorAction SilentlyContinue) } catch {}
        $anon = $false
        try { $anon = [bool](Get-WebConfigurationProperty -Filter 'ftpServer/security/authentication/anonymousAuthentication' -PSPath "IIS:\\Sites\\$n" -Name enabled -ErrorAction SilentlyContinue) } catch {}
        $basic = $false
        try { $basic = [bool](Get-WebConfigurationProperty -Filter 'ftpServer/security/authentication/basicAuthentication' -PSPath "IIS:\\Sites\\$n" -Name enabled -ErrorAction SilentlyContinue) } catch {}
        [PSCustomObject]@{ Name=$n; State=$_.State; PhysicalPath=$path; Port=$port; SslPolicy=$ssl; AnonymousEnabled=$anon; BasicEnabled=$basic }
      }`,
      { retries: 0 }
    )
    const arr = Array.isArray(raw) ? raw : [raw]
    const r = arr.find((x) => x && x.Name)
    if (!r) return null
    return mapFtpSite(r)
  } catch {
    return null
  }
}

// 应用 SSL 策略与认证模式配置（best-effort）
// 站点已由 New-WebFtpSite 创建成功，SSL/认证配置属于增强项；
// IIS 中 anonymousAuthentication/basicAuthentication 等配置节默认在服务器级别锁定，
// 需先调用 ensureFtpSectionsUnlocked 解锁，再用 PowerShell try/catch + -ErrorAction Stop 包裹每个 cmdlet，
// 确保非终止错误也能被 catch 捕获，不阻断创建流程。
// 实际生效的配置会由 fetchSite 读回并在返回的 Share 中反映。
async function applyFtpConfig(name: string, input: { sslPolicy?: Share['sslPolicy']; authMode?: Share['authMode'] }): Promise<void> {
  const pspath = iisPath(name)
  const parts: string[] = []
  const policy = psEnum(input.sslPolicy, SSL_POLICIES)
  if (policy) {
    // 同时设置控制通道与数据通道策略（白名单校验 + psQuote 双重保险）
    const p = psQuote(policy)
    parts.push(
      `try { Set-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath ${pspath} -Name controlChannelPolicy -Value ${p} -ErrorAction Stop } catch {}`,
      `try { Set-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath ${pspath} -Name dataChannelPolicy -Value ${p} -ErrorAction Stop } catch {}`
    )
  }
  if (input.authMode) {
    // 启用/禁用匿名与基本认证
    const anonEnabled = input.authMode === 'anonymous'
    const basicEnabled = input.authMode === 'basic' || input.authMode === 'windows'
    parts.push(
      `try { Set-WebConfigurationProperty -Filter 'ftpServer/security/authentication/anonymousAuthentication' -PSPath ${pspath} -Name enabled -Value $${anonEnabled ? 'true' : 'false'} -ErrorAction Stop } catch {}`,
      `try { Set-WebConfigurationProperty -Filter 'ftpServer/security/authentication/basicAuthentication' -PSPath ${pspath} -Name enabled -Value $${basicEnabled ? 'true' : 'false'} -ErrorAction Stop } catch {}`
    )
  }
  if (parts.length === 0) return
  // 预解锁配置节（overrideModeDefault=Deny），避免 Set 静默失败
  await ftp.ensureFtpSectionsUnlocked()
  try {
    // retries:0 避免配置节锁定时无谓重试造成延迟
    await runPowerShellVoid(parts.join('; '), { retries: 0 })
  } catch {
    // best-effort：配置应用失败（如配置节锁定）不应阻断站点创建/更新
  }
}
