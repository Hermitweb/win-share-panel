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
import { runPowerShell, runPowerShellVoid, psQuote, psBool, psNumber, psEnum, validateName, validatePath } from '../../../lib/powershell'
import { Errors } from '../../../lib/errors'
import * as nfs from '../../nfs'

// 枚举白名单（与 types.ts 声明一致），拼入命令前运行时校验，杜绝注入
const NFS_AUTH_MODES = new Set(['krb5', 'krb5i', 'krb5p', 'sys'])
const NFS_PERMISSIONS = new Set(['ro', 'rw'])

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
    console.log('[createShare:nfs] 适配器接收输入:', {
      name: input.name,
      path: input.path,
      authentication: input.authentication,
      nfsPermission: input.nfsPermission,
      enableUnmappedAccess: input.enableUnmappedAccess,
      allowRootAccess: input.allowRootAccess,
      anonymousUid: input.anonymousUid,
      anonymousGid: input.anonymousGid
    })
    if (!validateName(input.name)) throw Errors.invalidParam('共享名非法')
    if (!validatePath(input.path)) throw Errors.invalidParam('路径非法')
    const parts = ['New-NfsShare', `-Name ${psQuote(input.name)}`, `-Path ${psQuote(input.path)}`]
    // 认证模式数组：逐项白名单校验后拼接
    const authRaw = input.authentication || ['sys']
    const auth = authRaw.map((a) => psEnum(a, NFS_AUTH_MODES)).filter((a): a is string => !!a)
    parts.push(`-Authentication ${auth.length ? auth.join(',') : 'sys'}`)
    const perm = psEnum(input.nfsPermission, NFS_PERMISSIONS) || 'rw'
    parts.push(`-Permission ${perm}`)
    const uma = psBool(input.enableUnmappedAccess)
    if (uma) parts.push(`-EnableUnmappedAccess ${uma}`)
    const ra = psBool(input.allowRootAccess)
    if (ra) parts.push(`-AllowRootAccess ${ra}`)
    const uid = psNumber(input.anonymousUid)
    if (uid) parts.push(`-AnonymousUid ${uid}`)
    const gid = psNumber(input.anonymousGid)
    if (gid) parts.push(`-AnonymousGid ${gid}`)
    const cmd = parts.join(' ')
    console.log('[createShare:nfs] PowerShell 命令:', cmd)
    try {
      await runPowerShellVoid(cmd)
      console.log('[createShare:nfs] New-NfsShare 执行成功，正在读取共享信息...')
      const raw = await runPowerShell<RawNfsShare>(`Get-NfsShare -Name ${psQuote(input.name)}`)
      console.log('[createShare:nfs] 共享创建完成:', input.name)
      return mapNfsShare(raw)
    } catch (e) {
      console.error('[createShare:nfs] 创建失败:', input.name, (e as Error).message)
      // 清理可能的孤儿共享（New-NfsShare 成功但 Get-NfsShare 失败的情况）
      console.log('[createShare:nfs] 尝试清理可能的孤儿共享...')
      await runPowerShellVoid(
        `try { Remove-NfsShare -Name ${psQuote(input.name)} -Force -ErrorAction Stop } catch {}`,
        { retries: 0 }
      )
      // 验证清理结果
      const stillExists = await runPowerShell<string>(
        `try { Get-NfsShare -Name ${psQuote(input.name)} -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Name } catch {}`,
        { retries: 0 }
      ).catch(() => null)
      if (stillExists) {
        console.error('[createShare:nfs] 孤儿共享清理失败！共享仍存在:', input.name)
      } else {
        console.log('[createShare:nfs] 孤儿共享已确认清理:', input.name)
      }
      throw e
    }
  },

  async deleteShare(name: string): Promise<void> {
    console.log('[deleteShare:nfs] 删除共享:', name)
    if (!validateName(name)) throw Errors.invalidParam('共享名非法')
    const cmd = `Remove-NfsShare -Name ${psQuote(name)} -Force`
    console.log('[deleteShare:nfs] PowerShell 命令:', cmd)
    try {
      await runPowerShellVoid(cmd)
      console.log('[deleteShare:nfs] 删除成功:', name)
    } catch (e) {
      console.error('[deleteShare:nfs] 删除失败:', name, (e as Error).message)
      throw e
    }
  },

  async updateShare(name: string, input: UpdateShareInput): Promise<Share> {
    console.log('[updateShare:nfs] 更新共享:', name, {
      nfsPermission: input.nfsPermission,
      allowRootAccess: input.allowRootAccess,
      enableUnmappedAccess: input.enableUnmappedAccess
    })
    if (!validateName(name)) throw Errors.invalidParam('共享名非法')
    const parts = ['Set-NfsShare', `-Name ${psQuote(name)}`]
    const perm = psEnum(input.nfsPermission, NFS_PERMISSIONS)
    if (perm) parts.push(`-Permission ${perm}`)
    const ra = psBool(input.allowRootAccess)
    if (ra) parts.push(`-AllowRootAccess ${ra}`)
    const uma = psBool(input.enableUnmappedAccess)
    if (uma) parts.push(`-EnableUnmappedAccess ${uma}`)
    parts.push('-Confirm:$false')
    const cmd = parts.join(' ')
    console.log('[updateShare:nfs] PowerShell 命令:', cmd)
    try {
      await runPowerShellVoid(cmd)
      console.log('[updateShare:nfs] 更新成功，正在读取共享信息...')
      const raw = await runPowerShell<RawNfsShare>(`Get-NfsShare -Name ${psQuote(name)}`)
      console.log('[updateShare:nfs] 共享更新完成:', name)
      return mapNfsShare(raw)
    } catch (e) {
      console.error('[updateShare:nfs] 更新失败:', name, (e as Error).message)
      throw e
    }
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
    console.log('[setPermissions:nfs] 设置权限:', name, { 权限条数: perms.length, 权限: perms.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`) })
    if (!validateName(name)) throw Errors.invalidParam('共享名非法')
    // 事务补偿：先备份当前权限，若后续授予中途失败则回滚到原状态
    const backup = this.getPermissions ? await this.getPermissions(name).catch(() => [] as SharePermission[]) : []
    console.log('[setPermissions:nfs] 已备份当前权限:', backup.length, '条 →', backup.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
    // 先清空所有已配置权限
    try {
      const existing = await runPowerShell<RawNfsPermission | RawNfsPermission[]>(
        `Get-NfsSharePermission -Name ${psQuote(name)}`
      )
      const arr = Array.isArray(existing) ? existing : [existing]
      console.log('[setPermissions:nfs] 清空已有权限:', arr.length, '条')
      for (const e of arr) {
        await runPowerShellVoid(
          `Revoke-NfsSharePermission -Name ${psQuote(name)} -ClientName ${psQuote(e.ClientName)} -Confirm:$false`
        )
      }
    } catch {
      // 清空失败不阻塞
    }
    // 逐个授予，收集失败项（NoAccess 无对应授予语义——前面已清空全部权限，跳过即可）
    const failed: string[] = []
    for (const p of perms) {
      if (p.access === 'NoAccess') continue
      const perm = p.access === 'Full' || p.access === 'Change' ? 'rw' : 'ro'
      const denyFlag = p.deny ? 'Deny' : 'Allow'
      const cmd = `Grant-NfsSharePermission -Name ${psQuote(name)} -ClientName ${psQuote(p.account)} -Permission ${perm} -Type ${denyFlag} -Confirm:$false`
      console.log('[setPermissions:nfs] 授予权限, PowerShell 命令:', cmd)
      try {
        await runPowerShellVoid(cmd)
      } catch (e) {
        console.error('[setPermissions:nfs] 授予失败:', p.account, (e as Error).message)
        failed.push(p.account)
      }
    }
    // 若有失败项：回滚到备份状态，避免共享处于部分权限的危险状态
    if (failed.length > 0) {
      console.error('[setPermissions:nfs] 回滚触发！失败账号:', failed.join(', '))
      // 查询回滚前的当前权限状态（部分授予后的残留状态）
      const beforeRollback = this.getPermissions ? await this.getPermissions(name).catch(() => [] as SharePermission[]) : []
      console.log('[setPermissions:nfs] 回滚前权限状态:', beforeRollback.length, '条 →', beforeRollback.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
      // 清空当前（可能部分已授予）
      try {
        const current = await runPowerShell<RawNfsPermission | RawNfsPermission[]>(
          `Get-NfsSharePermission -Name ${psQuote(name)}`
        )
        const arr = Array.isArray(current) ? current : [current]
        for (const c of arr) {
          await runPowerShellVoid(
            `Revoke-NfsSharePermission -Name ${psQuote(name)} -ClientName ${psQuote(c.ClientName)} -Confirm:$false`
          )
        }
      } catch {}
      // 恢复备份权限
      for (const p of backup) {
        if (p.access === 'NoAccess') continue
        const perm = p.access === 'Full' || p.access === 'Change' ? 'rw' : 'ro'
        const denyFlag = p.deny ? 'Deny' : 'Allow'
        await runPowerShellVoid(
          `Grant-NfsSharePermission -Name ${psQuote(name)} -ClientName ${psQuote(p.account)} -Permission ${perm} -Type ${denyFlag} -Confirm:$false`
        ).catch(() => {})
      }
      // 查询回滚后的权限状态，验证是否恢复成功
      const afterRollback = this.getPermissions ? await this.getPermissions(name).catch(() => [] as SharePermission[]) : []
      console.log('[setPermissions:nfs] 回滚后权限状态:', afterRollback.length, '条 →', afterRollback.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
      throw Errors.commandFailed(`部分权限授予失败（${failed.join(', ')}），已回滚到原始状态`)
    }
    console.log('[setPermissions:nfs] 权限设置成功:', name)
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
    console.log('[closeSession:nfs] 关闭会话:', sessionId)
    const cmd = `Disconnect-NfsClient -ClientName ${psQuote(sessionId)} -Confirm:$false`
    console.log('[closeSession:nfs] PowerShell 命令:', cmd)
    try {
      await runPowerShellVoid(cmd)
      console.log('[closeSession:nfs] 会话关闭成功:', sessionId)
    } catch (e) {
      console.error('[closeSession:nfs] 会话关闭失败:', sessionId, (e as Error).message)
      throw e
    }
  },

  async getServiceStatus(): Promise<ServiceStatus> {
    return nfs.getServiceStatus()
  },

  async restartService(): Promise<void> {
    console.log('[restartService:nfs] 重启 NFS 服务...')
    try {
      await nfs.restartService()
      console.log('[restartService:nfs] 服务重启成功')
    } catch (e) {
      console.error('[restartService:nfs] 服务重启失败:', (e as Error).message)
      throw e
    }
  },

  async getConfig(): Promise<NfsServerConfig> {
    return nfs.getConfig()
  },

  async setConfig(config: Partial<NfsServerConfig>): Promise<void> {
    console.log('[setConfig:nfs] 设置服务器配置:', Object.keys(config))
    try {
      await nfs.setConfig(config)
      console.log('[setConfig:nfs] 配置设置成功')
    } catch (e) {
      console.error('[setConfig:nfs] 配置设置失败:', (e as Error).message)
      throw e
    }
  },

  defaultConfig(): NfsServerConfig {
    return nfs.defaultConfig()
  },

  async restoreDefault(): Promise<NfsServerConfig> {
    return nfs.restoreDefault()
  }
}
