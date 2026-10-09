import { runPowerShell, runPowerShellVoid, psQuote, validateName } from '../lib/powershell'
import { Errors } from '../lib/errors'
import type { DesiredRule, FirewallRule } from '../types'

// ===== 防火墙规则引擎（NetSecurity cmdlet，Win8+/Server2012+ 可用）=====
// 设计：所有本应用创建的规则挂 DisplayGroup="WinShare Panel" 分组 + 固定 DisplayName，
// ensure 幂等（已存在不重建）；删除只允许组内规则，绝不误伤系统/第三方规则。

export const FW_GROUP = 'WinShare Panel'

const NAME_RE = /^[\w ()\-.]{1,100}$/

export async function listManagedRules(): Promise<FirewallRule[]> {
  const raw = await runPowerShell<any | any[]>(
    `$rules = @(); try { $rules = @(Get-NetFirewallRule -DisplayGroup ${psQuote(FW_GROUP)} -ErrorAction Stop) } catch { return @() }` +
      `; $rules | ForEach-Object { $p = $_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue; ` +
      `[PSCustomObject]@{ DisplayName=$_.DisplayName; Enabled=($_.Enabled -eq 'True'); Ports=(($p.LocalPort) -join ',') } }`,
    { retries: 0 },
  )
  const arr = Array.isArray(raw) ? raw : raw ? [raw] : []
  return arr
    .filter((r) => r && r.DisplayName)
    .map((r) => ({
      name: String(r.DisplayName),
      enabled: Boolean(r.Enabled),
      ports: String(r.Ports ?? ''),
    }))
}

/** 幂等确保规则存在（入站允许）；返回本次新建的规则名 */
export async function ensureRules(desired: DesiredRule[]): Promise<string[]> {
  if (!Array.isArray(desired) || desired.length === 0) return []
  if (desired.length > 20) throw Errors.invalidParam('单次规则请求过多')
  const existing = new Set((await listManagedRules()).map((r) => r.name))
  const created: string[] = []
  for (const r of desired) {
    if (!NAME_RE.test(r.name ?? '')) throw Errors.invalidParam(`规则名非法：${r.name}`)
    if (!/^[\d,-]+$/.test(r.ports ?? '')) throw Errors.invalidParam(`端口格式非法：${r.ports}`)
    const proto = r.protocol === 'UDP' ? 'UDP' : 'TCP'
    if (proto !== 'TCP' && proto !== 'UDP') throw Errors.invalidParam('协议非法')
    if (existing.has(r.name)) continue
    const ports = r.ports
      .split(',')
      .map((p) => p.trim())
      .filter(Boolean)
      .map((p) => psQuote(p))
      .join(',')
    await runPowerShellVoid(
      `New-NetFirewallRule -DisplayName ${psQuote(r.name)} -Direction Inbound -Action Allow ` +
        `-Protocol ${psQuote(proto)} -LocalPort ${ports} -Profile Any -Group ${psQuote(FW_GROUP)}`,
      { retries: 0 },
    )
    created.push(r.name)
  }
  return created
}

/** 删除组内规则（按 DisplayName 精确匹配，且必须在 WinShare Panel 组内） */
export async function removeRule(name: string): Promise<void> {
  if (!NAME_RE.test(name ?? '')) throw Errors.invalidParam('规则名非法')
  await runPowerShellVoid(
    `$ids = @(Get-NetFirewallRule -DisplayGroup ${psQuote(FW_GROUP)} -ErrorAction SilentlyContinue | ` +
      `Where-Object { $_.DisplayName -eq ${psQuote(name)} } | Select-Object -ExpandProperty Name); ` +
      `if ($ids.Count -gt 0) { $ids | Remove-NetFirewallRule }`,
    { retries: 0 },
  )
}

/** 常用预设：SMB(445) / FTP 控制(21)+被动段 / WebDAV(80) —— 参数化端口供 UI 传入 */
export function presetRules(
  kind: string,
  opts?: { passiveFrom?: number; passiveTo?: number },
): DesiredRule[] {
  switch (kind) {
    case 'smb':
      return [{ name: 'WinShare SMB (445/TCP)', ports: '445' }]
    case 'ftp':
      return [
        { name: 'WinShare FTP (21/TCP)', ports: '21' },
        {
          name: 'WinShare FTP Passive (50000-51000/TCP)',
          ports: '50000-51000',
        },
      ]
    case 'ftpPassive': {
      const from = Number.isInteger(opts?.passiveFrom) ? (opts?.passiveFrom as number) : 50000
      const to = Number.isInteger(opts?.passiveTo) ? (opts?.passiveTo as number) : 51000
      if (from < 1 || to > 65535 || from > to) throw Errors.invalidParam('被动端口范围非法')
      return [
        {
          name: `WinShare FTP Passive (${from}-${to}/TCP)`,
          ports: `${from}-${to}`,
        },
      ]
    }
    case 'webdav':
      return [{ name: 'WinShare WebDAV (80/TCP)', ports: '80' }]
    case 'quic':
      return [{ name: 'WinShare SMB QUIC (445/UDP)', ports: '445', protocol: 'UDP' }]
    default:
      throw Errors.invalidParam(`未知防火墙预设：${validateName(kind) ? kind : '***'}`)
  }
}
