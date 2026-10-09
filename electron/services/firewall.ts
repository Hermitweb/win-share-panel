import { runPowerShell, runPowerShellVoid, psQuote, validateName } from '../lib/powershell'
import { Errors } from '../lib/errors'
import type { DesiredRule, FirewallRule } from '../types'

// ===== 防火墙规则引擎（NetSecurity cmdlet，Win8+/Server2012+ 可用）=====
// 设计：所有本应用创建的规则挂 DisplayGroup="WinShare Panel" 分组 + 固定 DisplayName，
// ensure 幂等（已存在不重建）；删除只允许组内规则，绝不误伤系统/第三方规则。

export const FW_GROUP = 'WinShare Panel'

// 规则名字符白名单。必须放行 "/"：presetRules 产出的预设名形如
// "WinShare SMB (445/TCP)"，早先白名单没有 "/" 导致 ensureRules(presetRules(kind))
// 恒抛 INVALID_PARAM——一键诊断的防火墙修复项与预设建/删规则在运行时全挂（qa-main t1 复现）。
// 安全性不变：这些名字最终经 psQuote 进 PowerShell 单引号字符串上下文，
// 单引号内无插值、"/" 与 "(" ")" 均为惰性字符，而真正的注入载体 "'" 仍被白名单挡在外面。
const NAME_RE = /^[\w ().\-/]{1,100}$/

/** 端口白名单：仅数字、逗号、连字符（范围）；先全量校验再写入，避免部分提交 */
const PORTS_RE = /^[\d,-]+$/

function validateDesired(r: DesiredRule): string {
  if (!NAME_RE.test(r.name ?? '')) throw Errors.invalidParam(`规则名非法：${r.name}`)
  if (!PORTS_RE.test(r.ports ?? '')) throw Errors.invalidParam(`端口格式非法：${r.ports}`)
  // 校验入参而不是归一化结果：原来写的是 proto !== 'TCP' && proto !== 'UDP'，
  // 上一行三元已把一切值归一成 TCP/UDP，那条判断永远为假（死分支，校验意图未落实）
  if (r.protocol !== undefined && r.protocol !== 'TCP' && r.protocol !== 'UDP') {
    throw Errors.invalidParam('协议非法')
  }
  return r.protocol === 'UDP' ? 'UDP' : 'TCP'
}

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
  // 先全量校验再发起任何写命令：原实现校验与写入在同一循环里交错，
  // [合法A, 非法B] 会先创建 A 再对 B 抛错——留下"部分提交"的中间态（qa-main t1 finding 2）
  const validated = desired.map((r) => ({ r, proto: validateDesired(r) }))
  const existing = new Set((await listManagedRules()).map((rule) => rule.name))
  const created: string[] = []
  for (const { r, proto } of validated) {
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
