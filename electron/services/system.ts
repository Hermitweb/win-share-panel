import { userInfo } from 'os'
import { app } from 'electron'
import { runPowerShell, runPowerShellVoid, psQuote } from '../lib/powershell'
import { readAuditLog } from '../lib/audit'
import { Errors } from '../lib/errors'
import { getServiceStatus } from './smb'
import { adapterList, adapterSessions } from './protocol/registry'
import type { UserInfo, DashboardStats, Protocol, Share } from '../types'

// 协议白名单（与 types.ts 声明一致），IPC 边界运行时校验
const PROTOCOLS = new Set<Protocol>(['smb', 'nfs', 'ftp', 'webdav'])
export function isProtocol(v: unknown): v is Protocol {
  return typeof v === 'string' && PROTOCOLS.has(v as Protocol)
}

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
  // 以管理员权限重新启动当前可执行文件（UAC 提权），成功后退出当前非提权实例。
  // 开发模式下提权无意义（electron-vite 子进程），直接抛错引导用户用管理员终端启动。
  if (!app.isPackaged) {
    throw Errors.commandFailed('开发模式不支持自动提权，请以管理员身份运行开发环境')
  }
  const exePath = process.execPath
  try {
    // Start-Process -Verb RunAs 触发 UAC 提示；新进程启动成功后当前实例退出
    await runPowerShellVoid(
      `Start-Process -FilePath ${psQuote(exePath)} -Verb RunAs`,
      { retries: 0, timeout: 30000 }
    )
    // 给新实例一点启动时间后退出当前实例
    setTimeout(() => app.quit(), 500)
  } catch (e) {
    throw Errors.commandFailed(`提权失败：${(e as Error).message.slice(0, 200)}`)
  }
}

export async function getDashboardStats(): Promise<DashboardStats> {
  console.time('[perf] getDashboardStats')
  // 所有独立查询并行执行，避免 5+ 个 PowerShell 进程串行启动造成 3-5s 延迟
  const [allSharesResult, smbSessionsResult, smbFilesResult, nfsSessionsResult, svcResult] =
    await Promise.allSettled([
      adapterList().catch(() => [] as Share[]),
      runPowerShell<any[]>('Get-SmbSession').catch(() => []),
      runPowerShell<any[]>('Get-SmbOpenFile').catch(() => []),
      adapterSessions('nfs').catch(() => []),
      getServiceStatus()
    ])

  const allShares = allSharesResult.status === 'fulfilled' ? allSharesResult.value : []

  const byProtocol: Record<Protocol, { shares: number; sessions: number }> = {
    smb: { shares: 0, sessions: 0 },
    nfs: { shares: 0, sessions: 0 },
    ftp: { shares: 0, sessions: 0 },
    webdav: { shares: 0, sessions: 0 }
  }
  for (const s of allShares) {
    if (byProtocol[s.protocol]) byProtocol[s.protocol].shares++
  }

  // SMB 会话与打开文件
  let activeSessions = 0
  let openFiles = 0
  if (smbSessionsResult.status === 'fulfilled') {
    const smbSessions = Array.isArray(smbSessionsResult.value) ? smbSessionsResult.value.length : 0
    activeSessions += smbSessions
    byProtocol.smb.sessions = smbSessions
  }
  if (smbFilesResult.status === 'fulfilled') {
    openFiles = Array.isArray(smbFilesResult.value) ? smbFilesResult.value.length : 0
  }

  // NFS 会话
  if (nfsSessionsResult.status === 'fulfilled') {
    const nfsSessions = nfsSessionsResult.value
    byProtocol.nfs.sessions = nfsSessions.length
    activeSessions += nfsSessions.length
  }

  // SMB 服务状态
  const svc = svcResult.status === 'fulfilled' ? svcResult.value : { status: 'Unknown' as const, name: '', startType: '' as const }
  const serviceStatus: DashboardStats['serviceStatus'] = svc.status

  const topShares = allShares
    .filter((s) => s.type !== 'Special' && s.type !== 'IPC')
    .map((s) => ({ name: s.name, connections: s.concurrentUsers || 0, protocol: s.protocol }))
    .sort((a, b) => b.connections - a.connections)
    .slice(0, 8)

  console.timeEnd('[perf] getDashboardStats')
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
