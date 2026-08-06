import type { ProtocolAdapter } from '../ProtocolAdapter'
import type {
  Share,
  SharePermission,
  ServiceStatus,
  CreateShareInput,
  UpdateShareInput
} from '../../../types'
import { runPowerShell, runPowerShellVoid, psQuote, validateName, validatePath } from '../../../lib/powershell'
import { Errors } from '../../../lib/errors'
import * as ftp from '../../ftp'

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

// 读取所有 FTP 站点详情（名称/状态/物理路径/端口/SSL/认证）
const LIST_SCRIPT = `Get-WebFtpSite | ForEach-Object {
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
  const sslPolicy = (r.SslPolicy as Share['sslPolicy']) || undefined
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
    supportsServerConfig: false,
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
    if (!validateName(input.name)) throw Errors.invalidParam('站点名非法')
    if (!validatePath(input.path)) throw Errors.invalidParam('路径非法')
    const port = input.port || 21
    // 创建 FTP 站点
    await runPowerShellVoid(
      `New-WebFtpSite -Name ${psQuote(input.name)} -PhysicalPath ${psQuote(input.path)} -Port ${port} -Force`
    )
    // 端口冲突由 New-WebFtpSite 抛错；成功后配置 SSL 与认证
    await applyFtpConfig(input.name, input)
    const site = await fetchSite(input.name)
    if (!site) throw Errors.commandFailed('FTP 站点创建后未能读取')
    return site
  },

  async deleteShare(name: string): Promise<void> {
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    // Remove-Item 经 IIS provider 可靠移除站点及其子项
    await runPowerShellVoid(`Remove-Item "IIS:\\Sites\\${psQuote(name)}" -Recurse -Force -ErrorAction SilentlyContinue`)
  },

  async updateShare(name: string, input: UpdateShareInput): Promise<Share> {
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    await applyFtpConfig(name, input)
    const site = await fetchSite(name)
    if (!site) throw Errors.shareNotFound(name)
    return site
  },

  async toggleShare(name: string, enabled: boolean): Promise<void> {
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    // 经 IIS provider 调用 Start()/Stop()（对 Web/FTP 站点均可靠）
    const want = enabled ? 'Start' : 'Stop'
    await runPowerShellVoid(
      `$s = Get-Item "IIS:\\Sites\\${psQuote(name)}" -ErrorAction Stop; if ('$want' -eq 'Start' -and $s.State -ne 'Started') { $s.Start() } elseif ('$want' -eq 'Stop' -and $s.State -eq 'Started') { $s.Stop() }`
    )
  },

  async getPermissions(name: string): Promise<SharePermission[]> {
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    const raw = await runPowerShell<RawFtpAuthRule | RawFtpAuthRule[]>(
      `Get-WebConfiguration -Filter 'ftpServer/security/authorization/*' -PSPath "IIS:\\Sites\\${psQuote(name)}" -ErrorAction SilentlyContinue`
    )
    const arr = (Array.isArray(raw) ? raw : [raw]).filter((r) => r && (r.Users || r.Roles))
    return arr.map((r) => {
      const isGroup = !!r.Roles && !r.Users
      let access: SharePermission['access'] = 'Read'
      const perm = String(r.Permissions || '').toLowerCase()
      if (perm.includes('write') && perm.includes('read')) access = 'Change'
      else if (perm.includes('write')) access = 'Change'
      else if (perm.includes('read')) access = 'Read'
      if (perm.includes('read') && perm.includes('write')) access = 'Change'
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
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    // 先清空已有授权规则，再按传入列表重建
    await runPowerShellVoid(
      `Clear-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath "IIS:\\Sites\\${psQuote(name)}" -ErrorAction SilentlyContinue`
    )
    for (const p of perms) {
      const accessType = p.deny ? 'Deny' : 'Allow'
      const permBits: string[] = []
      if (p.access === 'Read' || p.access === 'Full' || p.access === 'Change') permBits.push('Read')
      if (p.access === 'Change' || p.access === 'Full') permBits.push('Write')
      const permissions = permBits.join(',')
      // 用户授权用 users，组授权用 roles
      if (p.accountType === 'Group') {
        await runPowerShellVoid(
          `Add-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath "IIS:\\Sites\\${psQuote(
            name
          )}" -Value @{accessType='${accessType}';roles=${psQuote(p.account)};permissions='${permissions}'} -ErrorAction SilentlyContinue`
        )
      } else {
        await runPowerShellVoid(
          `Add-WebConfiguration -Filter 'ftpServer/security/authorization' -PSPath "IIS:\\Sites\\${psQuote(
            name
          )}" -Value @{accessType='${accessType}';users=${psQuote(p.account)};permissions='${permissions}'} -ErrorAction SilentlyContinue`
        )
      }
    }
  },

  // FTP 无原生会话 API（capabilities.supportsSessions=false），不实现 listSessions
  async getServiceStatus(): Promise<ServiceStatus> {
    return ftp.getServiceStatus()
  },

  async restartService(): Promise<void> {
    return ftp.restartService()
  }
}

// 读取单个 FTP 站点
async function fetchSite(name: string): Promise<Share | null> {
  try {
    const raw = await runPowerShell<RawFtpSite>(
      `Get-WebFtpSite -Name ${psQuote(name)} | ForEach-Object {
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
      }`
    )
    if (!raw || !raw.Name) return null
    return mapFtpSite(raw)
  } catch {
    return null
  }
}

// 应用 SSL 策略与认证模式配置
async function applyFtpConfig(name: string, input: { sslPolicy?: Share['sslPolicy']; authMode?: Share['authMode'] }): Promise<void> {
  const pspath = `"IIS:\\Sites\\${psQuote(name)}"`
  if (input.sslPolicy) {
    const policy = String(input.sslPolicy)
    // 同时设置控制通道与数据通道策略
    await runPowerShellVoid(
      `Set-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath ${pspath} -Name controlChannelPolicy -Value '${policy}' -ErrorAction SilentlyContinue; Set-WebConfigurationProperty -Filter 'ftpServer/security/ssl' -PSPath ${pspath} -Name dataChannelPolicy -Value '${policy}' -ErrorAction SilentlyContinue`
    )
  }
  if (input.authMode) {
    // 启用/禁用匿名与基本认证
    const anonEnabled = input.authMode === 'anonymous'
    const basicEnabled = input.authMode === 'basic' || input.authMode === 'windows'
    await runPowerShellVoid(
      `Set-WebConfigurationProperty -Filter 'ftpServer/security/authentication/anonymousAuthentication' -PSPath ${pspath} -Name enabled -Value $${
        anonEnabled ? 'true' : 'false'
      } -ErrorAction SilentlyContinue; Set-WebConfigurationProperty -Filter 'ftpServer/security/authentication/basicAuthentication' -PSPath ${pspath} -Name enabled -Value $${
        basicEnabled ? 'true' : 'false'
      } -ErrorAction SilentlyContinue`
    )
  }
  void normalizeSsl
}
