import { runPowerShell, runPowerShellVoid, psQuote } from '../lib/powershell'
import { Errors } from '../lib/errors'
import type { SmbSession, SmbOpenFile } from '../types'

export async function listSessions(): Promise<SmbSession[]> {
  const raw = await runPowerShell<any[]>('Get-SmbSession')
  const arr = Array.isArray(raw) ? raw : [raw]
  return arr.map((s) => ({
    clientId: `${s.ClientUserName || ''}@${s.ClientComputerName || ''}`,
    clientUserName: s.ClientUserName || '',
    clientComputerName: s.ClientComputerName || '',
    sessionStartTime: s.SessionStartTime || '',
    clientOpenFiles: s.ClientOpenFiles || 0,
    clientIdleTime: s.ClientIdleTime || 0,
    bytesReceived: s.BytesReceived || 0,
    bytesSent: s.BytesSent || 0
  }))
}

export async function listOpenFiles(): Promise<SmbOpenFile[]> {
  const raw = await runPowerShell<any[]>('Get-SmbOpenFile')
  const arr = Array.isArray(raw) ? raw : [raw]
  return arr.map((f) => ({
    fileId: String(f.FileId),
    path: f.Path || '',
    clientUserName: f.ClientUserName || '',
    clientComputerName: f.ClientComputerName || '',
    lockCount: f.LockCount || 0,
    relativeOpenTime: f.RelativeOpenTime || 0
  }))
}

export async function closeSession(clientUserName: string): Promise<void> {
  // 防御纵深：clientUserName 来自 IPC，校验非空且无危险字符（psQuote 已兜底，此处提前拒绝明显非法值）
  if (typeof clientUserName !== 'string' || !clientUserName || clientUserName.length > 200) {
    throw Errors.invalidParam('会话标识非法')
  }
  await runPowerShellVoid(`Close-SmbSession -ClientUserName ${psQuote(clientUserName)} -Force`)
}

export async function closeFile(fileId: string): Promise<void> {
  // fileId 来自 Get-SmbOpenFile，应为数字；校验防注入（psQuote 已兜底）
  if (typeof fileId !== 'string' || !/^\d+$/.test(fileId)) {
    throw Errors.invalidParam('文件标识非法')
  }
  await runPowerShellVoid(`Close-SmbOpenFile -FileId ${psQuote(fileId)} -Force`)
}
