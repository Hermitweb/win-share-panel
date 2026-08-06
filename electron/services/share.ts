import { runPowerShell, runPowerShellVoid, psQuote, validateName, validatePath } from '../lib/powershell'
import { Errors } from '../lib/errors'
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs'
import { join } from 'path'
import { homedir } from 'os'
import type { Share, CreateShareOpts, UpdateShareOpts, SharePermission } from '../types'

interface RawShare {
  Name: string
  Path: string
  Description: string
  ShareType: number
  Hidden: boolean
  ConcurrentUsers: number
  Cached: boolean
  Encrypted: boolean
  Special: boolean
}

const SHARE_TYPE_MAP: Record<number, Share['type']> = { 0: 'Disk', 1: 'IPC', 2: 'Printer', 3: 'Special' }

function mapShare(r: RawShare): Share {
  return {
    name: r.Name,
    path: r.Path,
    description: r.Description || '',
    type: r.Special ? 'Special' : SHARE_TYPE_MAP[r.ShareType] || 'Disk',
    hidden: !!r.Hidden,
    concurrentUsers: r.ConcurrentUsers || 0,
    status: 'Enabled',
    cached: !!r.Cached,
    encrypted: !!r.Encrypted
  }
}

function dataDir(): string {
  return join(process.env.APPDATA || homedir(), 'WinSharePanel')
}
function disabledFile(): string {
  return join(dataDir(), 'disabled.json')
}
interface DisabledRecord {
  name: string
  path: string
  description: string
  permissions: SharePermission[]
  encrypted: boolean
}
function readDisabled(): DisabledRecord[] {
  try {
    if (!existsSync(disabledFile())) return []
    return JSON.parse(readFileSync(disabledFile(), 'utf8'))
  } catch {
    return []
  }
}
function writeDisabled(list: DisabledRecord[]): void {
  const dir = dataDir()
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true })
  writeFileSync(disabledFile(), JSON.stringify(list, null, 2), 'utf8')
}

export async function listShares(): Promise<Share[]> {
  const raw = await runPowerShell<RawShare | RawShare[]>('Get-SmbShare')
  const arr = Array.isArray(raw) ? raw : [raw]
  return arr.map(mapShare)
}

export async function createShare(opts: CreateShareOpts): Promise<Share> {
  if (!validateName(opts.name)) throw Errors.invalidParam('共享名非法')
  if (!validatePath(opts.path)) throw Errors.invalidParam('路径非法')
  const parts = ['New-SmbShare', `-Name ${psQuote(opts.name)}`, `-Path ${psQuote(opts.path)}`]
  if (opts.description) parts.push(`-Description ${psQuote(opts.description)}`)
  if (opts.encrypted) parts.push('-EncryptData $true')
  if (opts.fullAccess?.length) parts.push(`-FullAccess ${opts.fullAccess.map(psQuote).join(',')}`)
  if (opts.changeAccess?.length) parts.push(`-ChangeAccess ${opts.changeAccess.map(psQuote).join(',')}`)
  if (opts.readAccess?.length) parts.push(`-ReadAccess ${opts.readAccess.map(psQuote).join(',')}`)
  await runPowerShellVoid(parts.join(' '))
  const raw = await runPowerShell<RawShare>(`Get-SmbShare -Name ${psQuote(opts.name)}`)
  return mapShare(raw)
}

export async function updateShare(name: string, opts: UpdateShareOpts): Promise<Share> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  const parts = ['Set-SmbShare', `-Name ${psQuote(name)}`]
  if (opts.description !== undefined) parts.push(`-Description ${psQuote(opts.description)}`)
  await runPowerShellVoid(parts.join(' '))
  const raw = await runPowerShell<RawShare>(`Get-SmbShare -Name ${psQuote(name)}`)
  return mapShare(raw)
}

export async function deleteShare(name: string): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  await runPowerShellVoid(`Remove-SmbShare -Name ${psQuote(name)} -Force`)
}

export async function toggleShare(name: string, enabled: boolean): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  if (!enabled) {
    const shares = await listShares()
    const s = shares.find((x) => x.name === name)
    if (!s) throw Errors.shareNotFound(name)
    const perms = await getSharePermissions(name)
    const list = readDisabled()
    if (!list.find((x) => x.name === name)) {
      list.push({ name, path: s.path, description: s.description, permissions: perms, encrypted: s.encrypted })
      writeDisabled(list)
    }
    await runPowerShellVoid(`Remove-SmbShare -Name ${psQuote(name)} -Force`)
  } else {
    const list = readDisabled()
    const idx = list.findIndex((x) => x.name === name)
    if (idx < 0) throw Errors.commandFailed('未找到该共享的禁用记录，无法恢复')
    const r = list[idx]
    await createShare({
      name: r.name,
      path: r.path,
      description: r.description,
      fullAccess: r.permissions.filter((p) => p.access === 'Full').map((p) => p.account),
      changeAccess: r.permissions.filter((p) => p.access === 'Change').map((p) => p.account),
      readAccess: r.permissions.filter((p) => p.access === 'Read').map((p) => p.account),
      encrypted: r.encrypted
    })
    list.splice(idx, 1)
    writeDisabled(list)
  }
}

export async function getSharePermissions(name: string): Promise<SharePermission[]> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  const r = await runPowerShell<any[]>(`Get-SmbShareAccess -Name ${psQuote(name)}`)
  const arr = Array.isArray(r) ? r : [r]
  return arr.map((x) => ({
    shareName: name,
    account: x.AccountName,
    accountType: x.AccountType === 1 ? 'User' : 'Group',
    access: x.AccessRight === 0 ? 'Full' : x.AccessRight === 1 ? 'Change' : 'Read',
    deny: x.AccessControlType === 1
  }))
}

export async function exportConfig(): Promise<string> {
  const shares = await listShares()
  const configs: Array<Record<string, unknown>> = []
  for (const s of shares) {
    if (s.type === 'Special' || s.type === 'IPC') continue
    const perms = await getSharePermissions(s.name)
    configs.push({ ...s, permissions: perms })
  }
  return JSON.stringify({ exportedAt: new Date().toISOString(), shares: configs }, null, 2)
}

export async function importConfig(json: string): Promise<void> {
  const data = JSON.parse(json)
  if (!data.shares || !Array.isArray(data.shares)) throw Errors.invalidParam('导入文件格式错误')
  for (const s of data.shares) {
    if (!validatePath(s.path)) continue
    try {
      const full = (s.permissions || []).filter((p: SharePermission) => p.access === 'Full' && !p.deny).map((p: SharePermission) => p.account)
      const change = (s.permissions || []).filter((p: SharePermission) => p.access === 'Change' && !p.deny).map((p: SharePermission) => p.account)
      const read = (s.permissions || []).filter((p: SharePermission) => p.access === 'Read' && !p.deny).map((p: SharePermission) => p.account)
      await createShare({ name: s.name, path: s.path, description: s.description, fullAccess: full, changeAccess: change, readAccess: read, encrypted: s.encrypted })
    } catch {
      // 单个失败跳过
    }
  }
}
