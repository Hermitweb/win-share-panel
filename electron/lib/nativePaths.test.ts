import { describe, it, expect } from 'vitest'
import {
  nativeSystemPath,
  nativePowerShellExe,
  listPowerShellCandidates,
  type NativePathDeps,
} from './nativePaths'

// 位宽 → 路径的三条分支必须各自成立，且不依赖运行测试的机器：
// 用注入的 exists + arch 模拟「32 位进程看到 Sysnative / 64 位进程看不到」这一 WOW64 事实
// （本机实测：64 位 Test-Path Sysnative=False；32 位 SysWOW64\cmd =True）。
//
// 注意：本测试在 64 位 Node 里跑（process.arch = 'x64'），所以必须通过
// deps.arch='ia32' 显式模拟 32 位进程，否则 nativePrefixOf 的 is32Bit 判定
// 会取真实的 process.arch 而非测试想模拟的位宽。
const ROOT = 'C:\\Windows'
const SYS = `${ROOT}\\System32`
const NATIVE = `${ROOT}\\Sysnative`
const PS_REL = 'WindowsPowerShell\\v1.0\\powershell.exe'
const PWSH_REL = 'WindowsPowerShell\\v1.0\\pwsh.exe'
const PF = 'C:\\Program Files'
const PWSH_PF = `${PF}\\PowerShell\\7\\pwsh.exe`

const PS51_SYS = `${SYS}\\${PS_REL}`
const PWSH_SYS = `${SYS}\\${PWSH_REL}`
const PS51_NATIVE = `${NATIVE}\\${PS_REL}`
const PWSH_NATIVE = `${NATIVE}\\${PWSH_REL}`

/** 模拟 32 位进程：Sysnative 视图里 5.1 存在 */
const onlySysnative51 = (p: string): boolean => p === PS51_NATIVE
/** 模拟 64 位进程 / 纯 32 位系统：Sysnative 不存在（5.1 在 System32） */
const sys64_51 = (p: string): boolean => p === PS51_SYS
/** 精简系统：5.1 被删，只剩 System32 的 pwsh */
const sysOnlyPwsh = (p: string): boolean => p === PWSH_SYS
/** 精简系统：5.1 被删，只剩 winget 装的 pwsh（%ProgramFiles%\PowerShell\7） */
const pfOnlyPwsh = (p: string): boolean => p === PWSH_PF
/** 全缺（杀软删干净 / 未装 PS7） */
const none = (): boolean => false

/** 模拟 32 位进程 + Sysnative 里 5.1 被删、只剩 pwsh（精简系统位宽对齐场景） */
const onlySysnativePwsh = (p: string): boolean => p === PWSH_NATIVE

describe('nativeSystemPath：32 位进程取 Sysnative，其余取 System32（v1.3.0 既有行为）', () => {
  // 注：nativeSystemPath 的位宽事实靠 stat Sysnative 判定（Sysnative 只有 32 位
  // 进程可见），与 nativePowerShellExe 的 arch 判定不同（见源码注释）。
  // 因此这里的 arch 注入不影响 nativeSystemPath 的结果，exists 才是决定性的。
  it('32 位进程在 64 位系统上（Sysnative 可见）：命中 Sysnative，取到原生 System32', () => {
    // nativeSystemPath 靠 stat Sysnative 判定位宽（Sysnative 只有 32 位进程可见），
    // exists=onlySysnative51 模拟"32 位进程 stat 到 Sysnative"
    expect(
      nativeSystemPath(PS_REL, { platform: 'win32', systemRoot: ROOT, arch: 'ia32', exists: onlySysnative51 }),
    ).toBe(`${NATIVE}\\${PS_REL}`)
  })

  it('64 位进程（Sysnative 不可见）：落回 System32，即本来就是原生路径', () => {
    // exists=sys64_51 只 stat 到 System32 的 5.1，Sysnative 的 stat 返回 false
    expect(
      nativeSystemPath(PS_REL, { platform: 'win32', systemRoot: ROOT, arch: 'x64', exists: sys64_51 }),
    ).toBe(`${SYS}\\${PS_REL}`)
  })

  it('纯 32 位系统（无 Sysnative）：落回 System32，即该系统上唯一可用的 32 位版本', () => {
    expect(
      nativeSystemPath('inetsrv\\appcmd.exe', {
        platform: 'win32',
        systemRoot: ROOT,
        arch: 'ia32',
        exists: none,
      }),
    ).toBe(`${SYS}\\inetsrv\\appcmd.exe`)
  })

  it('非 win32 平台不探测 Sysnative（避免造出不存在的路径）', () => {
    expect(
      nativeSystemPath(PS_REL, { platform: 'linux', systemRoot: '/x', arch: 'ia32', exists: onlySysnative51 }),
    ).toBe('/x\\System32\\WindowsPowerShell\\v1.0\\powershell.exe')
  })

  it('systemRoot 尾部反斜杠被规整，不会拼出双反斜杠', () => {
    expect(
      nativeSystemPath(PS_REL, {
        platform: 'win32',
        systemRoot: 'C:\\Windows\\',
        arch: 'x64',
        exists: none,
      }),
    ).toBe(`${SYS}\\${PS_REL}`)
  })
})

describe('nativePowerShellExe：多版本兼容（v1.4.0，5.1 优先缺失兜底 7.x）', () => {
  // 64 位进程（arch='x64'）：不命中 Sysnative，全部候选走 System32
  it('64 位进程 + 默认系统（只有 5.1）：取 System32 的 powershell.exe（5.1）', () => {
    const deps: NativePathDeps = { platform: 'win32', arch: 'x64', systemRoot: ROOT, programFilesNative: PF, exists: sys64_51 }
    expect(nativePowerShellExe(deps)).toBe(PS51_SYS)
  })

  it('32 位进程在 64 位系统（Sysnative 命中 5.1）：取 Sysnative 的 5.1，行为与 x64 包一致', () => {
    const deps: NativePathDeps = { platform: 'win32', arch: 'ia32', systemRoot: ROOT, programFilesNative: PF, exists: onlySysnative51 }
    expect(nativePowerShellExe(deps)).toBe(PS51_NATIVE)
  })

  it('32 位进程 + Sysnative 里 5.1 被删、只剩 Sysnative 的 pwsh：仍走 Sysnative 位宽对齐（兜底 7.x）', () => {
    const deps: NativePathDeps = { platform: 'win32', arch: 'ia32', systemRoot: ROOT, programFilesNative: PF, exists: onlySysnativePwsh }
    expect(nativePowerShellExe(deps)).toBe(PWSH_NATIVE)
  })

  it('5.1 与 7.x 并存（64 位进程）：优先 5.1（不冒险切引擎——本项目 cmdlet 按 5.1 调试验证）', () => {
    const both = (p: string) => p === PS51_SYS || p === PWSH_SYS
    const deps: NativePathDeps = { platform: 'win32', arch: 'x64', systemRoot: ROOT, programFilesNative: PF, exists: both }
    expect(nativePowerShellExe(deps)).toBe(PS51_SYS)
  })

  it('精简系统删了 5.1，只剩 System32 的 pwsh：兜底 7.x', () => {
    const deps: NativePathDeps = { platform: 'win32', arch: 'x64', systemRoot: ROOT, programFilesNative: PF, exists: sysOnlyPwsh }
    expect(nativePowerShellExe(deps)).toBe(PWSH_SYS)
  })

  it('精简系统删了 5.1，只剩 winget 装的 pwsh（%ProgramFiles%\\PowerShell\\7）：兜底 7.x', () => {
    const deps: NativePathDeps = { platform: 'win32', arch: 'x64', systemRoot: ROOT, programFilesNative: PF, exists: pfOnlyPwsh }
    expect(nativePowerShellExe(deps)).toBe(PWSH_PF)
  })

  it('两个引擎都缺（杀软删干净）：回退裸名 powershell.exe，ENOENT 由诊断层兜底', () => {
    const deps: NativePathDeps = { platform: 'win32', arch: 'x64', systemRoot: ROOT, programFilesNative: PF, exists: none }
    expect(nativePowerShellExe(deps)).toBe('powershell.exe')
  })

  it('非 Windows：返回裸名，交给 PATH（跨平台开发/纯逻辑用例不被硬编码路径挡住）', () => {
    expect(nativePowerShellExe({ platform: 'darwin', arch: 'x64', exists: sys64_51 })).toBe('powershell.exe')
    expect(nativePowerShellExe({ platform: 'linux', arch: 'x64', exists: sys64_51 })).toBe('powershell.exe')
  })
})

describe('listPowerShellCandidates：候选枚举（日志/诊断展示"这台机器有哪些 PowerShell"）', () => {
  it('默认系统（只有 5.1）：5.1 候选存在，两个 7.x 候选不存在，裸名兜底恒在', () => {
    const all = listPowerShellCandidates({
      platform: 'win32',
      arch: 'x64',
      systemRoot: ROOT,
      programFilesNative: PF,
      exists: sys64_51,
    })
    expect(all).toHaveLength(5)
    expect(all[0]).toMatchObject({ path: PS51_SYS, engine: 'windowsPowerShell', exists: true })
    expect(all[1]).toMatchObject({ path: PWSH_SYS, engine: 'powershellCore', exists: false })
    expect(all[2]).toMatchObject({ path: PWSH_PF, engine: 'powershellCore', exists: false })
    expect(all[3]).toMatchObject({ path: 'powershell.exe', engine: 'windowsPowerShell', exists: true })
    expect(all[4]).toMatchObject({ path: 'pwsh.exe', engine: 'powershellCore', exists: true })
  })

  it('双引擎并存（64 位进程）：5.1 与 7.x（System32）都标记 exists=true，优先级 5.1 在前', () => {
    const both = (p: string) => p === PS51_SYS || p === PWSH_SYS
    const all = listPowerShellCandidates({
      platform: 'win32',
      arch: 'x64',
      systemRoot: ROOT,
      programFilesNative: PF,
      exists: both,
    })
    expect(all[0].exists).toBe(true)
    expect(all[0].engine).toBe('windowsPowerShell')
    expect(all[0].path).toBe(PS51_SYS)
    expect(all[1].exists).toBe(true)
    expect(all[1].engine).toBe('powershellCore')
    expect(all[1].path).toBe(PWSH_SYS)
  })

  it('非 Windows：仅两个裸名候选（不 stat），exists 乐观 true', () => {
    const all = listPowerShellCandidates({ platform: 'linux', arch: 'x64' })
    expect(all).toHaveLength(2)
    expect(all[0].path).toBe('powershell.exe')
    expect(all[1].path).toBe('pwsh.exe')
    expect(all[0].exists).toBe(true)
  })

  it('32 位进程 + Sysnative 里 5.1 被删、只剩 Sysnative 的 pwsh：所有绝对路径候选都在 Sysnative 视图（位宽对齐）', () => {
    const bothNative = (p: string) => p === PS51_NATIVE || p === PWSH_NATIVE
    const all = listPowerShellCandidates({
      platform: 'win32',
      arch: 'ia32',
      systemRoot: ROOT,
      programFilesNative: PF,
      exists: bothNative,
    })
    const absolute = all.filter((c) => /^[A-Za-z]:[\\/]/.test(c.path))
    for (const c of absolute) {
      expect(c.path.startsWith(NATIVE)).toBe(true)
    }
    const bare = all.filter((c) => !/^[A-Za-z]:[\\/]/.test(c.path))
    expect(bare.map((c) => c.path)).toEqual(['powershell.exe', 'pwsh.exe'])
  })

  it('32 位进程 + winget 装的 64 位 pwsh（在原生 PF）：32 位进程用 ProgramW6432 取原生 PF，候选路径正确', () => {
    // 模拟：Sysnative 里没有 5.1 也没有 pwsh（精简系统），只有原生 PF 的 pwsh
    const onlyPf = (p: string) => p === PWSH_PF
    const all = listPowerShellCandidates({
      platform: 'win32',
      arch: 'ia32',
      systemRoot: ROOT,
      programFilesNative: PF, // 测试显式注入原生 PF（生产读 %ProgramW6432%）
      exists: onlyPf,
    })
    // 5.1 / System32-pwsh 候选都标记不存在，PF-pwsh 存在
    expect(all[0].exists).toBe(false)
    expect(all[1].exists).toBe(false)
    expect(all[2]).toMatchObject({ path: PWSH_PF, exists: true })
  })
})
