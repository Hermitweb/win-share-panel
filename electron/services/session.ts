import { runPowerShell, runPowerShellVoid, psQuote } from '../lib/powershell'
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
  await runPowerShellVoid(`Close-SmbSession -ClientUserName ${psQuote(clientUserName)} -Force`)
}

export async function closeFile(fileId: string): Promise<void> {
  await runPowerShellVoid(`Close-SmbOpenFile -FileId ${psQuote(fileId)} -Force`)
}
