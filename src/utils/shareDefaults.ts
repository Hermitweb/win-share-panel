import type { DiskUsage, Share } from '../types'

// ===== 新建共享的智能默认值（新手上路第一批）=====
// 目标：打开向导即有可用值，用户只需确认而不是从零理解"路径/共享名/权限"三件事。
// 纯函数，便于单测；磁盘探测与用户目录由主进程 disk.suggestRoot 提供。

/** 从文件夹路径取共享名：末级目录名，非法字符转下划线，限 60 字符 */
export function shareNameFromPath(p: string): string {
  const parts = String(p ?? '')
    .replace(/[\\/]+$/, '')
    .split(/[\\/]/)
  const base = (parts[parts.length - 1] ?? '').trim()
  // 盘符根（"D:" / "D:" 去尾分隔符后的形态）不是文件夹名，返回空让调用方保留用户输入
  if (/^[A-Za-z]:?$/.test(base) && base.length <= 2) return ''
  return base
    .replace(/^[.$]+/, '') // 先剥前导点/$（隐藏共享形态），否则替换成 _ 后就剥不掉了
    .replace(/[<>:"|?*\\/$]/g, '_')
    .slice(0, 60)
}

/** 默认授权：Users 组只读——连得上又不会误改，是共享场景的安全下限 */
export const DEFAULT_READ_ACCESS = ['Users']

/**
 * 磁盘水位等级：供列表黄标/详情提示。
 * 阈值语义取"比例或绝对值任一命中"，因为小盘看比例、大盘看绝对剩余。
 */
export function diskLevel(
  d: DiskUsage | undefined,
  thresholds: { lowPct?: number; lowGb?: number } = {},
): 'ok' | 'warn' | 'danger' | 'unknown' {
  if (!d) return 'unknown'
  const lowPct = thresholds.lowPct ?? 10
  const lowGb = thresholds.lowGb ?? 20
  if (d.freePct <= lowPct / 2 || d.freeGB <= lowGb / 4) return 'danger'
  if (d.freePct <= lowPct || d.freeGB <= lowGb) return 'warn'
  return 'ok'
}

/** UNC（\\server\share）没有本地盘符，不显示水位 */
export function isUncPath(p: string): boolean {
  return /^\\\\[^\\]/.test(String(p ?? ''))
}

/** 路径 → 盘符（"E:\\a\\b" → "E:"），非本地路径返回 null */
export function driveOf(p: string): string | null {
  const m = /^([A-Za-z]):/.exec(String(p ?? ''))
  return m ? `${m[1].toUpperCase()}:` : null
}

/** 按盘符建索引，渲染列表时 O(1) 取水位 */
export function indexDiskUsages(usages: DiskUsage[]): Record<string, DiskUsage> {
  const map: Record<string, DiskUsage> = {}
  for (const u of usages ?? []) map[u.drive.toUpperCase()] = u
  return map
}

/**
 * 删除前的连接影响提示（★ 低成本高价值：concurrentUsers 列表里已有，纯 UI 组合）。
 * 误删最怕的是"正在传文件的人被踢"，这句话必须在确认框里先看见，而不是事后从日志里知道。
 */
export function deleteImpactText(shares: Share[]): { text: string; danger: boolean } {
  const connected = shares.filter((s) => (s.concurrentUsers ?? 0) > 0)
  if (!connected.length) {
    return {
      text: shares.length === 1 ? '当前无人连接，可安全删除。' : '所选共享当前均无人连接。',
      danger: false,
    }
  }
  const users = connected.reduce((n, s) => n + (s.concurrentUsers ?? 0), 0)
  const names = connected
    .slice(0, 3)
    .map((s) => `${s.name}(${s.concurrentUsers})`)
    .join('、')
  const more = connected.length > 3 ? ' 等' : ''
  return {
    text: `当前 ${users} 个连接正在使用${more}共享：${names}${more}——删除会强制断开这些连接，正在写入的文件可能丢失。`,
    danger: true,
  }
}

/**
 * 撤销能力说明：只有 SMB 删除带可还原快照（journal.undoJournal 走 restoreShare），
 * 其他协议的站点/共享参数没有通用还原通道，诚实写明"仅留档"而不是给个坏按钮。
 */
export function undoHintText(shares: Share[]): string {
  const smb = shares.filter((s) => s.protocol === 'smb').length
  const other = shares.length - smb
  if (shares.length === 1) {
    return smb ? '删除后可在「操作回收站」一键撤销。' : '该协议的删除仅留档，不支持一键撤销。'
  }
  if (!other) return `${smb} 个 SMB 共享删除后均可在「操作回收站」撤销。`
  if (!smb) return `${other} 个非 SMB 共享仅留档，不支持一键撤销。`
  return `其中 ${smb} 个 SMB 共享可撤销；${other} 个非 SMB 共享仅留档，不支持一键撤销。`
}
