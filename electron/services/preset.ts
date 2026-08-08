import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs'
import { join } from 'path'
import { homedir } from 'os'
import { Errors } from '../lib/errors'
import { validateName } from '../lib/powershell'
import { setSharePermissions, getSharePermissions } from './user'
import type { PermissionPreset, SharePermission, PresetEntry } from '../types'

// access 白名单：防止恶意 preset 注入 PowerShell 命令
const ALLOWED_ACCESS = new Set(['Full', 'Change', 'Read'])

function dataDir(): string {
  return join(process.env.APPDATA || homedir(), 'WinSharePanel')
}
function presetFile(): string {
  return join(dataDir(), 'presets.json')
}

// 内置模板占位符解析
export function resolveAccount(account: string): string {
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

// 内置模板定义（作为恢复默认的基准）
const BUILTIN: PermissionPreset[] = [
  {
    id: 'builtin-readonly',
    name: '只读团队',
    description: '所有人只读',
    builtIn: true,
    category: '基础',
    entries: [{ account: '{Everyone}', accountType: 'Group', access: 'Read', deny: false }]
  },
  {
    id: 'builtin-rw',
    name: '读写协作',
    description: '当前用户可改写，所有人只读',
    builtIn: true,
    category: '基础',
    entries: [
      { account: '{CurrentUser}', accountType: 'User', access: 'Change', deny: false },
      { account: '{Everyone}', accountType: 'Group', access: 'Read', deny: false }
    ]
  },
  {
    id: 'builtin-admin',
    name: '管理员全权',
    description: '管理员与当前用户完全控制',
    builtIn: true,
    category: '安全',
    entries: [
      { account: '{Administrators}', accountType: 'Group', access: 'Full', deny: false },
      { account: '{CurrentUser}', accountType: 'User', access: 'Full', deny: false }
    ]
  },
  {
    id: 'builtin-private',
    name: '私有独占',
    description: '仅当前用户完全控制，其他人显式拒绝',
    builtIn: true,
    category: '安全',
    entries: [
      { account: '{CurrentUser}', accountType: 'User', access: 'Full', deny: false },
      { account: '{Everyone}', accountType: 'Group', access: 'Read', deny: true }
    ]
  },
  {
    id: 'builtin-exchange',
    name: '交换区',
    description: '所有人可改写，管理员完全控制',
    builtIn: true,
    category: '协作',
    entries: [
      { account: '{Administrators}', accountType: 'Group', access: 'Full', deny: false },
      { account: '{Everyone}', accountType: 'Group', access: 'Change', deny: false }
    ]
  }
]

function readCustom(): PermissionPreset[] {
  try {
    if (!existsSync(presetFile())) return []
    const list = JSON.parse(readFileSync(presetFile(), 'utf8'))
    return Array.isArray(list) ? list : []
  } catch {
    return []
  }
}
function writeCustom(list: PermissionPreset[]): void {
  const dir = dataDir()
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true })
  writeFileSync(presetFile(), JSON.stringify(list, null, 2), 'utf8')
}

// 条目校验：防止恶意 preset 注入 PowerShell 命令
function validateEntries(entries: PresetEntry[]): void {
  for (const e of entries) {
    if (!ALLOWED_ACCESS.has(e.access)) {
      throw Errors.invalidParam(`预设条目 access 非法：${e.access}`)
    }
    // account 需为合法名称或占位符
    const resolved = resolveAccount(e.account)
    if (!resolved || !validateName(resolved)) {
      throw Errors.invalidParam(`预设条目账号非法：${e.account}`)
    }
    if (e.accountType !== 'User' && e.accountType !== 'Group') {
      throw Errors.invalidParam(`预设条目 accountType 非法：${e.accountType}`)
    }
  }
}

export async function listPresets(): Promise<PermissionPreset[]> {
  return [...BUILTIN, ...readCustom()]
}

export async function getPreset(id: string): Promise<PermissionPreset | null> {
  const all = await listPresets()
  return all.find((p) => p.id === id) || null
}

export async function savePreset(preset: PermissionPreset): Promise<void> {
  if (!validateName(preset.name)) throw Errors.invalidParam('模板名非法')
  if (preset.name.length > 40) throw Errors.invalidParam('模板名长度不能超过 40 字符')
  // 内置模板 ID 不允许被自定义覆盖
  if (BUILTIN.find((p) => p.id === preset.id)) {
    throw Errors.builtinProtected()
  }
  validateEntries(preset.entries || [])

  const list = readCustom()
  const now = new Date().toISOString()
  const idx = list.findIndex((p) => p.id === preset.id)
  const item: PermissionPreset = {
    ...preset,
    builtIn: false,
    entries: preset.entries || [],
    category: preset.category || '自定义',
    updatedAt: now,
    createdAt: idx >= 0 ? list[idx].createdAt || now : now
  }
  if (idx >= 0) list[idx] = item
  else list.push(item)
  writeCustom(list)
}

// 更新现有自定义模板（含 entries 编辑）
export async function updatePreset(id: string, updates: Partial<PermissionPreset>): Promise<void> {
  if (BUILTIN.find((p) => p.id === id)) {
    throw Errors.builtinProtected()
  }
  const list = readCustom()
  const idx = list.findIndex((p) => p.id === id)
  if (idx < 0) throw Errors.presetNotFound(id)

  const merged: PermissionPreset = {
    ...list[idx],
    ...updates,
    id, // 不允许改 ID
    builtIn: false,
    updatedAt: new Date().toISOString()
  }
  if (merged.name !== undefined && !validateName(merged.name)) {
    throw Errors.invalidParam('模板名非法')
  }
  if (merged.entries) validateEntries(merged.entries)
  list[idx] = merged
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

// 复制内置模板为自定义模板（便于基于内置模板二次编辑）
export async function duplicatePreset(id: string, newName?: string): Promise<PermissionPreset> {
  const all = await listPresets()
  const src = all.find((p) => p.id === id)
  if (!src) throw Errors.presetNotFound(id)
  const name = newName || `${src.name} 副本`
  if (!validateName(name)) throw Errors.invalidParam('模板名非法')
  const newPreset: PermissionPreset = {
    id: `custom-${Date.now()}`,
    name,
    description: src.description,
    builtIn: false,
    category: '自定义',
    entries: src.entries.map((e) => ({ ...e })),
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  }
  const list = readCustom()
  list.push(newPreset)
  writeCustom(list)
  return newPreset
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
        deny: !!e.deny
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
          deny: !!e.deny
        } as SharePermission
      })
      .filter((p): p is SharePermission => p !== null)
  }

  // 复用 user.setSharePermissions 的事务补偿逻辑（含备份+回滚）
  await setSharePermissions(shareName, targetPerms)
}

// 导出预设为 JSON（便于备份）
export async function exportPresets(): Promise<string> {
  const list = readCustom()
  return JSON.stringify(
    {
      exportedAt: new Date().toISOString(),
      version: 1,
      presets: list
    },
    null,
    2
  )
}

// 导入预设（合并模式：不覆盖同名）
export async function importPresets(
  json: string
): Promise<{ imported: number; skipped: number; errors: string[] }> {
  let data: { presets?: PermissionPreset[] }
  try {
    data = JSON.parse(json)
  } catch {
    throw Errors.invalidParam('导入文件格式错误：JSON 解析失败')
  }
  if (!data.presets || !Array.isArray(data.presets)) {
    throw Errors.invalidParam('导入文件格式错误：缺少 presets 字段')
  }

  const list = readCustom()
  const existingIds = new Set(list.map((p) => p.id))
  const existingNames = new Set(list.map((p) => p.name))
  let imported = 0
  let skipped = 0
  const errors: string[] = []

  for (const p of data.presets) {
    try {
      if (!p.name || !validateName(p.name)) {
        skipped++
        errors.push(`跳过非法模板名：${p.name || '(空名)'}`)
        continue
      }
      if (existingIds.has(p.id) || existingNames.has(p.name)) {
        skipped++
        errors.push(`跳过重复模板：${p.name}`)
        continue
      }
      validateEntries(p.entries || [])
      const newPreset: PermissionPreset = {
        id: p.id || `custom-${Date.now()}-${imported}`,
        name: p.name,
        description: p.description || '',
        builtIn: false,
        category: p.category || '导入',
        entries: p.entries || [],
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString()
      }
      list.push(newPreset)
      existingIds.add(newPreset.id)
      existingNames.add(newPreset.name)
      imported++
    } catch (e) {
      skipped++
      errors.push(`${p.name || '(空名)'}: ${(e as Error).message}`)
    }
  }

  if (imported > 0) writeCustom(list)
  return { imported, skipped, errors }
}
