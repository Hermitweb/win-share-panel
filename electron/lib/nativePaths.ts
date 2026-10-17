import { existsSync } from 'node:fs'

/**
 * 原生（非 WOW64 重定向）系统路径解析 + PowerShell 多版本兼容 —— 32 位安装包的架构对齐点。
 *
 * ## 背景（v1.3.0 既有，保留）
 *
 * 32 位（ia32）包在 64 位 Windows 上运行时，进程内的 `C:\Windows\System32` 会被 WOW64
 * 重定向到 `C:\Windows\SysWOW64`，于是裸名 `powershell.exe` 拉起的是 **32 位 PowerShell**。
 * 两者能力并不等价，同一段脚本在两种位宽下实测结果：
 *
 * | 探针 | 32 位 PowerShell | 64 位 PowerShell |
 * |---|---|---|
 * | `Get-LocalUser`（本地用户列表 / 账号体检） | **不存在**（`Microsoft.PowerShell.LocalAccounts` 模块缺失） | 正常 |
 * | IIS `Get-Website`（`WebAdministration`） | **COM 类未注册 0x80040154** | 正常 |
 * | `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion` 的 `ProgramFilesDir` | `C:\Program Files (x86)`（WOW6432Node 视图） | `C:\Program Files` |
 * | `Get-SmbShare` / `Get-SmbServerConfiguration` | 正常 | 正常 |
 *
 * 即：x86 包若不改，用户页与账号体检、IIS（FTP/WebDAV）配置会直接不可用，而不是"慢一点"。
 *
 * `Sysnative` 是 WOW64 提供的虚拟别名：**只有 32 位进程能看到它**，它指向真正的原生
 * System32。本机实测：64 位进程 `Test-Path C:\Windows\Sysnative\...` = False，
 * 32 位进程（`SysWOW64\cmd.exe`）= True。因此用「存在即优先」探测：
 *
 * 1. 32 位进程在 64 位系统上 → 命中 Sysnative → 原生 64 位 PowerShell，行为与 x64 包一致；
 * 2. 64 位进程 → 探测不到 Sysnative → 落回 System32（本来就是原生）；
 * 3. 32 位系统（无 Sysnative）→ 落回 System32 → 32 位 PowerShell，即该系统上唯一的选择。
 *
 * 这样同一份代码在三种架构下都得到「与操作系统同位宽」的 PowerShell，无需按 process.arch
 * 分支硬编码，也不依赖 `PROCESSOR_ARCHITEW6432` 一类环境变量推断。
 *
 * ## v1.4.0 新增：多版本兼容（Windows PowerShell 5.1 + PowerShell 7 pwsh）
 *
 * 用户机器上的 PowerShell 形态千差万别：
 * - 默认 Win10/11：**只有** `powershell.exe`（5.1）
 * - 装了 PowerShell 7：`powershell.exe`（5.1）与 `pwsh.exe`（7.x）**并存**
 * - 精简系统 / 杀软删 5.1：可能**只剩** `pwsh.exe`（7.x）
 * - 杀软/策略拦截：两者都在但 spawn 被拦（ENOENT/0x80070003）
 *
 * 策略：**5.1 优先，缺失才兜底 7.x**。理由：
 * 1. 本项目全部 cmdlet 按 5.1 调试验证过（IIS WebAdministration 的 COM 注册仅 5.1
 *    可见、`Get-LocalUser` 行为、SMB cmdlet 面），7.x 下部分行为有差异
 *    （`ConvertTo-Json` 默认 Depth、.NET 8 vs 4.x 的 API 面、Appx 模块自动加载），
 *    5.1 可用时**不冒险切引擎**；
 * 2. 只有 5.1 缺失（精简系统删了 5.1）才落 7.x 兜底——总比全瘫强；
 * 3. 两个引擎的**可执行文件参数面**（`-NoProfile -NonInteractive -ExecutionPolicy
 *    Bypass -EncodedCommand`）一致，`powershell.ts`/`powershellPool.ts` 的调用方式
 *    无需按引擎分叉。
 *
 * 候选位置（按优先级）：
 * 1. `<SystemRoot>\{Sysnative|System32}\WindowsPowerShell\v1.0\powershell.exe`（5.1，位宽对齐）
 * 2. `<SystemRoot>\{Sysnative|System32}\WindowsPowerShell\v1.0\pwsh.exe`（7.x，
 *    Windows 官方默认把 pwsh 注册进 System32 的 WindowsPowerShell 目录）
 * 3. `<ProgramFiles>\PowerShell\7\pwsh.exe`（winget / 安装包常见位置；32 位进程下
 *    ProgramFiles 是 WOW6432Node 视图，7 位宽的 pwsh 在原生 ProgramFiles，
 *    故 32 位进程用 `%ProgramW6432%` 取原生视图）
 * 4. 裸名 `powershell.exe` / `pwsh.exe`（跨平台开发机交给 PATH；Windows 上仅在
 *    上述真实路径都缺失时作为最后兜底，spawn 失败由调用方的诊断层处理）
 *
 * 注：`inetsrv\appcmd.exe` 无需同样处理 —— 本机实测 System32 与 SysWOW64 **都存在**该文件，
 * 且拉起原生 PowerShell 后 PS 内部的 `system32` 本身就是原生视图，故 ftp/webdav 的 appcmd
 * 调用点保持不变。
 */

export interface NativePathDeps {
  /** 覆盖平台判定（测试注入；默认 process.platform） */
  platform?: string
  /** 覆盖进程位宽（测试注入；默认 process.arch，'ia32' 表示 32 位进程） */
  arch?: string
  /** 覆盖 Windows 目录（测试注入；默认 %SystemRoot% → %windir% → C:\Windows） */
  systemRoot?: string
  /** 覆盖 32 位进程下的原生 Program Files（测试注入；默认 %ProgramW6432% → %ProgramFiles%） */
  programFilesNative?: string
  /** 覆盖存在性探测（测试注入；默认 fs.existsSync） */
  exists?: (path: string) => boolean
}

export interface PsCandidate {
  /** 可执行文件绝对路径（裸名候选除外，裸名候选 path 即裸名） */
  path: string
  /** 引擎标识：'windowsPowerShell'（5.1）或 'powershellCore'（7+） */
  engine: 'windowsPowerShell' | 'powershellCore'
  /** 该路径是否存在 */
  exists: boolean
  /** 人类可读的解析说明（日志/诊断用） */
  note: string
}

const POWERSHELL_RELATIVE = 'WindowsPowerShell\\v1.0\\powershell.exe'
const PWSH_RELATIVE = 'WindowsPowerShell\\v1.0\\pwsh.exe'
const PWSH_PROGRAMFILES_RELATIVE = 'PowerShell\\7\\pwsh.exe'

function systemRootOf(deps: NativePathDeps): string {
  const raw = deps.systemRoot ?? process.env.SystemRoot ?? process.env.windir ?? 'C:\\Windows'
  // 去掉尾部反斜杠，避免拼出 "C:\Windows\\Sysnative\..."
  return raw.replace(/[\\/]+$/, '') || 'C:\\Windows'
}

/**
 * 原生（非 WOW64 重定向）Program Files：
 * 32 位进程用 %ProgramW6432%（WOW64 保留的原生 64 位 ProgramFiles 环境变量），
 * 64 位进程直接用 %ProgramFiles%；两者都缺失时落回 C:\Program Files。
 * 测试可通过 deps.programFilesNative 注入。
 */
function programFilesNativeOf(deps: NativePathDeps): string {
  const is32Bit = (deps.arch ?? process.arch) === 'ia32'
  // 32 位进程下优先 %ProgramW6432%（原生 PF）；64 位进程 %ProgramFiles% 即原生
  const raw = deps.programFilesNative
    ?? (is32Bit ? (process.env.ProgramW6432 ?? process.env.ProgramFiles ?? 'C:\\Program Files')
                : (process.env.ProgramFiles ?? 'C:\\Program Files'))
  return raw.replace(/[\\/]+$/, '') || 'C:\\Program Files'
}

/** 位宽对齐前缀：32 位进程在 64 位系统命中 Sysnative，其余落回 System32 */
function nativePrefixOf(deps: NativePathDeps): { prefix: string; viaSysnative: boolean } {
  const root = systemRootOf(deps)
  const platform = deps.platform ?? process.platform
  const exists = deps.exists ?? existsSync
  // 位宽判定用 arch（process.arch === 'ia32'），不靠"Sysnative 里能不能 stat 到 5.1"：
  // 精简系统删了 5.1 时 Sysnative 目录仍在（32 位进程可见），靠 5.1 存在性判定会误落 System32
  const is32Bit = (deps.arch ?? process.arch) === 'ia32'
  const sysnativePrefix = `${root}\\Sysnative`
  const canUseSysnative =
    platform === 'win32' &&
    is32Bit &&
    (exists(`${sysnativePrefix}\\${POWERSHELL_RELATIVE}`) ||
      exists(`${sysnativePrefix}\\${PWSH_RELATIVE}`))
  const prefix = canUseSysnative ? sysnativePrefix : `${root}\\System32`
  return { prefix, viaSysnative: canUseSysnative }
}

/**
 * 按优先级枚举 PowerShell 候选（全部 stat；32 位位宽对齐 + 多版本兼容）。
 * 每个候选带 exists 标记，调用方按需取第一个 exists 的真实路径候选。
 *
 * 位宽对齐：32 位（ia32）进程在 64 位系统上，System32 被 WOW64 重定向到
 * SysWOW64，裸名 powershell.exe 拉起 32 位 PowerShell（缺 Get-LocalUser、
 * IIS COM 未注册）。Sysnative 是只有 32 位进程可见的虚拟别名，指向真正的
 * 原生 System32 —— 因此 32 位进程下所有候选（含 7.x 的 pwsh）都改走
 * Sysnative 前缀，保证与操作系统同位宽。位宽判定用 process.arch === 'ia32'
 * （进程事实），不靠"Sysnative 里能不能 stat 到 5.1"（精简系统删了 5.1 时
 * 会误判，但 Sysnative 目录与 7.x 的 pwsh 仍在）。
 */
export function listPowerShellCandidates(deps: NativePathDeps = {}): PsCandidate[] {
  const platform = deps.platform ?? process.platform
  if (platform !== 'win32') {
    // 非 Windows（跨平台开发机）：交给 PATH，不探测真实路径
    return [
      {
        path: 'powershell.exe',
        engine: 'windowsPowerShell',
        exists: true,
        note: '非 Windows 平台，裸名交给 PATH（开发机）',
      },
      {
        path: 'pwsh.exe',
        engine: 'powershellCore',
        exists: true,
        note: '非 Windows 平台，裸名交给 PATH（开发机）',
      },
    ]
  }
  const exists = deps.exists ?? existsSync
  const { prefix, viaSysnative } = nativePrefixOf(deps)
  const widthNote = viaSysnative ? '经 Sysnative（原生 64 位）' : 'System32（原生）'
  const pf = programFilesNativeOf(deps)

  const candidates: PsCandidate[] = []

  // 1. 5.1（位宽对齐）
  const ws51 = `${prefix}\\${POWERSHELL_RELATIVE}`
  candidates.push({
    path: ws51,
    engine: 'windowsPowerShell',
    exists: exists(ws51),
    note: `Windows PowerShell 5.1，${widthNote}`,
  })

  // 2. 7.x（System32 视图的 WindowsPowerShell 目录，与 5.1 同目录同位宽）
  const pwshSys = `${prefix}\\${PWSH_RELATIVE}`
  candidates.push({
    path: pwshSys,
    engine: 'powershellCore',
    exists: exists(pwshSys),
    note: `PowerShell 7+（系统注册位置），${widthNote}`,
  })

  // 3. 7.x（winget / 安装包常见位置，原生 Program Files）
  const pwshPf = `${pf}\\${PWSH_PROGRAMFILES_RELATIVE}`
  candidates.push({
    path: pwshPf,
    engine: 'powershellCore',
    exists: exists(pwshPf),
    note: 'PowerShell 7+（winget 安装位置，%ProgramFiles%\\PowerShell\\7）',
  })

  // 4. 裸名兜底（仅当 1-3 全缺失时才有意义；spawn 失败由调用方诊断）
  candidates.push({
    path: 'powershell.exe',
    engine: 'windowsPowerShell',
    exists: true,
    note: '裸名兜底（交给 PATH）',
  })
  candidates.push({
    path: 'pwsh.exe',
    engine: 'powershellCore',
    exists: true,
    note: '裸名兜底（交给 PATH）',
  })

  return candidates
}

/**
 * 解析当前进程应使用的 PowerShell 可执行文件（**5.1 优先，缺失兜底 7.x**）。
 * 取第一个「存在」的真实路径候选；全部真实路径缺失时回退裸名 5.1
 * （保持 v1.3.0 行为：ENOENT 由调用方的错误归一化/诊断层处理）。
 *
 * 非 Windows 平台返回裸名（同 v1.3.0），只为让纯逻辑用例与跨平台开发不被硬编码路径挡住。
 */
export function nativePowerShellExe(deps: NativePathDeps = {}): string {
  const platform = deps.platform ?? process.platform
  if (platform !== 'win32') return 'powershell.exe'
  const candidates = listPowerShellCandidates(deps)
  // 跳过裸名候选（path 无盘符前缀 = 裸名），取第一个存在的真实路径
  for (const c of candidates) {
    if (c.exists && /^[A-Za-z]:[\\/]/.test(c.path)) return c.path
  }
  // 全部真实路径缺失：回退裸名 5.1（v1.3.0 契约不变，spawn ENOENT 由诊断层兜底）
  return 'powershell.exe'
}

/** 当前进程解析出的全部 PowerShell 候选（供日志/诊断展示"这台机器有哪些 PowerShell"） */
export function listNativePowerShellCandidates(deps: NativePathDeps = {}): PsCandidate[] {
  return listPowerShellCandidates(deps)
}

/**
 * 解析 System32 下的相对路径；32 位进程优先取 Sysnative，其余情况取 System32。
 * （v1.3.0 既有 API，保留供 appcmd.exe 等调用方使用。）
 * 位宽事实靠 stat Sysnative 判定（Sysnative 只有 32 位进程可见，64 位进程
 * stat 恒 false），不靠 process.arch —— 与 nativePrefixOf 在 PowerShell 候选
 * 上的 arch 判定不同：appcmd.exe 在 System32 与 SysWOW64 都存在（本机实测），
 * Sysnative 命中与否不影响其可用性，stat 事实更直接。
 */
export function nativeSystemPath(relative: string, deps: NativePathDeps = {}): string {
  const root = systemRootOf(deps)
  const platform = deps.platform ?? process.platform
  const exists = deps.exists ?? existsSync
  const viaSysnative = `${root}\\Sysnative\\${relative}`
  if (platform === 'win32' && exists(viaSysnative)) return viaSysnative
  return `${root}\\System32\\${relative}`
}
