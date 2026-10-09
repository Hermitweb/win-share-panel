import { existsSync } from 'fs'
import { homedir } from 'os'
import { join } from 'path'
import { runPowerShell } from '../lib/powershell'
import type { DiskUsage } from '../types'

// ===== 磁盘水位（文件服务器刚需）=====
interface RawDrive {
  Name: string
  Free: number
  Used: number
}

export async function getDiskUsages(): Promise<DiskUsage[]> {
  const raw = await runPowerShell<RawDrive | RawDrive[]>(
    'Get-PSDrive -PSProvider FileSystem | Select-Object Name,Free,Used',
    { retries: 0 },
  )
  const arr = Array.isArray(raw) ? raw : [raw]
  return arr
    .filter((d) => d && typeof d.Name === 'string' && /^[A-Za-z]$/.test(d.Name))
    .map((d) => {
      const total = (d.Free ?? 0) + (d.Used ?? 0)
      return {
        drive: `${d.Name}:`,
        freeGB: Math.round(((d.Free ?? 0) / 1e9) * 10) / 10,
        totalGB: Math.round((total / 1e9) * 10) / 10,
        freePct: total > 0 ? Math.round(((d.Free ?? 0) / total) * 100) : 100,
      }
    })
}

/** 从共享路径提取盘符（"E:\\share\\x" → "E:"），非法返回 null */
export function driveOfPath(p: string): string | null {
  const m = /^([A-Za-z]):/.exec(p ?? '')
  return m ? `${m[1].toUpperCase()}:` : null
}

/** UNC 路径（\\server\share）无本地盘符：返回 true，UI 不显示水位 */
export function isUnc(p: string): boolean {
  return /^\\\\[^\\]/.test(p ?? '')
}

function systemDrive(): string {
  return (process.env.SystemDrive || 'C:').replace(/\\/g, '').toUpperCase()
}

/**
 * 新建共享的路径建议（智能默认值）：
 * 优先非系统固定盘里剩余空间最大的那个（系统盘不放共享数据是共识），
 * 其次退回用户目录下的 Shared / 共享的（若已存在），最后退回系统盘根下 Share。
 * 仅返回建议字符串——目录是否真的存在/可写由创建时的系统校验负责。
 */
export function suggestShareRoot(usages: DiskUsage[], userHome = ''): string {
  const sys = systemDrive()
  const nonSystem = usages
    .filter((d) => /^[A-Z]:$/.test(d.drive) && d.drive !== sys)
    .sort((a, b) => b.freeGB - a.freeGB)
  if (nonSystem.length) return `${nonSystem[0].drive}\\Share`
  if (userHome) {
    for (const name of ['Shared', '共享的']) {
      const p = join(userHome, name)
      if (existsSync(p)) return p
    }
  }
  const fallback = usages.find((d) => d.drive === sys) ?? usages[0]
  return fallback ? `${fallback.drive}\\Share` : `${sys}\\Share`
}

/** 供 IPC 直接调用：查盘 + 算建议路径（用户目录取主进程 homedir） */
export async function suggestRoot(): Promise<string> {
  let usages: DiskUsage[] = []
  try {
    usages = await getDiskUsages()
  } catch {
    // 查盘失败也要给一个可用的默认值，向导不该被磁盘探测卡住
  }
  return suggestShareRoot(usages, homedir())
}
