/**
 * PowerShell 版本探测（v1.4.0 多版本兼容层）。
 *
 * 在应用启动时**非阻塞**地探测一次 $PSVersionTable，把引擎大版本
 * （5 / 7）与完整版本号写进 app.log（[psRuntime] 前缀），供 cmdlet 差异
 * 排查（IIS COM 仅 5.1 注册 / ConvertTo-Json 深度默认 / .NET API 面等）。
 *
 * 不阻塞首屏：main.ts whenReady 里 `void import(...)` 动态加载，
 * 探测失败（超时 / ENOENT）仅写 warn 日志，不影响池预热与窗口创建。
 *
 * 与 services/psRuntime.ts 的关系：
 *  - psRuntime.ts 做**同步 stat + 候选枚举**（廉价，启动即做）
 *  - 本文件做**异步版本探测**（需 spawn 一次 PowerShell，启动后空闲时做）
 */
import { nativePowerShellExe } from './nativePaths'
import { log } from './logger'

export interface PsVersionInfo {
  /** $PSVersionTable.PSVersion.Major（5 / 7） */
  major: number
  /** 完整版本号字符串（如 "5.1.19041.579"） */
  full: string
  /** 探测用的可执行文件路径 */
  exe: string
  /** 探测时间戳 */
  probedAt: number
}

// 探测缓存（同一进程内同一可执行文件只探一次）
const versionCache = new Map<string, PsVersionInfo>()
const VERSION_CACHE_MS = 5 * 60 * 1000

/**
 * 探测某可执行文件的 PowerShell 版本（异步，经注入的执行器跑一次 $PSVersionTable）。
 * 5 分钟 TTL 缓存；探测失败（超时 / ENOENT）不缓存负结果，下次调用重试。
 *
 * 不放在 nativePaths.ts 的原因：nativePaths 是纯同步解析层（主进程入口
 * 可在 spawn 任何 PowerShell 之前就调用），版本探测需 spawn 一次 PowerShell，
 * 归到本层（psRuntimeProbe）保持 nativePaths 的"零 spawn"契约。
 */
export async function probePsVersion(
  deps: Record<string, unknown> = {},
  run?: <T>(cmd: string) => Promise<T>,
): Promise<PsVersionInfo> {
  const exe = nativePowerShellExe(deps as never)
  const hit = versionCache.get(exe)
  if (hit && Date.now() - hit.probedAt < VERSION_CACHE_MS) return hit
  if (!run) {
    // 无执行器（如启动最早期）：只记「未探测」占位，caller 不阻塞
    return { major: 0, full: 'unknown', exe, probedAt: Date.now() }
  }
  try {
    const raw = (await run<{ major: number; full: string }>(
      '$v = $PSVersionTable.PSVersion; [PSCustomObject]@{ major = $v.Major; full = $v.ToString() } | ConvertTo-Json -Compress',
    )) as { major: number; full: string }
    const info: PsVersionInfo = {
      major: Number(raw?.major ?? 0),
      full: String(raw?.full ?? 'unknown'),
      exe,
      probedAt: Date.now(),
    }
    versionCache.set(exe, info)
    return info
  } catch {
    // 探测失败（超时 / 引擎启动失败）：不缓存负结果，下次重试
    return { major: 0, full: 'unknown', exe, probedAt: Date.now() }
  }
}

/** 测试钩子：清版本缓存 */
export function resetPsVersionCache(): void {
  versionCache.clear()
}

/**
 * 启动时调用：一次性探测 $PSVersionTable，结果写 app.log。
 * 探测失败（ENOENT / 超时）不抛错，仅写 warn 日志。
 */
export async function probePsVersionAndLog(): Promise<void> {
  const exe = nativePowerShellExe()
  // 裸名（跨平台 / PATH）不探测版本，避免在无 PS 的开发机上误 spawn
  if (!/^[A-Za-z]:[\\/]/.test(exe)) return

  // 最小探测命令：纯读 $PSVersionTable，不加载任何模块，5.1 / 7.x 都能跑
  const info = await probePsVersion(
    {},
    async <T>(cmd: string) => {
      // 经 lib/powershell 的 runPowerShell 会触发池预热，造成循环依赖；
      // 这里直接 spawn 一次单发进程（仅启动时跑一次，非热路径）
      const { execFile } = await import('node:child_process')
      const { promisify } = await import('node:util')
      const exec = promisify(execFile)
      const { stdout } = await exec(
        exe,
        [
          '-NoProfile',
          '-NonInteractive',
          '-ExecutionPolicy',
          'Bypass',
          '-EncodedCommand',
          Buffer.from(cmd, 'utf16le').toString('base64'),
        ],
        { timeout: 15_000, windowsHide: true },
      )
      // cmd 末尾已带 ConvertTo-Json -Compress（见 probePsVersion），直接解析
      return JSON.parse(String(stdout).trim()) as unknown as T
    },
  )

  if (info.major === 0 || info.full === 'unknown') {
    log.warn('psRuntime', `[psRuntime] 版本探测失败（${exe}），降级为"未探测"`)
    return
  }
  log.info('psRuntime', `[psRuntime] ${exe} → ${info.full}（major=${info.major}）`)
  // 引擎差异提示（仅 7.x 时写，5.1 是默认目标无需提示）
  if (info.major >= 7) {
    log.info(
      'psRuntime',
      '[psRuntime] 当前引擎为 PowerShell 7+：IIS WebAdministration 的 COM 组件仅在 5.1 注册，FTP/WebDAV 管理可能不可用；如需完整支持请安装 Windows PowerShell 5.1',
    )
  }
}
