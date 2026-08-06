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

// 列出所有 WebDAV（authoring 已启用）的 IIS Web 站点
const LIST_SCRIPT = `Get-Website | ForEach-Object {
  $name = $_.Name
  $authoring = $false
  try { $authoring = [bool](Get-WebConfigurationProperty -Filter 'webdav/authoring' -PSPath "IIS:\\Sites\\$name" -Name enabled -ErrorAction SilentlyContinue) } catch {}
  if ($authoring) {
    $port = 80
    try {
      $b = $_.bindings.Collection | Select-Object -First 1
      if ($b -and $b.bindingInformation) { $parts = $b.bindingInformation -split ':'; if ($parts.Length -gt 1 -and $parts[1]) { $port = [int]$parts[1] } }
    } catch {}
    $anon = $false
    try { $anon = [bool](Get-WebConfigurationProperty -Filter 'security/authentication/anonymousAuthentication' -PSPath "IIS:\\Sites\\$name" -Name enabled -ErrorAction SilentlyContinue) } catch {}
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
    supportsServerConfig: false,
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
    if (!validateName(input.name)) throw Errors.invalidParam('站点名非法')
    if (!validatePath(input.path)) throw Errors.invalidParam('路径非法')
    const port = input.port || 80
    // 创建 Web 站点（WebDAV 寄生于 Web 站点）
    await runPowerShellVoid(
      `New-Website -Name ${psQuote(input.name)} -PhysicalPath ${psQuote(input.path)} -Port ${port} -Force`
    )
    // 启用 WebDAV authoring
    await runPowerShellVoid(
      `Set-WebConfigurationProperty -Filter 'webdav/authoring' -PSPath "IIS:\\Sites\\${psQuote(
        input.name
      )}" -Name enabled -Value $true -ErrorAction SilentlyContinue`
    )
    if (input.anonymousEnabled !== undefined) {
      await runPowerShellVoid(
        `Set-WebConfigurationProperty -Filter 'security/authentication/anonymousAuthentication' -PSPath "IIS:\\Sites\\${psQuote(
          input.name
        )}" -Name enabled -Value $${input.anonymousEnabled} -ErrorAction SilentlyContinue`
      )
    }
    const site = await fetchSite(input.name)
    if (!site) throw Errors.commandFailed('WebDAV 站点创建后未能读取')
    return site
  },

  async deleteShare(name: string): Promise<void> {
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    await runPowerShellVoid(`Remove-Item "IIS:\\Sites\\${psQuote(name)}" -Recurse -Force -ErrorAction SilentlyContinue`)
  },

  async updateShare(name: string, input: UpdateShareInput): Promise<Share> {
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    if (input.anonymousEnabled !== undefined) {
      await runPowerShellVoid(
        `Set-WebConfigurationProperty -Filter 'security/authentication/anonymousAuthentication' -PSPath "IIS:\\Sites\\${psQuote(
          name
        )}" -Name enabled -Value $${input.anonymousEnabled} -ErrorAction SilentlyContinue`
      )
    }
    const site = await fetchSite(name)
    if (!site) throw Errors.shareNotFound(name)
    return site
  },

  async toggleShare(name: string, enabled: boolean): Promise<void> {
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    const want = enabled ? 'Start' : 'Stop'
    await runPowerShellVoid(
      `$s = Get-Item "IIS:\\Sites\\${psQuote(name)}" -ErrorAction Stop; if ('$want' -eq 'Start' -and $s.State -ne 'Started') { $s.Start() } elseif ('$want' -eq 'Stop' -and $s.State -eq 'Started') { $s.Stop() }`
    )
  },

  async getPermissions(name: string): Promise<SharePermission[]> {
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    const raw = await runPowerShell<RawWebdavRule | RawWebdavRule[]>(
      `Get-WebConfiguration -Filter 'webdav/authoring/authoringRules/*' -PSPath "IIS:\\Sites\\${psQuote(
        name
      )}" -ErrorAction SilentlyContinue`
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
    if (!validateName(name)) throw Errors.invalidParam('站点名非法')
    await runPowerShellVoid(
      `Clear-WebConfiguration -Filter 'webdav/authoring/authoringRules' -PSPath "IIS:\\Sites\\${psQuote(
        name
      )}" -ErrorAction SilentlyContinue`
    )
    for (const p of perms) {
      const bits: string[] = []
      if (p.access === 'Read' || p.access === 'Change' || p.access === 'Full') bits.push('Read')
      if (p.access === 'Change' || p.access === 'Full') bits.push('Write')
      if (p.access === 'Full') bits.push('Source')
      const access = bits.join(',')
      if (p.accountType === 'Group') {
        await runPowerShellVoid(
          `Add-WebConfiguration -Filter 'webdav/authoring/authoringRules' -PSPath "IIS:\\Sites\\${psQuote(
            name
          )}" -Value @{roles=${psQuote(p.account)};path='*';access='${access}'} -ErrorAction SilentlyContinue`
        )
      } else {
        await runPowerShellVoid(
          `Add-WebConfiguration -Filter 'webdav/authoring/authoringRules' -PSPath "IIS:\\Sites\\${psQuote(
            name
          )}" -Value @{users=${psQuote(p.account)};path='*';access='${access}'} -ErrorAction SilentlyContinue`
        )
      }
    }
  },

  // WebDAV 无原生会话 API（capabilities.supportsSessions=false）
  async getServiceStatus(): Promise<ServiceStatus> {
    return webdav.getServiceStatus()
  },

  async restartService(): Promise<void> {
    return webdav.restartService()
  }
}

// 读取单个 WebDAV 站点
async function fetchSite(name: string): Promise<Share | null> {
  try {
    const raw = await runPowerShell<RawWebdavSite>(
      `Get-Website -Name ${psQuote(name)} | ForEach-Object {
        $n = $_.Name
        $port = 80
        try { $b = $_.bindings.Collection | Select-Object -First 1; if ($b.bindingInformation) { $parts = $b.bindingInformation -split ':'; if ($parts.Length -gt 1 -and $parts[1]) { $port = [int]$parts[1] } } } catch {}
        $authoring = $false
        try { $authoring = [bool](Get-WebConfigurationProperty -Filter 'webdav/authoring' -PSPath "IIS:\\Sites\\$n" -Name enabled -ErrorAction SilentlyContinue) } catch {}
        $anon = $false
        try { $anon = [bool](Get-WebConfigurationProperty -Filter 'security/authentication/anonymousAuthentication' -PSPath "IIS:\\Sites\\$n" -Name enabled -ErrorAction SilentlyContinue) } catch {}
        [PSCustomObject]@{ Name=$n; State=$_.State; PhysicalPath=$_.physicalPath; Port=$port; AuthoringEnabled=$authoring; AnonymousEnabled=$anon }
      }`
    )
    if (!raw || !raw.Name) return null
    return mapWebdavSite(raw)
  } catch {
    return null
  }
}
