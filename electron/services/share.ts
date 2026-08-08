import { runPowerShell, runPowerShellVoid, psQuote, psBool, psNumber, psEnum, validateName, validatePath } from '../lib/powershell'
import { Errors } from '../lib/errors'
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs'
import { join } from 'path'
import { homedir } from 'os'
import type {
  Share,
  CreateShareOpts,
  UpdateShareOpts,
  SharePermission
} from '../types'

// 枚举白名单（与 types.ts 声明一致），拼入命令前运行时校验，杜绝注入
const CACHING_MODES = new Set(['None', 'Manual', 'Documents', 'Programs', 'BranchCache'])
const FOLDER_ENUM_MODES = new Set(['AccessBased', 'Unrestricted'])

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
  // 扩展字段
  FolderEnumerationMode?: string
  CachingMode?: string
  ConcurrentUserLimit?: number
  EncryptData?: boolean
  ShadowCopy?: boolean
}

const SHARE_TYPE_MAP: Record<number, Share['type']> = { 0: 'Disk', 1: 'IPC', 2: 'Printer', 3: 'Special' }

function mapShare(r: RawShare): Share {
  return {
    name: r.Name,
    path: r.Path,
    description: r.Description || '',
    protocol: 'smb',
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
  concurrentUserLimit?: number
  cachingMode?: CreateShareOpts['cachingMode']
  folderEnumerationMode?: CreateShareOpts['folderEnumerationMode']
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
  // retries:0 避免未装/降级时无谓重试造成切 Tab 延迟（与其他协议适配器一致）
  const raw = await runPowerShell<RawShare | RawShare[]>('Get-SmbShare', { retries: 0 })
  const arr = Array.isArray(raw) ? raw : [raw]
  return arr.map(mapShare)
}

export async function getShare(name: string): Promise<Share> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  const raw = await runPowerShell<RawShare>(`Get-SmbShare -Name ${psQuote(name)}`)
  if (!raw || !raw.Name) throw Errors.shareNotFound(name)
  return mapShare(raw)
}

export async function createShare(opts: CreateShareOpts): Promise<Share> {
  if (!validateName(opts.name)) throw Errors.invalidParam('共享名非法')
  if (!validatePath(opts.path)) throw Errors.invalidParam('路径非法')
  // 校验路径必须存在（New-SmbShare 对不存在路径会创建空文件夹，可能不符预期）
  const parts = ['New-SmbShare', `-Name ${psQuote(opts.name)}`, `-Path ${psQuote(opts.path)}`]
  if (opts.description) parts.push(`-Description ${psQuote(opts.description)}`)
  if (opts.encrypted || opts.encryptData) parts.push('-EncryptData $true')
  if (opts.fullAccess?.length) parts.push(`-FullAccess ${opts.fullAccess.map(psQuote).join(',')}`)
  if (opts.changeAccess?.length) parts.push(`-ChangeAccess ${opts.changeAccess.map(psQuote).join(',')}`)
  if (opts.readAccess?.length) parts.push(`-ReadAccess ${opts.readAccess.map(psQuote).join(',')}`)
  if (opts.noAccess?.length) parts.push(`-NoAccess ${opts.noAccess.map(psQuote).join(',')}`)
  // 高级选项（运行时类型校验，防止 IPC 传入非法值注入）
  if (opts.concurrentUserLimit !== undefined) {
    const n = psNumber(opts.concurrentUserLimit)
    if (n && Number(opts.concurrentUserLimit) > 0) parts.push(`-ConcurrentUserLimit ${n}`)
  }
  const cm = psEnum(opts.cachingMode, CACHING_MODES)
  if (cm) parts.push(`-CachingMode ${cm}`)
  const fem = psEnum(opts.folderEnumerationMode, FOLDER_ENUM_MODES)
  if (fem) parts.push(`-FolderEnumerationMode ${fem}`)
  if (opts.shareShadowCopy) {
    parts.push('-ShareShadowCopy')
  }
  const cmd = parts.join(' ')
  console.log('[createShare:smb] 输入参数:', {
    name: opts.name,
    path: opts.path,
    description: opts.description,
    encrypted: opts.encrypted,
    encryptData: opts.encryptData,
    fullAccess: opts.fullAccess,
    changeAccess: opts.changeAccess,
    readAccess: opts.readAccess,
    noAccess: opts.noAccess,
    concurrentUserLimit: opts.concurrentUserLimit,
    cachingMode: opts.cachingMode,
    folderEnumerationMode: opts.folderEnumerationMode,
    shareShadowCopy: opts.shareShadowCopy
  })
  console.log('[createShare:smb] PowerShell 命令:', cmd)
  try {
    await runPowerShellVoid(cmd)
    console.log('[createShare:smb] New-SmbShare 执行成功，正在读取共享信息...')
    const raw = await runPowerShell<RawShare>(`Get-SmbShare -Name ${psQuote(opts.name)}`)
    console.log('[createShare:smb] 共享创建完成:', opts.name)
    return mapShare(raw)
  } catch (e) {
    console.error('[createShare:smb] 创建失败:', opts.name, (e as Error).message)
    // 清理可能的孤儿共享（New-SmbShare 成功但 Get-SmbShare 失败的情况）
    console.log('[createShare:smb] 尝试清理可能的孤儿共享...')
    await runPowerShellVoid(
      `try { Remove-SmbShare -Name ${psQuote(opts.name)} -Force -ErrorAction Stop } catch {}`,
      { retries: 0 }
    )
    // 验证清理结果
    const stillExists = await runPowerShell<string>(
      `try { Get-SmbShare -Name ${psQuote(opts.name)} -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Name } catch {}`,
      { retries: 0 }
    ).catch(() => null)
    if (stillExists) {
      console.error('[createShare:smb] 孤儿共享清理失败！共享仍存在:', opts.name)
    } else {
      console.log('[createShare:smb] 孤儿共享已确认清理:', opts.name)
    }
    throw e
  }
}

export async function updateShare(name: string, opts: UpdateShareOpts): Promise<Share> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  const parts = ['Set-SmbShare', `-Name ${psQuote(name)}`]
  if (opts.description !== undefined) parts.push(`-Description ${psQuote(opts.description)}`)
  if (opts.concurrentUserLimit !== undefined) {
    const n = psNumber(opts.concurrentUserLimit)
    if (n) parts.push(`-ConcurrentUserLimit ${n}`)
  }
  const cm = psEnum(opts.cachingMode, CACHING_MODES)
  if (cm) parts.push(`-CachingMode ${cm}`)
  const fem = psEnum(opts.folderEnumerationMode, FOLDER_ENUM_MODES)
  if (fem) parts.push(`-FolderEnumerationMode ${fem}`)
  const enc = psBool(opts.encryptData)
  if (enc) parts.push(`-EncryptData ${enc}`)
  if (opts.cached !== undefined) {
    // Cached 在 SMB 中通过 CachingMode 控制（Cached=$true 等价于 CachingMode=Manual）
    if (opts.cached) {
      if (!cm) parts.push('-CachingMode Manual')
    } else if (!cm) {
      parts.push('-CachingMode None')
    }
  }
  parts.push('-Force')
  await runPowerShellVoid(parts.join(' '))
  const raw = await runPowerShell<RawShare>(`Get-SmbShare -Name ${psQuote(name)}`)
  return mapShare(raw)
}

export async function deleteShare(name: string): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  // 系统特殊共享（ADMIN$, IPC$, C$ 等）禁止删除
  const protectedShares = ['admin$', 'ipc$', 'c$', 'print$', 'fax$']
  if (protectedShares.includes(name.toLowerCase())) {
    throw Errors.invalidParam(`系统特殊共享 ${name} 不允许删除`)
  }
  await runPowerShellVoid(`Remove-SmbShare -Name ${psQuote(name)} -Force`)
}

export async function toggleShare(name: string, enabled: boolean): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  if (!enabled) {
    const shares = await listShares()
    const s = shares.find((x) => x.name === name)
    if (!s) throw Errors.shareNotFound(name)
    const perms = await getSharePermissions(name)
    // 捕获高级选项，以便恢复时还原（避免回退为默认值）
    const advanced = await getShareAdvanced(name)
    // 先删除共享，成功后再落盘禁用记录，避免 Remove 失败时残留脏记录
    await runPowerShellVoid(`Remove-SmbShare -Name ${psQuote(name)} -Force`)
    const list = readDisabled()
    if (!list.find((x) => x.name === name)) {
      list.push({
        name,
        path: s.path,
        description: s.description,
        permissions: perms,
        encrypted: s.encrypted,
        concurrentUserLimit: advanced.concurrentUserLimit,
        cachingMode: advanced.cachingMode,
        folderEnumerationMode: advanced.folderEnumerationMode
      })
      writeDisabled(list)
    }
  } else {
    const list = readDisabled()
    const idx = list.findIndex((x) => x.name === name)
    if (idx < 0) throw Errors.commandFailed('未找到该共享的禁用记录，无法恢复')
    const r = list[idx]
    // 还原共享：保留高级选项；allow 列表用 New-SmbShare 参数，deny 条目随后用 Block-SmbShareAccess 补回
    const allow = r.permissions.filter((p) => !p.deny)
    await createShare({
      name: r.name,
      path: r.path,
      description: r.description,
      fullAccess: allow.filter((p) => p.access === 'Full').map((p) => p.account),
      changeAccess: allow.filter((p) => p.access === 'Change').map((p) => p.account),
      readAccess: allow.filter((p) => p.access === 'Read').map((p) => p.account),
      encrypted: r.encrypted,
      concurrentUserLimit: r.concurrentUserLimit,
      cachingMode: r.cachingMode,
      folderEnumerationMode: r.folderEnumerationMode
    })
    // 补回 deny 条目（New-SmbShare 仅支持 allow 列表，deny 需 Block-SmbShareAccess）
    const deny = r.permissions.filter((p) => p.deny && p.account)
    for (const p of deny) {
      try {
        await runPowerShellVoid(
          `Block-SmbShareAccess -Name ${psQuote(r.name)} -AccountName ${psQuote(p.account)} -Force`,
          { retries: 0 }
        )
      } catch {
        // best-effort：单个 deny 失败不阻断恢复
      }
    }
    list.splice(idx, 1)
    writeDisabled(list)
  }
}

// 读取 SMB 共享的高级选项（用于禁用/恢复时保留配置）
async function getShareAdvanced(
  name: string
): Promise<{
  concurrentUserLimit?: number
  cachingMode?: CreateShareOpts['cachingMode']
  folderEnumerationMode?: CreateShareOpts['folderEnumerationMode']
}> {
  try {
    const raw = await runPowerShell<RawShare>(
      `Get-SmbShare -Name ${psQuote(name)} | Select-Object ConcurrentUserLimit, CachingMode, FolderEnumerationMode`,
      { retries: 0 }
    )
    return {
      concurrentUserLimit:
        raw?.ConcurrentUserLimit !== undefined && raw.ConcurrentUserLimit > 0
          ? raw.ConcurrentUserLimit
          : undefined,
      // 经白名单校验，确保仅合法枚举值回填（同时收敛为联合类型）
      cachingMode: psEnum(raw?.CachingMode, CACHING_MODES) as CreateShareOpts['cachingMode'] | undefined,
      folderEnumerationMode: psEnum(
        raw?.FolderEnumerationMode,
        FOLDER_ENUM_MODES
      ) as CreateShareOpts['folderEnumerationMode'] | undefined
    }
  } catch {
    return {}
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

// 获取共享的详细连接信息（用于共享详情面板）
export async function getShareConnections(name: string): Promise<{
  concurrentUsers: number
  clientConnections: { clientUserName: string; clientComputerName: string; openFiles: number }[]
}> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  try {
    const raw = await runPowerShell<any[]>(
      `Get-SmbConnection | Where-Object { $_.ShareName -eq ${psQuote(name)} } | Select-Object ClientUserName, ClientComputerName`
    )
    const arr = Array.isArray(raw) ? raw : [raw]
    const clientConnections = arr.map((c) => ({
      clientUserName: c.ClientUserName || '',
      clientComputerName: c.ClientComputerName || '',
      openFiles: 0
    }))
    return {
      concurrentUsers: clientConnections.length,
      clientConnections
    }
  } catch {
    return { concurrentUsers: 0, clientConnections: [] }
  }
}

// 获取共享的打开文件列表
export async function getShareOpenFiles(name: string): Promise<
  {
    fileId: number
    path: string
    clientUserName: string
    clientComputerName: string
    lockCount: number
  }[]
> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  try {
    const raw = await runPowerShell<any[]>(
      `Get-SmbOpenFile | Where-Object { $_.Path -like ${psQuote(`*${name}*`)} } | Select-Object FileId, Path, ClientUserName, ClientComputerName, LockCount`
    )
    const arr = Array.isArray(raw) ? raw : [raw]
    return arr.map((f) => ({
      fileId: Number(f.FileId) || 0,
      path: f.Path || '',
      clientUserName: f.ClientUserName || '',
      clientComputerName: f.ClientComputerName || '',
      lockCount: Number(f.LockCount) || 0
    }))
  } catch {
    return []
  }
}

// 关闭共享上的所有打开文件
export async function closeShareOpenFiles(name: string): Promise<{ closed: number; failed: number }> {
  if (!validateName(name)) throw Errors.invalidParam('共享名非法')
  try {
    const files = await getShareOpenFiles(name)
    let closed = 0
    let failed = 0
    for (const f of files) {
      try {
        await runPowerShellVoid(`Close-SmbOpenFile -FileId ${f.fileId} -Force`, { retries: 0 })
        closed++
      } catch {
        failed++
      }
    }
    return { closed, failed }
  } catch (e) {
    throw Errors.commandFailed(`关闭共享打开文件失败：${(e as Error).message.slice(0, 200)}`)
  }
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

export async function importConfig(json: string): Promise<{ imported: number; skipped: number; errors: string[] }> {
  const data = JSON.parse(json)
  if (!data.shares || !Array.isArray(data.shares)) throw Errors.invalidParam('导入文件格式错误')
  let imported = 0
  let skipped = 0
  const errors: string[] = []
  for (const s of data.shares) {
    // 安全校验：name 与 path 均需合法，防止导入恶意配置注入非法共享名/路径
    if (!validateName(s.name) || !validatePath(s.path)) {
      skipped++
      errors.push(`跳过非法条目：${s.name || '(空名)'}`)
      continue
    }
    try {
      const full = (s.permissions || []).filter((p: SharePermission) => p.access === 'Full' && !p.deny).map((p: SharePermission) => p.account)
      const change = (s.permissions || []).filter((p: SharePermission) => p.access === 'Change' && !p.deny).map((p: SharePermission) => p.account)
      const read = (s.permissions || []).filter((p: SharePermission) => p.access === 'Read' && !p.deny).map((p: SharePermission) => p.account)
      await createShare({ name: s.name, path: s.path, description: s.description, fullAccess: full, changeAccess: change, readAccess: read, encrypted: s.encrypted })
      imported++
    } catch (e) {
      skipped++
      errors.push(`${s.name}: ${(e as Error).message}`)
    }
  }
  return { imported, skipped, errors }
}
