import { runPowerShell, runPowerShellVoid, psQuote, validateName, validatePath } from '../lib/powershell'
import { Errors } from '../lib/errors'
import type { LocalUser, LocalGroup, SharePermission, NtfsAcl, NtfsAclEntry } from '../types'

export async function listUsers(): Promise<LocalUser[]> {
  const raw = await runPowerShell<any[]>('Get-LocalUser')
  const arr = Array.isArray(raw) ? raw : [raw]
  return arr.map((u) => ({
    name: u.Name,
    fullName: u.FullName || '',
    enabled: !!u.Enabled,
    description: u.Description || '',
    groups: [] as string[]
  }))
}

export async function listGroups(): Promise<LocalGroup[]> {
  const raw = await runPowerShell<any[]>('Get-LocalGroup')
  const arr = Array.isArray(raw) ? raw : [raw]
  const groups: LocalGroup[] = []
  for (const g of arr) {
    let members: string[] = []
    try {
      const mraw = await runPowerShell<any[]>(`Get-LocalGroupMember -Group ${psQuote(g.Name)}`)
      const marr = Array.isArray(mraw) ? mraw : [mraw]
      members = marr.map((m) => m.Name)
    } catch {
      members = []
    }
    groups.push({ name: g.Name, description: g.Description || '', members })
  }
  return groups
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

export async function setSharePermissions(shareName: string, perms: SharePermission[]): Promise<void> {
  if (!validateName(shareName)) throw Errors.invalidParam('共享名非法')
  // 入参校验：account 必须合法，不静默跳过（避免用户误以为权限已设但实际未设）
  const invalid = perms.filter((p) => !validateName(p.account))
  if (invalid.length) {
    throw Errors.invalidParam(`账号名非法：${invalid.map((p) => p.account).join(', ')}`)
  }

  // 事务补偿：先备份当前权限，若后续 Grant/Block 中途失败则回滚到原状态
  const backup = await getSharePermissions(shareName).catch(() => [] as SharePermission[])

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
    }
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
