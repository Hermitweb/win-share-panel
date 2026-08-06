import { appendFileSync, existsSync, statSync, renameSync, mkdirSync, readFileSync } from 'fs'
import { homedir, platform } from 'os'
import { join } from 'path'

const MAX_SIZE = 5 * 1024 * 1024 // 5MB
const MAX_FILES = 10

function logDir(): string {
  if (platform() === 'win32') {
    return join(process.env.APPDATA || homedir(), 'WinSharePanel')
  }
  return join(homedir(), '.winshare-panel')
}

function logFile(): string {
  return join(logDir(), 'audit.log')
}

export interface AuditRecord {
  ts: string
  operator: string
  action: string
  target: string
  result: 'success' | 'failure'
  detail?: string
}

export function audit(
  operator: string,
  action: string,
  target: string,
  result: 'success' | 'failure',
  detail?: string
): void {
  try {
    const dir = logDir()
    if (!existsSync(dir)) mkdirSync(dir, { recursive: true })
    const file = logFile()
    if (existsSync(file) && statSync(file).size > MAX_SIZE) rotate(file)
    const record: AuditRecord = { ts: new Date().toISOString(), operator, action, target, result, detail }
    appendFileSync(file, JSON.stringify(record) + '\n', 'utf8')
  } catch {
    // 审计失败不影响主流程
  }
}

function rotate(file: string): void {
  for (let i = MAX_FILES - 1; i >= 1; i--) {
    const src = `${file}.${i}`
    const dst = `${file}.${i + 1}`
    if (existsSync(src)) renameSync(src, dst)
  }
  if (existsSync(file)) renameSync(file, `${file}.1`)
}

export function readAuditLog(): string {
  try {
    const file = logFile()
    if (!existsSync(file)) return ''
    return readFileSync(file, 'utf8')
  } catch {
    return ''
  }
}
