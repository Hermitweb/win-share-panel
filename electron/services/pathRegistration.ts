/**
 * 系统注册项一键管理（v1.4.0 新增）。
 *
 * 背景：v1.4.0 的 NSIS 安装器（resources/installer-hooks.nsh）在安装时写入
 *   ① ARP 程序列表（HKLM\...\Uninstall\WinSharePanel，electron-builder 自带 + 本钩子增强）
 *   ② 登录快速启动（HKCU\...\Run\WinSharePanel = <dir>\WinShare Panel.exe --silent）
 *   ③ Windows 服务预留标记（HKLM\Software\WinSharePanel\ServicePending=1）
 * ④ 系统 PATH 条目 **不**由安装器写（跨权限/卸载残留风险），改由本模块在
 *    主进程"首次启动自检 + 一键注册"入口写入。
 *
 * 卸载时由 un.showPathHint 提示用户手动清理；本模块也提供 unregisterPathEntry
 * 供 UI 直接撤销。
 *
 * 实现选择：
 *  - 读写 HKCU\Environment\Path（**用户级 PATH**，不是系统级 HKEY_LOCAL_MACHINE）
 *    —— 用户级 PATH 不需要 UAC 提权即可写，与"单用户工具"形态匹配；
 *    若未来需要系统级（全用户可调用），再开 HKLM 通道（需 UAC + 写后广播
 *    WM_SETTINGCHANGE，这里预留开关）。
 *  - 幂等：写入前读现存 PATH，仅当不含本应用目录精确匹配时追加；撤销时
 *    仅当含本应用目录精确匹配时删除。
 *  - 广播：用户级 PATH 变更需广播 WM_SETTINGCHANGE 给本会话所有进程，
 *    否则已有终端不会拿到新 PATH；使用 PowerShell（经 lib/powershell.ts）
 *    的 NativeMethods 广播（本模块依赖 PS 可用，PS 缺失时静默降级为
 *    "写成功但需重开终端才生效"并在结果里注明）。
 */
import { app } from 'electron'
import { dirname } from 'node:path'
import { runPowerShell, runPowerShellVoid } from '../lib/powershell'
import { log } from '../lib/logger'

// 注册项键名（与 installer-hooks.nsh 对齐）
const HKCU_RUN_KEY = 'Software\\Microsoft\\Windows\\CurrentVersion\\Run'

/**
 * 把任意路径转成正则**字面量**（可直接拼进 RegExp 源串）。
 *
 * 必须转义正则元字符，而不能只做 `dir.replace(/\\/g, '\\\\')`：
 * 后者只处理了反斜杠，路径里的 `(` `)` `.` `[` `]` 等仍会被当成正则语法 ——
 * 例如装到 `C:\Program Files (x86)\WinShare Panel` 时，`(x86)` 变成捕获组，
 * 真实段匹配不上，反而会匹配到形似但不同的 `C:\Program Files x86\...`，
 * 导致"撤销 PATH"**移除错段、留下真段**。
 *
 * 反斜杠一并由本函数处理（`\` → `\\`），调用方无需再自行加倍。
 */
function escapeRegExpLiteral(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

// 打包版：$INSTDIR = resourcesPath 的父目录；dev 模式返回 appPath（写操作被拦截）
function currentUserPathDir(): string {
  if (app.isPackaged) {
    return dirname(process.resourcesPath)
  }
  return app.getAppPath()
}

export interface PathRegistration {
  /** 是否已存在本应用目录条目 */
  registered: boolean
  /** 当前用户级 PATH 值（脱敏：只返回长度 + 是否含本应用目录，不回传完整 PATH 避免泄露其他工具路径） */
  pathLength: number
  /** 本次操作结果（供 UI 提示） */
  message: string
  /** 是否广播了 WM_SETTINGCHANGE（PS 缺失时为 false） */
  broadcast: boolean
}

/** 读取当前用户级 PATH（HKCU\Environment\Path）；缺失返回空串 */
export async function getUserPath(): Promise<string> {
  try {
    // PS 单引号串内无插值/转义语义，反斜杠按字面保留 —— 与 powershell.ts 的 psQuote 约定一致
    const raw = await runPowerShell<string>(
      "(Get-ItemProperty -Path 'HKCU:\\Environment' -Name Path -ErrorAction SilentlyContinue).Path",
      { retries: 0, timeout: 10_000 },
    )
    return typeof raw === 'string' ? raw : ''
  } catch {
    return ''
  }
}

/** 写入用户级 PATH（HKCU\Environment\Path）；空串表示清除 */
export async function setUserPath(value: string): Promise<void> {
  if (value === '') {
    await runPowerShellVoid(
      "Remove-ItemProperty -Path 'HKCU:\\Environment' -Name Path -ErrorAction SilentlyContinue",
      { retries: 0, timeout: 10_000 },
    )
    return
  }
  // 值用单引号包裹 + 内部 ' 转义为 ''（psQuote 同一规则）；PATH 值里的反斜杠按字面保留
  const psQuoted = `'${value.replace(/'/g, "''")}'`
  await runPowerShellVoid(
    `Set-ItemProperty -Path 'HKCU:\\Environment' -Name Path -Value ${psQuoted} -Type String`,
    { retries: 0, timeout: 10_000 },
  )
}

/** 广播 WM_SETTINGCHANGE（环境变量段）；PS 缺失时静默失败 */
export async function broadcastPathChange(): Promise<boolean> {
  try {
    await runPowerShellVoid(
      // SendMessageTimeout 广播 WM_SETTINGCHANGE（HWND_BROADCAST=0xFFFF）
      // 参数 'Environment' 指定环境变量段；其他进程收到后重新读 HKCU\Environment
      `
$sig = @'
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll", SetLastError=true)]
  public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr wp, string lp, uint fu, uint to, out IntPtr r);
}
'@
Add-Type -TypeDefinition $sig
$r = [IntPtr]::Zero
[void]([W]::SendMessageTimeout([IntPtr]([int]0xFFFF), 0x1A, [IntPtr]::Zero, 'Environment', 0x2, 1000, [ref]$r))
`,
      { retries: 0, timeout: 15_000 },
    )
    return true
  } catch (e) {
    log.warn(
      'pathReg',
      '[broadcastPathChange] 广播失败（PS 不可用或权限不足）:',
      (e as Error).message,
    )
    return false
  }
}

/** 一键注册：把本应用目录追加到用户级 PATH（幂等） */
export async function registerAppInUserPath(): Promise<PathRegistration> {
  const dir = currentUserPathDir()
  if (!app.isPackaged) {
    return {
      registered: false,
      pathLength: 0,
      message: '开发模式不支持一键注册 PATH；请使用打包版',
      broadcast: false,
    }
  }
  const before = await getUserPath()
  const escapedDir = escapeRegExpLiteral(dir)
  const segMatch = new RegExp(`(^|;)\\s*"?(?:${escapedDir})"?\\s*(?=;|$)`, 'i')
  if (segMatch.test(before)) {
    return {
      registered: true,
      pathLength: before.length,
      message: `PATH 已包含 ${dir}，无需重复注册`,
      broadcast: false,
    }
  }
  const appended = before ? `${before};${dir}` : dir
  await setUserPath(appended)
  const broadcast = await broadcastPathChange()
  return {
    registered: true,
    pathLength: appended.length,
    message: broadcast
      ? `已将 ${dir} 追加到用户 PATH，并广播给本会话进程`
      : `已将 ${dir} 追加到用户 PATH；广播失败，需重开终端/登录才生效`,
    broadcast,
  }
}

/** 一键撤销：从用户级 PATH 移除本应用目录（幂等） */
export async function unregisterAppFromUserPath(): Promise<PathRegistration> {
  const dir = currentUserPathDir()
  if (!app.isPackaged) {
    return {
      registered: false,
      pathLength: 0,
      message: '开发模式不支持撤销 PATH 注册',
      broadcast: false,
    }
  }
  const before = await getUserPath()
  const escapedDir = escapeRegExpLiteral(dir)
  const segMatch = new RegExp(`(^|;)\\s*"?(?:${escapedDir})"?\\s*(?=;|$)`, 'i')
  if (!segMatch.test(before)) {
    return {
      registered: false,
      pathLength: before.length,
      message: `PATH 未包含 ${dir}，无需撤销`,
      broadcast: false,
    }
  }
  // 拆分 + 过滤 + 重组；保留引号（若有）
  const segs = before
    .split(';')
    .map((s) => s.trim())
    .filter((s) => s.length)
  // 段本身可能带引号（PATH 里带空格的目录惯例写法），故两侧各允许一个可选引号。
  // 注：这里必须自己闭合分组——早期版本尾部写成 `(?:"|\\)`，`\)` 被当成转义字面量括号，
  // 分组永不闭合，new RegExp 直接抛 SyntaxError（撤销功能 100% 失败）。
  const kept = segs.filter((s) => !new RegExp(`^"?${escapedDir}"?$`, 'i').test(s))
  await setUserPath(kept.join(';'))
  const broadcast = await broadcastPathChange()
  return {
    registered: false,
    pathLength: kept.length,
    message: broadcast
      ? `已从用户 PATH 移除 ${dir}，并广播给本会话进程`
      : `已从用户 PATH 移除 ${dir}；广播失败，需重开终端/登录才生效`,
    broadcast,
  }
}

/** 查询当前用户级 PATH 是否已注册本应用目录 */
export async function isAppInUserPath(): Promise<boolean> {
  const dir = currentUserPathDir()
  const p = await getUserPath()
  if (!p) return false
  const escapedDir = escapeRegExpLiteral(dir)
  return new RegExp(`(^|;)\\s*"?(?:${escapedDir})"?\\s*(?=;|$)`, 'i').test(p)
}

/**
 * 登录快速启动（HKCU Run 键）—— 与 NSIS 安装器钩子 ② 等价；
 * 供用户从应用内补写/撤销（卸载器删了 Run 键后，用户仍可重新启用）。
 * 注意：开机自启走的是 LoginItemSettings（系统登录项，见 ipc/index.ts
 * system:setAutoStart），与 HKCU Run 键是两条不同链路：
 *  - HKCU Run\WinSharePanel = "<dir>\WinShare Panel.exe --silent"（本模块，静默自启）
 *  - 系统登录项（Electron LoginItemSettings，GUI 自启，托盘 + 窗口）
 * 两者并存时：登录时先跑 Run 键的静默进程，再弹登录项的 GUI；
 * 主进程单实例锁会让第二个进程激活第一个（见 main.ts 的 second-instance 处理器）。
 */
export async function setLoginShortcut(enabled: boolean): Promise<string> {
  const dir = currentUserPathDir()
  if (!app.isPackaged) {
    return '开发模式不支持补写 HKCU Run 键；请重新安装或手动写注册表'
  }
  // 键名/值名用单引号字面量（PS 单引号串内无插值，反斜杠按字面保留）
  const keyPath = `HKCU:${HKCU_RUN_KEY}`
  if (enabled) {
    const cmd = `"${dir}\\WinShare Panel.exe" --silent`
    await runPowerShellVoid(
      `Set-ItemProperty -Path '${keyPath}' -Name WinSharePanel -Value '${cmd.replace(/'/g, "''")}' -Type String`,
      { retries: 0, timeout: 10_000 },
    )
    return `已写入登录快速启动：${keyPath}\\WinSharePanel`
  }
  await runPowerShellVoid(
    `Remove-ItemProperty -Path '${keyPath}' -Name WinSharePanel -ErrorAction SilentlyContinue`,
    { retries: 0, timeout: 10_000 },
  )
  return `已删除登录快速启动：${keyPath}\\WinSharePanel`
}

/** 读取登录快速启动是否启用（HKCU Run 键） */
export async function getLoginShortcut(): Promise<boolean> {
  try {
    const raw = await runPowerShell<string>(
      `(Get-ItemProperty -Path 'HKCU:${HKCU_RUN_KEY}' -Name WinSharePanel -ErrorAction SilentlyContinue).WinSharePanel`,
      { retries: 0, timeout: 10_000 },
    )
    return typeof raw === 'string' && raw.length > 0
  } catch {
    return false
  }
}

/**
 * 读取 NSIS 安装器钩子 ③ 写下的"Windows 服务预留标记"。
 * v1.4.0 暂不创建服务本体；本函数仅返回标记是否存在，供 UI 提示
 * "此机器安装时预留了服务注册位，可在后续版本启用系统服务"。
 * 标记缺失（如 portable 安装或老版本升级）时返回 false。
 */
export async function readServicePendingMarker(): Promise<boolean> {
  try {
    const raw = await runPowerShell<string>(
      // 注：JS 字符串里 '\W' 会被**丢弃反斜杠**（只留 W），实际发出的键路径变成
      // 'HKLM:SoftwareWinSharePanel' —— 那是另一个不存在的键，-ErrorAction SilentlyContinue
      // 会静默吞掉，读到的永远是 null → 标记恒为 false。必须写成 \\ 才能保留分隔符。
      `(Get-ItemProperty -Path 'HKLM:Software\\WinSharePanel' -Name ServicePending -ErrorAction SilentlyContinue).ServicePending`,
      { retries: 0, timeout: 10_000 },
    )
    return String(raw) === '1'
  } catch {
    return false
  }
}
