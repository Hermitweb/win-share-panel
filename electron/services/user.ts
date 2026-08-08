import { runPowerShell, runPowerShellVoid, psQuote, validateName, validatePath } from '../lib/powershell'
import { Errors } from '../lib/errors'
import type { LocalUser, LocalGroup, GroupMember, SharePermission, NtfsAcl, NtfsAclEntry } from '../types'

// 解析时间字段（PowerShell 返回的 WMI 时间可能为 /Date(...)/ 或字符串）
function parseTime(raw: unknown): string {
  if (!raw) return ''
  const s = String(raw)
  // /Date(123456789000+000)/ 格式
  const m = s.match(/\/Date\((\d+)(?:[+-]\d+)?\)\//)
  if (m) {
    const ts = parseInt(m[1], 10)
    if (!isNaN(ts)) return new Date(ts).toISOString()
  }
  return s
}

export async function listUsers(): Promise<LocalUser[]> {
  const raw = await runPowerShell<any[]>(
    `Get-LocalUser | Select-Object Name, FullName, Enabled, Description, PasswordRequired, PasswordChangeable, PasswordExpires, UserMayChangePassword, PasswordLastSet, LastLogon, SID, PrincipalSource`
  )
  const arr = Array.isArray(raw) ? raw : [raw]
  // 批量获取每个用户所属组（一次性查询 Get-LocalGroup 后 group by member，避免 N+1）
  const groupMap = await buildUserGroupMap()

  return arr.map((u) => ({
    name: u.Name,
    fullName: u.FullName || '',
    enabled: !!u.Enabled,
    description: u.Description || '',
    groups: groupMap[u.Name] || [],
    passwordRequired: !!u.PasswordRequired,
    passwordChangeable: !!u.PasswordChangeable,
    passwordExpires: !!u.PasswordExpires,
    userMayChangePassword: !!u.UserMayChangePassword,
    passwordLastSet: parseTime(u.PasswordLastSet),
    lastLogon: parseTime(u.LastLogon),
    sid: u.SID?.Value || u.SID || '',
    principalSource: u.PrincipalSource || 'Local'
  }))
}

// 构造 user -> groups 映射，避免对每个用户单独查询（N+1 优化）
async function buildUserGroupMap(): Promise<Record<string, string[]>> {
  const map: Record<string, string[]> = {}
  try {
    const groups = await runPowerShell<any[]>('Get-LocalGroup | Select-Object Name')
    const garr = Array.isArray(groups) ? groups : [groups]
    for (const g of garr) {
      if (!g || !g.Name) continue
      try {
        const members = await runPowerShell<any[]>(
          `Get-LocalGroupMember -Group ${psQuote(g.Name)} | Select-Object Name, ObjectClass, PrincipalSource`,
          { retries: 0 }
        )
        const marr = Array.isArray(members) ? members : [members]
        for (const m of marr) {
          if (!m || !m.Name) continue
          // Name 形如 "COMPUTERNAME\\username"，取反斜杠后部分
          const userName = String(m.Name).split('\\').pop() || m.Name
          if (!map[userName]) map[userName] = []
          if (!map[userName].includes(g.Name)) map[userName].push(g.Name)
        }
      } catch {
        // 某些组（如特殊系统组）查询成员可能失败
      }
    }
  } catch {
    // 整体失败时返回空映射，用户列表仍可用
  }
  return map
}

export async function listGroups(): Promise<LocalGroup[]> {
  const raw = await runPowerShell<any[]>('Get-LocalGroup | Select-Object Name, Description')
  const arr = Array.isArray(raw) ? raw : [raw]
  const groups: LocalGroup[] = []
  for (const g of arr) {
    let members: GroupMember[] = []
    try {
      const mraw = await runPowerShell<any[]>(
        `Get-LocalGroupMember -Group ${psQuote(g.Name)} | Select-Object Name, ObjectClass, PrincipalSource`,
        { retries: 0 }
      )
      const marr = Array.isArray(mraw) ? mraw : [mraw]
      members = marr.map((m) => ({
        // Name 形如 "COMPUTERNAME\\username"
        name: String(m.Name).split('\\').pop() || m.Name,
        objectClass: m.ObjectClass === 'Group' ? 'Group' : 'User',
        principalSource: m.PrincipalSource || 'Local'
      }))
    } catch {
      members = []
    }
    groups.push({ name: g.Name, description: g.Description || '', members })
  }
  return groups
}

// 获取单个用户详情
export async function getUser(name: string): Promise<LocalUser> {
  if (!validateName(name)) throw Errors.invalidParam('用户名非法')
  const raw = await runPowerShell<any>(
    `Get-LocalUser -Name ${psQuote(name)} | Select-Object Name, FullName, Enabled, Description, PasswordRequired, PasswordChangeable, PasswordExpires, UserMayChangePassword, PasswordLastSet, LastLogon, SID, PrincipalSource`
  )
  if (!raw || !raw.Name) throw Errors.invalidParam(`用户 ${name} 不存在`)
  const groupMap = await buildUserGroupMap()
  return {
    name: raw.Name,
    fullName: raw.FullName || '',
    enabled: !!raw.Enabled,
    description: raw.Description || '',
    groups: groupMap[raw.Name] || [],
    passwordRequired: !!raw.PasswordRequired,
    passwordChangeable: !!raw.PasswordChangeable,
    passwordExpires: !!raw.PasswordExpires,
    userMayChangePassword: !!raw.UserMayChangePassword,
    passwordLastSet: parseTime(raw.PasswordLastSet),
    lastLogon: parseTime(raw.LastLogon),
    sid: raw.SID?.Value || raw.SID || '',
    principalSource: raw.PrincipalSource || 'Local'
  }
}

export interface CreateUserOpts {
  name: string
  password: string
  fullName?: string
  description?: string
  enabled?: boolean
  passwordChangeable?: boolean
  passwordExpires?: boolean
}

export async function createUser(opts: CreateUserOpts): Promise<void> {
  if (!validateName(opts.name)) throw Errors.invalidParam('用户名非法')
  if (!opts.password || opts.password.length < 1) throw Errors.invalidParam('密码不能为空')
  if (opts.password.length > 127) throw Errors.invalidParam('密码长度不能超过 127 字符')
  const parts = [
    'New-LocalUser',
    `-Name ${psQuote(opts.name)}`,
    `-Password (ConvertTo-SecureString -AsPlainText -Force ${psQuote(opts.password)})`
  ]
  if (opts.fullName !== undefined) parts.push(`-FullName ${psQuote(opts.fullName)}`)
  if (opts.description !== undefined) parts.push(`-Description ${psQuote(opts.description)}`)
  if (opts.enabled === false) parts.push('-Disabled')
  if (opts.passwordChangeable === false) parts.push('-UserMayNotChangePassword')
  if (opts.passwordExpires === true) parts.push('-PasswordNeverExpires:$false')
  else parts.push('-PasswordNeverExpires')
  // 不创建 profile，避免某些系统上的副作用
  parts.push('-NoProfile')
  await runPowerShellVoid(parts.join(' '), { retries: 0 })
}

export interface UpdateUserOpts {
  fullName?: string
  description?: string
  enabled?: boolean
  passwordChangeable?: boolean
  passwordExpires?: boolean
}

export async function updateUser(name: string, opts: UpdateUserOpts): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('用户名非法')
  const parts = ['Set-LocalUser', `-Name ${psQuote(name)}`]
  if (opts.fullName !== undefined) parts.push(`-FullName ${psQuote(opts.fullName)}`)
  if (opts.description !== undefined) parts.push(`-Description ${psQuote(opts.description)}`)
  if (opts.enabled === false) parts.push('-Disabled')
  else if (opts.enabled === true) parts.push('-Enabled')
  if (opts.passwordChangeable === false) parts.push('-UserMayNotChangePassword')
  else if (opts.passwordChangeable === true) parts.push('-UserMayChangePassword')
  if (opts.passwordExpires === true) parts.push('-PasswordNeverExpires:$false')
  else if (opts.passwordExpires === false) parts.push('-PasswordNeverExpires')
  await runPowerShellVoid(parts.join(' '), { retries: 0 })
}

export async function deleteUser(name: string): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('用户名非法')
  // 系统保护：内置 Administrator/Guest/DefaultAccount 等不允许删除
  const protectedNames = ['administrator', 'guest', 'defaultaccount', 'wdagutilityaccount']
  if (protectedNames.includes(name.toLowerCase())) {
    throw Errors.invalidParam(`系统内置用户 ${name} 不允许删除`)
  }
  await runPowerShellVoid(`Remove-LocalUser -Name ${psQuote(name)}`, { retries: 0 })
}

export async function setUserPassword(name: string, password: string): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('用户名非法')
  if (!password || password.length < 1) throw Errors.invalidParam('密码不能为空')
  if (password.length > 127) throw Errors.invalidParam('密码长度不能超过 127 字符')
  await runPowerShellVoid(
    `Set-LocalUser -Name ${psQuote(name)} -Password (ConvertTo-SecureString -AsPlainText -Force ${psQuote(password)})`,
    { retries: 0 }
  )
}

export async function enableUser(name: string): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('用户名非法')
  await runPowerShellVoid(`Enable-LocalUser -Name ${psQuote(name)}`, { retries: 0 })
}

export async function disableUser(name: string): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('用户名非法')
  await runPowerShellVoid(`Disable-LocalUser -Name ${psQuote(name)}`, { retries: 0 })
}

export async function renameUser(oldName: string, newName: string): Promise<void> {
  if (!validateName(oldName)) throw Errors.invalidParam('原用户名非法')
  if (!validateName(newName)) throw Errors.invalidParam('新用户名非法')
  if (oldName === newName) return
  await runPowerShellVoid(`Rename-LocalUser -Name ${psQuote(oldName)} -NewName ${psQuote(newName)}`, {
    retries: 0
  })
}

// === 组管理 ===

export interface CreateGroupOpts {
  name: string
  description?: string
}

export async function createGroup(opts: CreateGroupOpts): Promise<void> {
  if (!validateName(opts.name)) throw Errors.invalidParam('组名非法')
  const parts = ['New-LocalGroup', `-Name ${psQuote(opts.name)}`]
  if (opts.description !== undefined) parts.push(`-Description ${psQuote(opts.description)}`)
  await runPowerShellVoid(parts.join(' '), { retries: 0 })
}

// 系统内置组保护列表（删除/重命名均禁止）
const PROTECTED_GROUPS = [
  'administrators',
  'users',
  'guests',
  'power users',
  'account operators',
  'server operators',
  'print operators',
  'backup operators',
  'replicator',
  'iis_iusrs',
  'network configuration operators',
  'performance monitor users',
  'performance log users',
  'remote desktop users',
  'system operators',
  'cryptographic operators',
  'event log readers',
  'certificate service dcom access'
]

export async function deleteGroup(name: string): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('组名非法')
  // 系统保护：内置组不允许删除（复用 PROTECTED_GROUPS 常量）
  if (PROTECTED_GROUPS.includes(name.toLowerCase())) {
    throw Errors.invalidParam(`系统内置组 ${name} 不允许删除`)
  }
  await runPowerShellVoid(`Remove-LocalGroup -Name ${psQuote(name)}`, { retries: 0 })
}

export async function updateGroup(name: string, description: string): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('组名非法')
  await runPowerShellVoid(
    `Set-LocalGroup -Name ${psQuote(name)} -Description ${psQuote(description)}`,
    { retries: 0 }
  )
}

export async function renameGroup(name: string, newName: string): Promise<void> {
  if (!validateName(name)) throw Errors.invalidParam('原组名非法')
  if (!validateName(newName)) throw Errors.invalidParam('新组名非法')
  if (name === newName) return
  if (PROTECTED_GROUPS.includes(name.toLowerCase())) {
    throw Errors.invalidParam(`系统内置组 ${name} 不允许重命名`)
  }
  await runPowerShellVoid(`Rename-LocalGroup -Name ${psQuote(name)} -NewName ${psQuote(newName)}`, {
    retries: 0
  })
}

export async function addGroupMember(group: string, member: string): Promise<void> {
  if (!validateName(group)) throw Errors.invalidParam('组名非法')
  if (!validateName(member)) throw Errors.invalidParam('成员名非法')
  await runPowerShellVoid(
    `Add-LocalGroupMember -Group ${psQuote(group)} -Member ${psQuote(member)}`,
    { retries: 0 }
  )
}

export async function removeGroupMember(group: string, member: string): Promise<void> {
  if (!validateName(group)) throw Errors.invalidParam('组名非法')
  if (!validateName(member)) throw Errors.invalidParam('成员名非法')
  await runPowerShellVoid(
    `Remove-LocalGroupMember -Group ${psQuote(group)} -Member ${psQuote(member)}`,
    { retries: 0 }
  )
}

export async function getSharePermissions(shareName: string): Promise<SharePermission[]> {
  if (!validateName(shareName)) throw Errors.invalidParam('共享名非法')
  const r = await runPowerShell<any[]>(`Get-SmbShareAccess -Name ${psQuote(shareName)}`)
  const arr = Array.isArray(r) ? r : [r]
  return arr.map((x) => ({
    shareName,
    account: x.AccountName,
    accountType: x.AccountType === 1 ? 'User' : 'Group',
    access: x.AccessRight === 0 ? 'Full' : x.AccessRight === 1 ? 'Change' : 'Read',
    deny: x.AccessControlType === 1
  }))
}

// 按用户名查询该用户在所有 SMB 共享上的权限（单次 PowerShell 批量查询）
// 匹配规则：AccountName 等于用户名 或 以 \用户名 结尾（如 COMPUTERNAME\Admin）
export async function getUserSharePermissions(username: string): Promise<SharePermission[]> {
  if (!validateName(username)) throw Errors.invalidParam('用户名非法')
  const r = await runPowerShell<any[]>(
    `Get-SmbShare | ForEach-Object { $sn = $_.Name; Get-SmbShareAccess -Name $sn -ErrorAction SilentlyContinue | ForEach-Object { [PSCustomObject]@{ shareName = $sn; account = $_.AccountName; accessRight = $_.AccessRight; accountType = $_.AccountType; accessControlType = $_.AccessControlType } } } | Where-Object { $_.account -ieq ${psQuote(username)} -or $_.account -ilike "*\\${username}" }`
  )
  const arr = Array.isArray(r) ? r : r ? [r] : []
  return arr.map((x) => ({
    shareName: x.shareName,
    account: x.account,
    accountType: x.accountType === 1 ? 'User' : 'Group',
    access: x.accessRight === 0 ? 'Full' : x.accessRight === 1 ? 'Change' : 'Read',
    deny: x.accessControlType === 1
  }))
}

export async function setSharePermissions(shareName: string, perms: SharePermission[]): Promise<void> {
  if (!validateName(shareName)) throw Errors.invalidParam('共享名非法')
  // 入参校验：account 必须合法，不静默跳过（避免用户误以为权限已设但实际未设）
  const invalid = perms.filter((p) => !validateName(p.account))
  if (invalid.length) {
    throw Errors.invalidParam(`账号名非法：${invalid.map((p) => p.account).join(', ')}`)
  }

  // 事务补偿：先备份当前权限，若后续 Grant/Block 中途失败则回滚到原状态
  const backup = await getSharePermissions(shareName).catch(() => [] as SharePermission[])
  console.log('[setPermissions:smb] 已备份当前权限:', backup.length, '条 →', backup.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')

  // overwrite：先清空所有已授予与已拒绝项
  await runPowerShellVoid(
    `Get-SmbShareAccess -Name ${psQuote(shareName)} | ForEach-Object { Revoke-SmbShareAccess -Name ${psQuote(shareName)} -AccountName $_.AccountName -Force }`
  )
  // Unblock 当前所有 Deny 项（Revoke 不一定清除 Block，需显式 Unblock）
  try {
    await runPowerShellVoid(
      `Get-SmbShareAccess -Name ${psQuote(shareName)} | Where-Object { $_.AccessControlType -eq 1 } | ForEach-Object { Unblock-SmbShareAccess -Name ${psQuote(shareName)} -AccountName $_.AccountName -Force }`
    )
  } catch {
    // 旧版 Windows 可能无 Unblock-SmbShareAccess cmdlet，降级：仅 Revoke 已足够清空
  }

  // 逐个 Grant/Block，收集失败项
  const failed: string[] = []
  for (const p of perms) {
    if (p.deny || p.access === 'NoAccess') {
      try {
        await runPowerShellVoid(
          `Block-SmbShareAccess -Name ${psQuote(shareName)} -AccountName ${psQuote(p.account)} -Force`
        )
      } catch {
        // 无 Block-SmbShareAccess 时降级为"不授予"（仅 Revoke 状态）
        failed.push(p.account)
      }
    } else {
      const right = p.access === 'Full' ? 'Full' : p.access === 'Change' ? 'Change' : 'Read'
      try {
        await runPowerShellVoid(
          `Grant-SmbShareAccess -Name ${psQuote(shareName)} -AccountName ${psQuote(p.account)} -AccessRight ${right} -Force`
        )
      } catch {
        failed.push(p.account)
      }
    }
  }

  // 若有失败项：尝试回滚到备份状态，保证共享不处于"全空"危险状态
  if (failed.length > 0) {
    console.error('[setPermissions:smb] 回滚触发！失败账号:', failed.join(', '))
    // 查询回滚前的当前权限状态（部分授予后的残留状态）
    const beforeRollback = await getSharePermissions(shareName).catch(() => [] as SharePermission[])
    console.log('[setPermissions:smb] 回滚前权限状态:', beforeRollback.length, '条 →', beforeRollback.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
    // 静默回滚：重新清空 + 重新授予备份权限
    try {
      await runPowerShellVoid(
        `Get-SmbShareAccess -Name ${psQuote(shareName)} | ForEach-Object { Revoke-SmbShareAccess -Name ${psQuote(shareName)} -AccountName $_.AccountName -Force }`
      )
      for (const p of backup) {
        if (p.deny) {
          await runPowerShellVoid(
            `Block-SmbShareAccess -Name ${psQuote(shareName)} -AccountName ${psQuote(p.account)} -Force`
          ).catch(() => undefined)
        } else {
          const right = p.access === 'Full' ? 'Full' : p.access === 'Change' ? 'Change' : 'Read'
          await runPowerShellVoid(
            `Grant-SmbShareAccess -Name ${psQuote(shareName)} -AccountName ${psQuote(p.account)} -AccessRight ${right} -Force`
          ).catch(() => undefined)
        }
      }
    } catch {
      // 回滚失败：已尽力，抛出原始错误
      console.error('[setPermissions:smb] 回滚过程出错！')
    }
    // 查询回滚后的权限状态，验证是否恢复成功
    const afterRollback = await getSharePermissions(shareName).catch(() => [] as SharePermission[])
    console.log('[setPermissions:smb] 回滚后权限状态:', afterRollback.length, '条 →', afterRollback.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`).join(', ') || '(空)')
    throw Errors.commandFailed(
      `部分权限设置失败（${failed.length} 个账号：${failed.slice(0, 3).join(', ')}${failed.length > 3 ? ' 等' : ''}），已回滚到原状态`
    )
  }
}

export async function getNtfsPermissions(path: string): Promise<NtfsAcl> {
  if (!validatePath(path)) throw Errors.invalidParam('路径非法')
  const raw = await runPowerShell<any>(`Get-Acl -Path ${psQuote(path)} | Select-Object -ExpandProperty Access`)
  const arr = Array.isArray(raw) ? raw : [raw]
  const entries: NtfsAclEntry[] = arr.map((a) => ({
    account: a.IdentityReference?.toString() || '',
    rights: a.FileSystemRights?.toString() || '',
    type: a.AccessControlType === 0 ? 'Allow' : 'Deny',
    inherited: !!a.IsInherited
  }))
  return { path, entries }
}
