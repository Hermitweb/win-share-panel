import { userInfo } from 'os'
import { runPowerShell, runPowerShellVoid } from '../lib/powershell'
import { readAuditLog } from '../lib/audit'
import { Errors } from '../lib/errors'
import type { UserInfo, DashboardStats } from '../types'

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
  const shares = await runPowerShell<any[]>('Get-SmbShare')
  const shareCount = (Array.isArray(shares) ? shares : []).filter((s) => !s.Special).length
  const sessions = await runPowerShell<any[]>('Get-SmbSession')
  const activeSessions = Array.isArray(sessions) ? sessions.length : 0
  const files = await runPowerShell<any[]>('Get-SmbOpenFile')
  const openFiles = Array.isArray(files) ? files.length : 0
  const svc = await runPowerShell<any>('Get-Service LanmanServer')
  const serviceStatus: DashboardStats['serviceStatus'] =
    svc.Status === 'Running' ? 'Running' : svc.Status === 'Stopped' ? 'Stopped' : 'Unknown'
  const topShares = (Array.isArray(shares) ? shares : [])
    .filter((s) => !s.Special)
    .map((s) => ({ name: s.Name, connections: s.ConcurrentUsers || 0 }))
    .sort((a, b) => b.connections - a.connections)
    .slice(0, 5)
  return { shareCount, activeSessions, openFiles, serviceStatus, topShares }
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
