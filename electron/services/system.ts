import { userInfo } from 'os'
import { runPowerShell, runPowerShellVoid } from '../lib/powershell'
import { readAuditLog } from '../lib/audit'
import { Errors } from '../lib/errors'
import { getServiceStatus } from './smb'
import { adapterList, adapterSessions } from './protocol/registry'
import type { UserInfo, DashboardStats, Protocol, Share } from '../types'

export async function getCurrentUser(): Promise<UserInfo> {
  const name = userInfo().username
  return { username: name, isAdmin: await isAdmin() }
}

export async function isAdmin(): Promise<boolean> {
  try {
    const r = await runPowerShell<boolean>(
      '(New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)',
      { retries: 0 }
    )
    return !!r
  } catch {
    return false
  }
}

export async function relaunchAsAdmin(): Promise<void> {
  // 提权由主进程处理
  throw Errors.commandFailed('提权由主进程处理')
}

export async function getDashboardStats(): Promise<DashboardStats> {
  // 跨协议聚合：经 adapter 注册表合并所有已注册协议的共享
  const allShares = await adapterList().catch(() => [] as Share[])

  const byProtocol: Record<Protocol, { shares: number; sessions: number }> = {
    smb: { shares: 0, sessions: 0 },
    nfs: { shares: 0, sessions: 0 },
    ftp: { shares: 0, sessions: 0 },
    webdav: { shares: 0, sessions: 0 }
  }
  for (const s of allShares) {
    if (byProtocol[s.protocol]) byProtocol[s.protocol].shares++
  }

  // SMB 会话与打开文件（直接经 PowerShell，效率最高）
  let activeSessions = 0
  let openFiles = 0
  try {
    const sessions = await runPowerShell<any[]>('Get-SmbSession')
    const smbSessions = Array.isArray(sessions) ? sessions.length : 0
    activeSessions += smbSessions
    byProtocol.smb.sessions = smbSessions
  } catch {
    // SMB 未装或失败静默
  }
  try {
    const files = await runPowerShell<any[]>('Get-SmbOpenFile')
    openFiles = Array.isArray(files) ? files.length : 0
  } catch {
    // 静默
  }

  // NFS 会话（经 adapter 统一路由）
  try {
    const nfsSessions = await adapterSessions('nfs')
    byProtocol.nfs.sessions = nfsSessions.length
    activeSessions += nfsSessions.length
  } catch {
    // NFS 未装或无会话静默
  }

  // SMB 服务状态（其他协议服务状态见 HealthBar/协议探测）
  const svc = await getServiceStatus()
  const serviceStatus: DashboardStats['serviceStatus'] = svc.status

  const topShares = allShares
    .filter((s) => s.type !== 'Special' && s.type !== 'IPC')
    .map((s) => ({ name: s.name, connections: s.concurrentUsers || 0, protocol: s.protocol }))
    .sort((a, b) => b.connections - a.connections)
    .slice(0, 8)

  return {
    shareCount: allShares.filter((s) => s.type !== 'Special' && s.type !== 'IPC').length,
    activeSessions,
    openFiles,
    serviceStatus,
    topShares,
    byProtocol
  }
}

export async function getAuditLog(): Promise<string> {
  return readAuditLog()
}

export async function healthCheck(): Promise<{ ok: boolean; detail: string }> {
  try {
    await runPowerShellVoid('Get-Command Get-SmbShare | Out-Null', { retries: 0 })
    return { ok: true, detail: 'PowerShell SMB 模块可用' }
  } catch (e) {
    return { ok: false, detail: (e as Error).message }
  }
}
