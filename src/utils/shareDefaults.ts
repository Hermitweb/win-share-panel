import type { DiskUsage } from '../types'

// ===== 新建共享的智能默认值（新手上路第一批）=====
// 目标：打开向导即有可用值，用户只需确认而不是从零理解"路径/共享名/权限"三件事。
// 纯函数，便于单测；磁盘探测与用户目录由主进程 disk.suggestRoot 提供。

/** 从文件夹路径取共享名：末级目录名，非法字符转下划线，限 60 字符 */
export function shareNameFromPath(p: string): string {
  const parts = String(p ?? '')
    .replace(/[\\/]+$/, '')
    .split(/[\\/]/)
  const base = (parts[parts.length - 1] ?? '').trim()
  return base
    .replace(/[<>:"|?*\\/$]/g, '_')
    .replace(/^[.$]+/, '')
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
