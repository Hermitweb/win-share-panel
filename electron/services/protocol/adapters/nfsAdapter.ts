import type { ProtocolAdapter } from '../ProtocolAdapter'
import type {
  Share,
  SharePermission,
  ServiceStatus,
  ProtocolSession,
  NfsServerConfig,
  CreateShareInput,
  UpdateShareInput
} from '../../../types'
import { runPowerShell, runPowerShellVoid, psQuote, validateName, validatePath } from '../../../lib/powershell'
import { Errors } from '../../../lib/errors'
import * as nfs from '../../nfs'

interface RawNfsShare {
  Name: string
  Path: string
  NetworkName: string
  Authentication: string | string[]
  AnonymousUid: number
  AnonymousGid: number
  EnableUnmappedAccess: boolean
  AllowRootAccess: boolean
  Availability: string
  Online: boolean
  Permission: string
}

interface RawNfsClient {
  ClientName: string
  ClientComputerName: string
  ClientIpAddress: string
  SessionStartTime: string
  OpenFileCount: number
  IdleTime: number
  BytesReceived: number
  BytesSent: number
}

interface RawNfsPermission {
  ClientName: string
  Permission: string
  Type: string
}

function toArray(v: unknown): string[] {
  if (!v) return []
  return Array.isArray(v) ? v : [v as string]
}

function mapNfsShare(r: RawNfsShare): Share {
  return {
    name: r.Name,
    path: r.Path,
    description: '',
    protocol: 'nfs',
    type: 'Disk',
    hidden: false,
    encrypted: false,
    concurrentUsers: 0,
    status: r.Online ? 'Enabled' : 'Disabled',
    cached: false,
    networkName: r.NetworkName,
    authentication: toArray(r.Authentication) as Share['authentication'],
    nfsPermission: (r.Permission || 'rw') === 'ro' ? 'ro' : 'rw',
    anonymousUid: r.AnonymousUid,
    anonymousGid: r.AnonymousGid,
    enableUnmappedAccess: !!r.EnableUnmappedAccess,
    allowRootAccess: !!r.AllowRootAccess
  }
}

export const nfsAdapter: ProtocolAdapter = {
  protocol: 'nfs',
  capabilities: {
    supportsCreate: true,
    supportsUpdate: true,
    supportsDelete: true,
    supportsToggle: false,
    supportsPermissions: true,
    supportsSessions: true,
    supportsOpenFiles: false,
    supportsServerConfig: true,
    supportsRestart: true,
    permissionModel: 'nfs-krb'
  },

  async listShares(): Promise<Share[]> {
    // retries:0 避免未装 NFS 角色时无谓重试 2 次造成切 Tab 延迟
    const raw = await runPowerShell<RawNfsShare | RawNfsShare[]>(
      'Get-NfsShare',
      { retries: 0 }
    )
    const arr = Array.isArray(raw) ? raw : [raw]
    return arr.map(mapNfsShare)
  },

  async createShare(input: CreateShareInput): Promise<Share> {
    if (!validateName(input.name)) throw Errors.invalidParam('共享名非法')
    if (!validatePath(input.path)) throw Errors.invalidParam('路径非法')
    const parts = ['New-NfsShare', `-Name ${psQuote(input.name)}`, `-Path ${psQuote(input.path)}`]
    const auth = input.authentication || ['sys']
    parts.push(`-Authentication ${auth.join(',')}`)
    parts.push(`-Permission ${input.nfsPermission || 'rw'}`)
    if (input.enableUnmappedAccess !== undefined) parts.push(`-EnableUnmappedAccess $${input.enableUnmappedAccess}`)
    if (input.allowRootAccess !== undefined) parts.push(`-AllowRootAccess $${input.allowRootAccess}`)
    if (input.anonymousUid !== undefined) parts.push(`-AnonymousUid ${input.anonymousUid}`)
    if (input.anonymousGid !== undefined) parts.push(`-AnonymousGid ${input.anonymousGid}`)
    await runPowerShellVoid(parts.join(' '))
    const raw = await runPowerShell<RawNfsShare>(`Get-NfsShare -Name ${psQuote(input.name)}`)
    return mapNfsShare(raw)
  },

  async deleteShare(name: string): Promise<void> {
    if (!validateName(name)) throw Errors.invalidParam('共享名非法')
    await runPowerShellVoid(`Remove-NfsShare -Name ${psQuote(name)} -Force`)
  },

  async updateShare(name: string, input: UpdateShareInput): Promise<Share> {
    if (!validateName(name)) throw Errors.invalidParam('共享名非法')
    const parts = ['Set-NfsShare', `-Name ${psQuote(name)}`]
    if (input.nfsPermission) parts.push(`-Permission ${input.nfsPermission}`)
    if (input.allowRootAccess !== undefined) parts.push(`-AllowRootAccess $${input.allowRootAccess}`)
    if (input.enableUnmappedAccess !== undefined) parts.push(`-EnableUnmappedAccess $${input.enableUnmappedAccess}`)
    parts.push('-Confirm:$false')
    await runPowerShellVoid(parts.join(' '))
    const raw = await runPowerShell<RawNfsShare>(`Get-NfsShare -Name ${psQuote(name)}`)
    return mapNfsShare(raw)
  },

  async getPermissions(name: string): Promise<SharePermission[]> {
    if (!validateName(name)) throw Errors.invalidParam('共享名非法')
    // retries:0 避免未装 NFS 时无谓重试
    const r = await runPowerShell<RawNfsPermission | RawNfsPermission[]>(
      `Get-NfsSharePermission -Name ${psQuote(name)}`,
      { retries: 0 }
    )
    const arr = Array.isArray(r) ? r : [r]
    return arr.map((x) => ({
      shareName: name,
      account: x.ClientName,
      accountType: 'Group' as const,
      access: x.Permission === 'rw' ? 'Change' : 'Read',
      deny: String(x.Type).toLowerCase() === 'deny'
    }))
  },

  async setPermissions(name: string, perms: SharePermission[]): Promise<void> {
    if (!validateName(name)) throw Errors.invalidParam('共享名非法')
    // 先清空所有已配置权限
    try {
      const existing = await runPowerShell<RawNfsPermission | RawNfsPermission[]>(
        `Get-NfsSharePermission -Name ${psQuote(name)}`
      )
      const arr = Array.isArray(existing) ? existing : [existing]
      for (const e of arr) {
        await runPowerShellVoid(
          `Revoke-NfsSharePermission -Name ${psQuote(name)} -ClientName ${psQuote(e.ClientName)} -Confirm:$false`
        )
      }
    } catch {
      // 清空失败不阻塞
    }
    // 重新授予
    for (const p of perms) {
      const perm = p.access === 'Full' || p.access === 'Change' ? 'rw' : 'ro'
      const denyFlag = p.deny ? 'Deny' : 'Allow'
      await runPowerShellVoid(
        `Grant-NfsSharePermission -Name ${psQuote(name)} -ClientName ${psQuote(p.account)} -Permission ${perm} -Type ${denyFlag} -Confirm:$false`
      )
    }
  },

  async listSessions(): Promise<ProtocolSession[]> {
    // retries:0 避免未装 NFS 时无谓重试
    const raw = await runPowerShell<RawNfsClient | RawNfsClient[]>(
      'Get-NfsClient',
      { retries: 0 }
    )
    const arr = Array.isArray(raw) ? raw : [raw]
    return arr.map((c) => ({
      protocol: 'nfs' as const,
      sessionId: c.ClientName || c.ClientComputerName || c.ClientIpAddress || '',
      clientUserName: c.ClientName || '',
      clientComputerName: c.ClientComputerName || c.ClientIpAddress || '',
      sessionStartTime: c.SessionStartTime || '',
      clientOpenFiles: c.OpenFileCount || 0,
      clientIdleTime: c.IdleTime || 0,
      bytesReceived: c.BytesReceived || 0,
      bytesSent: c.BytesSent || 0
    }))
  },

  async closeSession(sessionId: string): Promise<void> {
    // NFS 关闭客户端会话：Disconnect-NfsClient（需 ClientName）
    await runPowerShellVoid(`Disconnect-NfsClient -ClientName ${psQuote(sessionId)} -Confirm:$false`)
  },

  async getServiceStatus(): Promise<ServiceStatus> {
    return nfs.getServiceStatus()
  },

  async restartService(): Promise<void> {
    return nfs.restartService()
  },

  async getConfig(): Promise<NfsServerConfig> {
    return nfs.getConfig()
  },

  async setConfig(config: Partial<NfsServerConfig>): Promise<void> {
    return nfs.setConfig(config)
  }
}
