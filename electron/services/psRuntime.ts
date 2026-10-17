/**
 * PowerShell 运行时探测 + 失败诊断（v1.4.0 多版本兼容版）。
 *
 * 背景：用户机器上 PowerShell 版本/形态千差万别（5.1 / 7.x / 双引擎 /
 * 精简系统全删 / 杀软拦截）。`nativePowerShellExe()` 在 lib/nativePaths.ts
 * 里做"多版本兼容解析"（5.1 优先，缺失时兜底 7.x），本模块做：
 *  - 启动时 stat + 候选枚举（把结果写 app.log，供事后追溯"机器差异"）
 *  - 失败后给出可执行修复建议（sfc /scannow / 杀软白名单 / 安装 PowerShell 7）
 *
 * 设计原则（与 nativePaths.ts 一致）：
 *  - 不修改 PowerShell 解析策略（nativePaths.ts 是唯一真相源）
 *  - 纯同步 stat，零 spawn（版本探测走 lib/psRuntimeProbe 的异步通道）
 */
import { existsSync } from 'node:fs'
import {
  nativePowerShellExe,
  listPowerShellCandidates,
  type NativePathDeps,
  type PsCandidate,
} from '../lib/nativePaths'
import type { PsRuntimeStatus, PsRuntimeDiagnosis } from '../types'

// 重新导出，供渲染层 / IPC 使用同一份类型（与 electron/types.ts 对齐）
export type { PsRuntimeStatus, PsRuntimeDiagnosis }

let cached: PsRuntimeStatus | null = null
let testOverrides: TestOverrides | null = null

/** 允许测试注入 custom exists / candidates 实现（生产不生效） */
export type TestOverrides = {
  exists?: (p: string) => boolean
  candidates?: () => PsCandidate[]
}
export function __setTestOverrides(o: TestOverrides | null): void {
  testOverrides = o
}

/** 纯逻辑兜底：deps 未注入 exists 时用 node:fs 的真实探测（生产路径） */
function realExists(p: string): boolean {
  try {
    return existsSync(p)
  } catch {
    return false
  }
}

/**
 * 同步探测 PowerShell 可执行文件；`force=true` 时重新 stat。
 *
 * 注意：候选的 exists 标记由 nativePaths.listPowerShellCandidates 统一计算
 * （它接受 deps.exists 注入），本函数不再单独对 resolvedPath 二次 stat，
 * 避免两处 stat 逻辑漂移。realPathFound = 候选里至少有一个真实绝对路径存在。
 */
export function probePowerShell(deps: NativePathDeps = {}, force = false): PsRuntimeStatus {
  if (cached && !force) return cached
  const resolvedPath = nativePowerShellExe(deps)
  const isAbsolutePath = /^[A-Za-z]:[\\/]/.test(resolvedPath) || resolvedPath.startsWith('/')

  // 候选摘要（多版本兼容层）：列出这台机器上实际存在的所有 PowerShell
  let candidates: PsRuntimeStatus['candidates']
  let engine: PsRuntimeStatus['engine']
  let realPathFound = false
  if (testOverrides?.candidates) {
    const all = testOverrides.candidates()
    candidates = all.map((c) => ({ path: c.path, engine: c.engine, exists: c.exists, note: c.note }))
    const hit = all.find((c) => c.path === resolvedPath)
    engine = hit?.engine ?? 'unknown'
    realPathFound = all.some((c) => c.exists && /^[A-Za-z]:[\\/]/.test(c.path))
  } else if (deps.platform === undefined || deps.platform === 'win32') {
    // 生产 / win32 测试路径：用 nativePaths.listPowerShellCandidates 统一 stat
    // （它接受 deps.exists 注入，测试可模拟"只有 5.1 / 只有 7.x / 双引擎 / 全缺"）
    const all = listPowerShellCandidates(deps)
    candidates = all.map((c) => ({ path: c.path, engine: c.engine, exists: c.exists, note: c.note }))
    const hit = all.find((c) => c.path === resolvedPath)
    engine = hit?.engine ?? 'unknown'
    realPathFound = all.some((c) => c.exists && /^[A-Za-z]:[\\/]/.test(c.path))
  } else {
    engine = 'unknown'
    // 非 win32 平台：裸名交给 PATH，乐观认为存在
    realPathFound = true
  }

  // 裸名（交给 PATH）：不 stat，记乐观 exists=true；
  // 绝对路径：exists 直接取候选里该路径的 stat 结果（与 nativePaths 同源，避免漂移）
  let exists: boolean
  if (!isAbsolutePath) {
    exists = true
  } else {
    // 绝对路径：取候选里该路径的 stat 结果；候选未生成（非 win32 平台分支）时
    // 回退到本地 existsSync（realExists，生产路径）
    const hit = candidates?.find((c) => c.path === resolvedPath)
    exists = hit ? hit.exists : realExists(resolvedPath)
  }

  cached = {
    resolvedPath,
    exists,
    realPathFound,
    statError: undefined,
    probedAt: Date.now(),
    engine,
    candidates,
  }
  return cached
}

/** 读取最近一次探测结果；从未探测时触发一次（主进程入口调用链） */
export function getPowershellStatus(): PsRuntimeStatus {
  return cached ?? probePowerShell()
}

/** 当前选中引擎的人类可读名（日志/UI 用） */
export function engineLabel(status: Pick<PsRuntimeStatus, 'engine' | 'resolvedPath'>): string {
  switch (status.engine) {
    case 'windowsPowerShell':
      return 'Windows PowerShell 5.1'
    case 'powershellCore':
      return 'PowerShell 7+（pwsh）'
    default:
      return status.resolvedPath
  }
}

/**
 * 根据一次 PowerShell 调用失败 + 当前探测结果，给出修复建议。
 * 与 powershell.ts 的错误归一化互补：formatPsError 负责把 stderr 变可读，
 * 这里负责把"可读错误"翻译成"下一步做什么"。
 *
 * 多版本兼容：若 5.1 缺失但 7.x 可用，说明"已自动切到 pwsh.exe（7.x），
 * 部分 IIS cmdlet 行为可能有差异"；若两者都缺，建议"安装 PowerShell 7
 * （winget install Microsoft.PowerShell）或 sfc /scannow 恢复 5.1"。
 */
export function diagnosePsRuntime(
  rawError: string,
  deps: NativePathDeps = {},
): PsRuntimeDiagnosis {
  const probe = probePowerShell(deps, true)
  const suggestions: string[] = []

  // 用"真实绝对路径候选是否存在"判断双引擎缺失（裸名不算——PATH 兜底不可靠）
  const realMissing = !probe.realPathFound

  if (realMissing) {
    suggestions.push(
      `PowerShell 可执行文件未找到：${probe.resolvedPath}（5.1 与 7.x 的真实路径都不存在，仅剩 PATH 裸名兜底）`,
      '检查 Windows 是否被精简/优化（如"系统还原"、小智优工具箱等）删除了 %SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe',
      '以管理员身份运行 `sfc /scannow` 修复系统文件，或「设置 → 系统 → 恢复」重置',
      '或安装 PowerShell 7：`winget install Microsoft.PowerShell`（应用会自动识别 pwsh.exe）',
    )
  } else if (!probe.exists) {
    suggestions.push(
      `选中的 ${probe.resolvedPath} 不存在，但候选里还有其他 PowerShell，请重启应用`,
    )
  } else {
    suggestions.push(`已找到 PowerShell：${probe.resolvedPath}（${engineLabel(probe)}）`)
    const msg = rawError.toLowerCase()
    if (msg.includes('0x80070003') || msg.includes('enoent')) {
      suggestions.push(
        '错误码 0x80070003（文件找不到）：进程能枚举到可执行文件但无法启动——多为杀软/EDR/AppLocker 拦截 spawn',
        `在杀软白名单中放行：${probe.resolvedPath}`,
      )
    }
    if (msg.includes('access is denied') || msg.includes('拒绝访问')) {
      suggestions.push(
        '拒绝访问：以管理员身份运行 WinShare Panel（打包版已声明 requireAdministrator，非管理员启动会触发 UAC 提示）',
      )
    }
    if (msg.includes('clixml') || msg.includes('powershell 命令执行失败')) {
      suggestions.push(
        'PowerShell 已启动但脚本执行失败：多为系统被精简导致 .NET Framework 4.x 损坏（5.1 引擎）或 .NET 8 运行时缺失（7.x 引擎），运行 `sfc /scannow` 或重装对应引擎',
      )
    }
    if (probe.engine === 'powershellCore') {
      suggestions.push(
        '当前引擎为 PowerShell 7+（pwsh.exe）：部分 IIS WebAdministration 的 COM 组件只在 5.1 注册，FTP/WebDAV 管理可能不可用——可安装 Windows PowerShell 5.1 后重启应用',
      )
    }
    const has51 = probe.candidates?.some(
      (c) => c.engine === 'windowsPowerShell' && c.exists && /^[A-Za-z]:[\\/]/.test(c.path),
    )
    if (!has51 && probe.engine === 'powershellCore') {
      suggestions.push(
        '本机仅有 PowerShell 7（无 5.1）：应用已自动切到 pwsh.exe，核心 SMB 功能可用；IIS（FTP/WebDAV）与 32 位相关 cmdlet 行为可能有差异，详见 docs/COMPATIBILITY.md',
      )
    }
  }

  return {
    // ok 反映"是否找到真实可用的 PowerShell"：双引擎真实路径全缺时 ok=false
    // （即使裸名兜底让 probe.exists 乐观为 true，也不能算"找到了 PowerShell"）
    ok: probe.realPathFound,
    reason: probe.realPathFound
      ? `PowerShell 可执行文件存在（${engineLabel(probe)}），启动/执行环节失败（详见应用日志 app.log）`
      : 'PowerShell 可执行文件未找到（5.1 与 7.x 的真实路径都缺失），所有依赖 PowerShell 的功能不可用',
    suggestions,
    rawError,
  }
}

/** 测试钩子：清除缓存，让下一次探测重新 stat */
export function resetPsProbeCache(): void {
  cached = null
}
