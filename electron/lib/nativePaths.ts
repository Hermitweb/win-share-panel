import { existsSync } from 'node:fs'

/**
 * 原生（非 WOW64 重定向）系统路径解析 —— 32 位安装包的架构对齐点。
 *
 * ## 问题（本机实测，不是推测）
 *
 * 32 位（ia32）包在 64 位 Windows 上运行时，进程内的 `C:\Windows\System32` 会被 WOW64
 * 重定向到 `C:\Windows\SysWOW64`，于是裸名 `powershell.exe` 拉起的是 **32 位 PowerShell**。
 * 两者能力并不等价，同一段脚本在两种位宽下实测结果：
 *
 * | 探针 | 32 位 PowerShell | 64 位 PowerShell |
 * |---|---|---|
 * | `Get-LocalUser`（本地用户列表 / 账号体检 `/services/user.ts`、`/services/security.ts`） | **不存在**（`Microsoft.PowerShell.LocalAccounts` 模块缺失） | 正常 |
 * | IIS `Get-Website`（`WebAdministration`，`/services/ftp.ts`、`/services/webdav.ts`、`/services/system.ts`） | **COM 类未注册 0x80040154** | 正常 |
 * | `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion` 的 `ProgramFilesDir` | `C:\Program Files (x86)`（WOW6432Node 视图） | `C:\Program Files` |
 * | `Get-SmbShare` / `Get-SmbServerConfiguration` | 正常 | 正常 |
 *
 * 即：x86 包若不改，用户页与账号体检、IIS（FTP/WebDAV）配置会直接不可用，而不是"慢一点"。
 *
 * ## 做法
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
 * 注：`inetsrv\appcmd.exe` 无需同样处理 —— 本机实测 System32 与 SysWOW64 **都存在**该文件，
 * 且拉起原生 PowerShell 后 PS 内部的 `system32` 本身就是原生视图，故 ftp/webdav 的 appcmd
 * 调用点保持不变。
 */

export interface NativePathDeps {
  /** 覆盖平台判定（测试注入；默认 process.platform） */
  platform?: string
  /** 覆盖 Windows 目录（测试注入；默认 %SystemRoot% → %windir% → C:\Windows） */
  systemRoot?: string
  /** 覆盖存在性探测（测试注入；默认 fs.existsSync） */
  exists?: (path: string) => boolean
}

const POWERSHELL_RELATIVE = 'WindowsPowerShell\\v1.0\\powershell.exe'

function systemRootOf(deps: NativePathDeps): string {
  const raw = deps.systemRoot ?? process.env.SystemRoot ?? process.env.windir ?? 'C:\\Windows'
  // 去掉尾部反斜杠，避免拼出 "C:\Windows\\Sysnative\..."
  return raw.replace(/[\\/]+$/, '') || 'C:\\Windows'
}

/** 解析 System32 下的相对路径；32 位进程优先取 Sysnative，其余情况取 System32 */
export function nativeSystemPath(relative: string, deps: NativePathDeps = {}): string {
  const root = systemRootOf(deps)
  const platform = deps.platform ?? process.platform
  const exists = deps.exists ?? existsSync
  const viaSysnative = `${root}\\Sysnative\\${relative}`
  if (platform === 'win32' && exists(viaSysnative)) return viaSysnative
  return `${root}\\System32\\${relative}`
}

/**
 * 当前进程应使用的 Windows PowerShell：始终取与操作系统同位宽的原生版本。
 * 非 Windows 平台返回裸名（交给 PATH）——只为让纯逻辑用例与跨平台开发不被硬编码路径挡住。
 */
export function nativePowerShellExe(deps: NativePathDeps = {}): string {
  const platform = deps.platform ?? process.platform
  if (platform !== 'win32') return 'powershell.exe'
  return nativeSystemPath(POWERSHELL_RELATIVE, deps)
}
