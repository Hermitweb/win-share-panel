import { existsSync } from 'fs'
import {
  runPowerShell,
  runPowerShellVoid,
  psQuote,
  validateShareName,
  validatePath,
} from '../lib/powershell'
import { Errors } from '../lib/errors'
import { audit } from '../lib/audit'
import * as smb from './smb'
import * as user from './user'
import { listManagedRules, ensureRules } from './firewall'
import type { DiagnoseFixKind, DiagnoseItem } from '../types'

// ===== 一键诊断："别人连不上我的共享" 自助排查 =====
// 每项 check 独立容错：探测失败记 warn 而不是整体崩；fix 键驱动可自动修复项。
// 链路与用户旅程一致：服务在跑 → 端口在听 → 共享存在 → 路径有效 → NTFS 可读
// → 共享权限 → 防火墙放行 → 安全策略（SMB1/访客）。

type Fix = DiagnoseFixKind

async function push(
  items: DiagnoseItem[],
  key: string,
  label: string,
  fn: () => Promise<{ ok: boolean; detail: string; fix?: Fix }>,
): Promise<void> {
  try {
    const r = await fn()
    items.push({ key, label, status: r.ok ? 'pass' : 'fail', detail: r.detail, fix: r.fix })
  } catch (e) {
    items.push({ key, label, status: 'warn', detail: `探测失败：${(e as Error).message}` })
  }
}

/** 解析共享的本地路径（不存在/非法返回 null）；一次查询多处复用 */
async function resolveSharePath(shareName: string): Promise<string | null> {
  if (!validateShareName(shareName)) throw Errors.invalidParam('共享名非法')
  const raw = await runPowerShell<unknown>(
    `try { Get-SmbShare -Name ${psQuote(shareName)} -ErrorAction Stop | Select-Object -ExpandProperty Path } catch { $null }`,
    { retries: 0 },
  )
  const p = Array.isArray(raw) ? raw[0] : raw
  return typeof p === 'string' && p ? p : null
}

export async function runDiagnose(opts: { shareName?: string }): Promise<DiagnoseItem[]> {
  const items: DiagnoseItem[] = []
  // 闭包内需要非空收窄，先用局部常量接住（opts.shareName 在闭包中会被重新收窄为可选）
  const target = opts.shareName
  let pathProbe: string | null | undefined
  let probed = false
  async function probePath(): Promise<string | null> {
    if (!target) return null
    if (!probed) {
      probed = true
      pathProbe = await resolveSharePath(target)
    }
    return pathProbe ?? null
  }

  await push(items, 'smb-service', 'SMB 服务端（LanmanServer）运行', async () => {
    const svc = await smb.getServiceStatus()
    return {
      ok: svc.status === 'Running',
      detail: `状态：${svc.status}（${svc.startType}）`,
      fix: svc.status !== 'Running' ? 'service:lanman' : undefined,
    }
  })

  await push(items, 'port-445', 'SMB 端口 445 已监听', async () => {
    const raw = await runPowerShell<unknown>(
      '@(Get-NetTCPConnection -LocalPort 445 -State Listen -ErrorAction SilentlyContinue).Count',
      { retries: 0 },
    )
    const n = Number(Array.isArray(raw) ? raw[0] : raw)
    return { ok: n > 0, detail: n > 0 ? `${n} 个监听` : '445 未监听（服务未起或被占用）' }
  })

  await push(items, 'share-exists', '目标共享存在', async () => {
    if (!target) return { ok: true, detail: '未指定共享，跳过' }
    const path = await probePath()
    return {
      ok: Boolean(path),
      detail: path ? `共享路径：${path}` : `共享"${target}"不存在`,
    }
  })

  await push(items, 'path-exists', '共享本地路径存在', async () => {
    if (!target) return { ok: true, detail: '未指定共享，跳过' }
    const path = await probePath()
    if (!path) return { ok: false, detail: '共享不存在（见上项）' }
    return {
      ok: existsSync(path),
      detail: existsSync(path) ? path : `路径不存在：${path}`,
    }
  })

  await push(items, 'ntfs-read', '共享路径 NTFS 已授予读取权限', async () => {
    if (!target) return { ok: true, detail: '未指定共享，跳过' }
    const path = await probePath()
    if (!path) return { ok: false, detail: '共享不存在（见上项）' }
    if (!existsSync(path)) return { ok: false, detail: `路径不存在：${path}` }
    const acl = await user.getNtfsPermissions(path)
    const readable = acl.entries.filter((e) => e.type === 'Allow')
    const denied = acl.entries.filter((e) => e.type === 'Deny')
    const anyone =
      /^(everyone|authenticated users|builtin\/users|nt authority\/authenticated users)$/i
    const hasRead = readable.some(
      (e) => anyone.test(e.account.toLowerCase()) || /(read|modify|full)/i.test(e.rights),
    )
    return {
      ok: hasRead,
      detail: hasRead
        ? `${readable.length} 条允许访问${denied.length ? `，${denied.length} 条拒绝` : ''}`
        : '该路径 NTFS 未授予任何可读权限——客户端即使有共享权限也会被拒',
      fix: hasRead ? undefined : 'acl:read-everyone',
    }
  })

  await push(items, 'firewall-smb', '防火墙放行 SMB 入站', async () => {
    const rules = await listManagedRules()
    const hasSmb = rules.some((r) => r.name.includes('SMB (445/TCP)') && r.enabled)
    if (hasSmb) return { ok: true, detail: 'WinShare 组规则已存在' }
    // 系统内置"文件和打印机共享"规则组通常已启用，无法穷举判定，命中即视为放行
    const anyAllow = await runPowerShell<unknown>(
      '@(Get-NetFirewallRule -Group "@FirewallAPI.dll,-28502" -Enabled True -Action Allow -ErrorAction SilentlyContinue).Count',
      { retries: 0 },
    )
    const n = Number(Array.isArray(anyAllow) ? anyAllow[0] : anyAllow)
    return {
      ok: n > 0,
      detail: n > 0 ? '系统"文件和打印机共享"规则组已启用' : '未见启用的共享放行规则',
      fix: n > 0 ? undefined : 'firewall:smb',
    }
  })

  if (target) {
    await push(items, 'share-perms', '共享权限已授予访问者', async () => {
      if (!validateShareName(target)) throw Errors.invalidParam('共享名非法')
      const perms = await user.getSharePermissions(target)
      const allow = perms.filter((p) => !p.deny)
      const everyoneFull = allow.some(
        (p) => /^(everyone|authenticated users)$/i.test(p.account) && p.access === 'Full',
      )
      if (!allow.length) {
        return { ok: false, detail: '共享权限为空——任何人都连不上' }
      }
      return {
        ok: true,
        detail: everyoneFull
          ? `${allow.length} 条授权，其中 Everyone 为完全控制（建议收敛为只读或指定组）`
          : `${allow.length} 条授权`,
      }
    })
  }

  await push(items, 'smb1', '未启用 SMB1 旧协议（安全）', async () => {
    const cfg = await smb.getConfig()
    return {
      ok: !cfg.enableSMB1Protocol,
      detail: cfg.enableSMB1Protocol ? 'SMB1 已开启——建议关闭（永恒之蓝等风险面）' : 'SMB1 关闭',
    }
  })

  await push(items, 'guest', '未开放来宾/不安全访客登录（安全）', async () => {
    const cfg = await smb.getConfig()
    const risky = cfg.enableGuestUserAccess || cfg.enableInsecureGuestLogons
    return {
      ok: !risky,
      detail: risky
        ? `访客访问=${cfg.enableGuestUserAccess}，不安全访客登录=${cfg.enableInsecureGuestLogons}`
        : '均未开放',
    }
  })

  return items
}

/**
 * 执行自动修复。
 * 路径一律由服务端按 shareName 重新解析（绝不信渲染层传入的路径），
 * 与新建共享同一 validatePath 防线；修复动作写审计日志。
 */
export async function applyFix(fix: Fix, args?: { shareName?: string }): Promise<string> {
  switch (fix) {
    case 'service:lanman': {
      await smb.startService()
      audit('system', 'diagnoseFix', 'service:lanman', 'success')
      return 'LanmanServer 服务已启动'
    }
    case 'firewall:smb': {
      const created = await ensureRules([{ name: 'WinShare SMB (445/TCP)', ports: '445' }])
      audit('system', 'diagnoseFix', 'firewall:smb', 'success')
      return created.length ? '已添加防火墙规则：SMB 445/TCP 入站放行' : '规则已存在，无需重复添加'
    }
    case 'acl:read-everyone': {
      const shareName = args?.shareName ?? ''
      if (!shareName) throw Errors.invalidParam('ACL 修复需要共享名')
      const path = await resolveSharePath(shareName)
      if (!path) throw Errors.shareNotFound(shareName)
      // 复用共享路径校验（盘符开头、无控制字符/通配），与新建共享同一防线，杜绝注入
      if (!validatePath(path) || path.length > 260) {
        throw Errors.invalidParam(`共享路径非法，无法执行 ACL 修复：${path}`)
      }
      if (!existsSync(path)) throw Errors.invalidParam(`共享路径不存在：${path}`)
      // *S-1-1-0 为 Everyone 的 SID 形式，免依赖本地化组名（中文/英文系统一致）
      await runPowerShellVoid(
        `$s = (Get-Item -LiteralPath ${psQuote(path)}).FullName; icacls $s /grant ${psQuote('*S-1-1-0:(OI)(CI)R')} /C /Q | Out-Null`,
        { retries: 0 },
      )
      audit('system', 'diagnoseFix', `acl:read-everyone:${path}`, 'success')
      return `已对 ${path} 授予 Everyone 读取`
    }
    default:
      throw Errors.invalidParam('未知修复项')
  }
}
