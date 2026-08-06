import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs'
import { join } from 'path'
import { homedir } from 'os'
import { Errors } from '../lib/errors'
import { validateName } from '../lib/powershell'
import { setSharePermissions, getSharePermissions } from './user'
import type { PermissionPreset, SharePermission } from '../types'

// access 白名单：防止恶意 preset 注入 PowerShell 命令
const ALLOWED_ACCESS = new Set(['Full', 'Change', 'Read'])

function dataDir(): string {
  return join(process.env.APPDATA || homedir(), 'WinSharePanel')
}
function presetFile(): string {
  return join(dataDir(), 'presets.json')
}

// 内置模板占位符解析
function resolveAccount(account: string): string {
  switch (account) {
    case '{Everyone}':
      return 'Everyone'
    case '{Administrators}':
      return 'Administrators'
    case '{CurrentUser}':
      return process.env.USERNAME || process.env.USER || ''
    default:
      return account
  }
}

const BUILTIN: PermissionPreset[] = [
  {
    id: 'builtin-readonly',
    name: '只读团队',
    description: '所有人只读',
    builtIn: true,
    entries: [{ account: '{Everyone}', accountType: 'Group', access: 'Read' }]
  },
  {
    id: 'builtin-rw',
    name: '读写协作',
    description: '当前用户可改写，所有人只读',
    builtIn: true,
    entries: [
      { account: '{CurrentUser}', accountType: 'User', access: 'Change' },
      { account: '{Everyone}', accountType: 'Group', access: 'Read' }
    ]
  },
  {
    id: 'builtin-admin',
    name: '管理员全权',
    description: '管理员与当前用户完全控制',
    builtIn: true,
    entries: [
      { account: '{Administrators}', accountType: 'Group', access: 'Full' },
      { account: '{CurrentUser}', accountType: 'User', access: 'Full' }
    ]
  }
]

function readCustom(): PermissionPreset[] {
  try {
    if (!existsSync(presetFile())) return []
    return JSON.parse(readFileSync(presetFile(), 'utf8'))
  } catch {
    return []
  }
}
function writeCustom(list: PermissionPreset[]): void {
  const dir = dataDir()
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true })
  writeFileSync(presetFile(), JSON.stringify(list, null, 2), 'utf8')
}

export async function listPresets(): Promise<PermissionPreset[]> {
  return [...BUILTIN, ...readCustom()]
}

export async function savePreset(preset: PermissionPreset): Promise<void> {
  if (!validateName(preset.name)) throw Errors.invalidParam('模板名非法')
  const list = readCustom()
  const idx = list.findIndex((p) => p.id === preset.id)
  const item: PermissionPreset = { ...preset, builtIn: false }
  if (idx >= 0) list[idx] = item
  else list.push(item)
  writeCustom(list)
}

export async function deletePreset(id: string): Promise<void> {
  if (BUILTIN.find((p) => p.id === id)) throw Errors.builtinProtected()
  const list = readCustom()
  const idx = list.findIndex((p) => p.id === id)
  if (idx < 0) throw Errors.presetNotFound(id)
  list.splice(idx, 1)
  writeCustom(list)
}

export async function applyPreset(
  shareName: string,
  presetId: string,
  mode: 'overwrite' | 'merge'
): Promise<void> {
  if (!validateName(shareName)) throw Errors.invalidParam('共享名非法')
  const all = await listPresets()
  const preset = all.find((p) => p.id === presetId)
  if (!preset) throw Errors.presetNotFound(presetId)

  // 安全校验：access 必须在白名单内，防止恶意 preset 注入 PowerShell 命令
  for (const e of preset.entries) {
    if (!ALLOWED_ACCESS.has(e.access)) {
      throw Errors.invalidParam(`预设条目 access 非法：${e.access}`)
    }
  }

  // 构造 SharePermission[]：merge 模式需先读取现有权限再合并
  let targetPerms: SharePermission[]
  if (mode === 'merge') {
    // merge：保留现有权限，仅追加 preset 中的条目（同账号后者覆盖前者）
    const existing = await getSharePermissions(shareName).catch(() => [] as SharePermission[])
    const map = new Map<string, SharePermission>()
    for (const p of existing) map.set(p.account, p)
    for (const e of preset.entries) {
      const acct = resolveAccount(e.account)
      if (!acct) continue
      map.set(acct, {
        shareName,
        account: acct,
        accountType: e.accountType,
        access: e.access,
        deny: false
      })
    }
    targetPerms = Array.from(map.values())
  } else {
    // overwrite：仅应用 preset 条目
    targetPerms = preset.entries
      .map((e) => {
        const acct = resolveAccount(e.account)
        if (!acct) return null
        return {
          shareName,
          account: acct,
          accountType: e.accountType,
          access: e.access,
          deny: false
        } as SharePermission
      })
      .filter((p): p is SharePermission => p !== null)
  }

  // 复用 user.setSharePermissions 的事务补偿逻辑（含备份+回滚）
  await setSharePermissions(shareName, targetPerms)
}
