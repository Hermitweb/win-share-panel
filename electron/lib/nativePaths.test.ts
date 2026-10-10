import { describe, it, expect } from 'vitest'
import { nativeSystemPath, nativePowerShellExe } from './nativePaths'

// 位宽 → 路径的三条分支必须各自成立，且不依赖运行测试的机器：
// 用注入的 exists 模拟「64 位进程看不到 Sysnative / 32 位进程看得到」这一 WOW64 事实
// （本机实测：64 位 Test-Path Sysnative=False；32 位 SysWOW64\cmd =True）。
const ROOT = 'C:\\Windows'
const SYS = `${ROOT}\\System32`
const NATIVE = `${ROOT}\\Sysnative`
const PS_REL = 'WindowsPowerShell\\v1.0\\powershell.exe'

/** 只有 32 位进程能看到 Sysnative：exists 只对 Sysnative 下的路径返回 true */
const onlySysnative = (p: string): boolean => p.startsWith(`${NATIVE}\\`)
/** 64 位进程 / 纯 32 位系统：Sysnative 不存在 */
const noSysnative = (): boolean => false

describe('nativeSystemPath：32 位进程取 Sysnative，其余取 System32', () => {
  it('32 位进程在 64 位系统上（Sysnative 可见）：命中 Sysnative，取到原生 System32', () => {
    expect(
      nativeSystemPath(PS_REL, { platform: 'win32', systemRoot: ROOT, exists: onlySysnative }),
    ).toBe(`${NATIVE}\\${PS_REL}`)
  })

  it('64 位进程（Sysnative 不可见）：落回 System32，即本来就是原生路径', () => {
    expect(
      nativeSystemPath(PS_REL, { platform: 'win32', systemRoot: ROOT, exists: noSysnative }),
    ).toBe(`${SYS}\\${PS_REL}`)
  })

  it('纯 32 位系统（无 Sysnative）：落回 System32，即该系统上唯一可用的 32 位版本', () => {
    expect(
      nativeSystemPath('inetsrv\\appcmd.exe', {
        platform: 'win32',
        systemRoot: ROOT,
        exists: noSysnative,
      }),
    ).toBe(`${SYS}\\inetsrv\\appcmd.exe`)
  })

  it('非 win32 平台不探测 Sysnative（避免造出不存在的路径）', () => {
    expect(
      nativeSystemPath(PS_REL, { platform: 'linux', systemRoot: '/x', exists: onlySysnative }),
    ).toBe('/x\\System32\\WindowsPowerShell\\v1.0\\powershell.exe')
  })

  it('systemRoot 尾部反斜杠被规整，不会拼出双反斜杠', () => {
    expect(
      nativeSystemPath(PS_REL, {
        platform: 'win32',
        systemRoot: 'C:\\Windows\\',
        exists: noSysnative,
      }),
    ).toBe(`${SYS}\\${PS_REL}`)
  })

  it('未注入 systemRoot 时可用（读 %SystemRoot%/%windir%），返回值仍是绝对路径', () => {
    const p = nativeSystemPath(PS_REL, { platform: 'win32', exists: noSysnative })
    expect(p).toMatch(/^[A-Za-z]:\\/)
    expect(p.endsWith(`System32\\${PS_REL}`)).toBe(true)
  })
})

describe('nativePowerShellExe：与操作系统同位宽', () => {
  it('32 位进程 → 原生 64 位 PowerShell（Sysnative）——x86 包可用的关键', () => {
    expect(
      nativePowerShellExe({ platform: 'win32', systemRoot: ROOT, exists: onlySysnative }),
    ).toBe(`${NATIVE}\\${PS_REL}`)
  })

  it('64 位进程 → System32 下的 PowerShell', () => {
    expect(nativePowerShellExe({ platform: 'win32', systemRoot: ROOT, exists: noSysnative })).toBe(
      `${SYS}\\${PS_REL}`,
    )
  })

  it('非 Windows：返回裸名，交给 PATH（跨平台开发/纯逻辑用例不被硬编码路径挡住）', () => {
    expect(nativePowerShellExe({ platform: 'darwin', exists: onlySysnative })).toBe(
      'powershell.exe',
    )
    expect(nativePowerShellExe({ platform: 'linux', exists: onlySysnative })).toBe('powershell.exe')
  })
})
