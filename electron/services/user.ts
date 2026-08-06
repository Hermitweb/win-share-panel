import { runPowerShell, psQuote, validateName, validatePath } from '../lib/powershell'
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
  // overwrite：先清空再授予
  await runPowerShell(`Get-SmbShareAccess -Name ${psQuote(shareName)} | ForEach-Object { Revoke-SmbShareAccess -Name ${psQuote(shareName)} -AccountName $_.AccountName -Force }`)
  for (const p of perms) {
    if (!validateName(p.account)) continue
    const right = p.access === 'Full' ? 'Full' : p.access === 'Change' ? 'Change' : 'Read'
    await runPowerShell(`Grant-SmbShareAccess -Name ${psQuote(shareName)} -AccountName ${psQuote(p.account)} -AccessRight ${right} -Force`)
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
