import { describe, it, expect, vi, beforeEach } from 'vitest'

// 与 powershell.test.ts 同一手法：vi.mock('../lib/powershell') 把真实 PS 调用替掉，
// 只断言"命令拼对了没"，不触发任何子进程。
vi.mock('../lib/powershell', () => ({
  runPowerShell: vi.fn(async () => ''),
  runPowerShellVoid: vi.fn(async () => undefined),
}))
vi.mock('../lib/logger', () => ({
  log: { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn() },
}))
vi.mock('electron', () => ({
  app: {
    isPackaged: true,
    getAppPath: () => 'C:\\apps\\winshare',
  },
}))

import * as pathRegistration from './pathRegistration'
import { runPowerShell, runPowerShellVoid } from '../lib/powershell'
import process from 'node:process'

// process.resourcesPath 是打包版注入的；mock 成 $INSTDIR 形态
// configurable: true —— 允许各用例重指向不同安装目录（回归"含括号路径"用）
Object.defineProperty(process, 'resourcesPath', {
  value: 'C:\\Program Files\\WinShare Panel\\resources',
  configurable: true,
})

/** 把安装目录重指向另一处（currentUserPathDir() 取 resourcesPath 的父目录） */
function pointInstallDir(dir: string): void {
  Object.defineProperty(process, 'resourcesPath', {
    value: `${dir}\\resources`,
    configurable: true,
  })
}

const mockedRunPowerShell = vi.mocked(runPowerShell)
const mockedRunPowerShellVoid = vi.mocked(runPowerShellVoid)

describe('isAppInUserPath', () => {
  beforeEach(() => {
    mockedRunPowerShell.mockReset()
  })

  it('已含本应用目录（精确匹配）→ true', async () => {
    mockedRunPowerShell.mockResolvedValueOnce(
      'C:\\other;C:\\Program Files\\WinShare Panel;D:\\bin' as never,
    )
    await expect(pathRegistration.isAppInUserPath()).resolves.toBe(true)
  })

  it('已含带引号的目录 → true', async () => {
    mockedRunPowerShell.mockResolvedValueOnce(
      '"C:\\Program Files\\WinShare Panel";C:\\other' as never,
    )
    await expect(pathRegistration.isAppInUserPath()).resolves.toBe(true)
  })

  it('PATH 缺失 → false', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('' as never)
    await expect(pathRegistration.isAppInUserPath()).resolves.toBe(false)
  })

  it('相似但不精确匹配（子路径）→ false', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('C:\\Program Files\\WinShare Panel Sub' as never)
    await expect(pathRegistration.isAppInUserPath()).resolves.toBe(false)
  })
})

describe('registerAppInUserPath', () => {
  beforeEach(() => {
    mockedRunPowerShell.mockReset()
    mockedRunPowerShellVoid.mockReset()
  })

  it('未注册：追加并广播（PS 可用）', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('C:\\existing' as never)
    const r = await pathRegistration.registerAppInUserPath()
    expect(r.registered).toBe(true)
    expect(r.broadcast).toBe(true)
    // 追加后的 PATH 是 C:\existing;C:\Program Files\WinShare Panel
    const writeCall = mockedRunPowerShellVoid.mock.calls[0]?.[0] as string
    expect(writeCall).toContain('Set-ItemProperty')
    expect(writeCall).toContain('C:\\existing')
    expect(writeCall).toContain('WinShare Panel')
  })

  it('已注册：幂等返回（不重写 PATH）', async () => {
    mockedRunPowerShell.mockResolvedValueOnce(
      'C:\\existing;C:\\Program Files\\WinShare Panel' as never,
    )
    const r = await pathRegistration.registerAppInUserPath()
    expect(r.registered).toBe(true)
    expect(r.message).toMatch(/无需重复注册/)
    // 只有 1 次读，没有写入
    expect(mockedRunPowerShell.mock.calls.length).toBe(1)
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('PATH 广播失败（PS 抛错）：registered 仍为 true，message 注明需重开终端', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('' as never)
    // 调用序：runPowerShell(读 PATH) → runPowerShellVoid#1(setUserPath 写入) → runPowerShellVoid#2(broadcast 广播)
    // 只想让**广播**失败，故第 1 次 void 调用（写入）必须成功：
    // setUserPath 没有 try/catch，错误会在写入处冒泡（broadcastPathChange 才有 catch → 返回 false）。
    mockedRunPowerShellVoid.mockImplementationOnce(async () => undefined)
    mockedRunPowerShellVoid.mockImplementationOnce(async () => {
      throw new Error('PS 不可用')
    })
    const r = await pathRegistration.registerAppInUserPath()
    expect(r.registered).toBe(true)
    expect(r.broadcast).toBe(false)
    expect(r.message).toMatch(/需重开终端/)
  })
})

describe('unregisterAppFromUserPath', () => {
  beforeEach(() => {
    mockedRunPowerShell.mockReset()
    mockedRunPowerShellVoid.mockReset()
  })

  it('已注册：移除本应用目录，保留其他段', async () => {
    mockedRunPowerShell.mockResolvedValueOnce(
      'C:\\a;C:\\Program Files\\WinShare Panel;C:\\b' as never,
    )
    const r = await pathRegistration.unregisterAppFromUserPath()
    expect(r.registered).toBe(false)
    const writeCall = mockedRunPowerShellVoid.mock.calls[0]?.[0] as string
    expect(writeCall).toMatch(/C:\\a;C:\\b/)
    expect(writeCall).not.toMatch(/WinShare Panel/)
  })

  it('未注册：幂等返回，不重写', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('C:\\a;C:\\b' as never)
    const r = await pathRegistration.unregisterAppFromUserPath()
    expect(r.registered).toBe(false)
    expect(r.message).toMatch(/无需撤销/)
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })
})

describe('setLoginShortcut', () => {
  beforeEach(() => {
    mockedRunPowerShell.mockReset()
    mockedRunPowerShellVoid.mockReset()
  })

  it('enabled=true：写 HKCU Run 键', async () => {
    const r = await pathRegistration.setLoginShortcut(true)
    expect(r).toMatch(/已写入登录快速启动/)
    const writeCall = mockedRunPowerShellVoid.mock.calls[0]?.[0] as string
    expect(writeCall).toContain('Set-ItemProperty')
    expect(writeCall).toContain('Run')
    expect(writeCall).toContain('WinSharePanel')
  })

  it('enabled=false：删 HKCU Run 键', async () => {
    const r = await pathRegistration.setLoginShortcut(false)
    expect(r).toMatch(/已删除登录快速启动/)
    const writeCall = mockedRunPowerShellVoid.mock.calls[0]?.[0] as string
    expect(writeCall).toContain('Remove-ItemProperty')
  })
})

describe('readServicePendingMarker', () => {
  beforeEach(() => {
    mockedRunPowerShell.mockReset()
  })

  it('HKLM ServicePending=1 → true', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('1' as never)
    await expect(pathRegistration.readServicePendingMarker()).resolves.toBe(true)
  })

  it('HKLM ServicePending 缺失 → false', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('' as never)
    await expect(pathRegistration.readServicePendingMarker()).resolves.toBe(false)
  })

  it('PS 调用抛错 → false（静默降级）', async () => {
    mockedRunPowerShell.mockImplementationOnce(async () => {
      throw new Error('PS 不可用')
    })
    await expect(pathRegistration.readServicePendingMarker()).resolves.toBe(false)
  })
})

/**
 * 回归：正则元字符与反斜杠两类"字面量丢失"缺陷。
 *
 * ① 目录路径未转义就拼进 RegExp → 装到 `C:\Program Files (x86)\...` 时，
 *    `(x86)` 被当成捕获组，真实段匹配不上，反而匹配到形似但不同的
 *    `C:\Program Files x86\...` → 撤销 PATH 会**移除错段、留下真段**。
 * ② JS 字符串里 '\W' 会被丢弃反斜杠（只剩 W）→ 发给 PS 的注册表路径变成
 *    'HKLM:SoftwareWinSharePanel'，那是另一个不存在的键，标记恒读为 false。
 */
describe('回归：路径字面量转义', () => {
  const PAREN_DIR = 'C:\\Program Files (x86)\\WinShare Panel'
  const LOOKALIKE = 'C:\\Program Files x86\\WinShare Panel'

  beforeEach(() => {
    mockedRunPowerShell.mockReset()
    mockedRunPowerShellVoid.mockReset()
    pointInstallDir(PAREN_DIR)
  })

  it('撤销时只移除真实段（含括号），保留形似但不同的段', async () => {
    mockedRunPowerShell.mockResolvedValueOnce(`C:\\a;${PAREN_DIR};${LOOKALIKE};C:\\b` as never)
    const r = await pathRegistration.unregisterAppFromUserPath()
    expect(r.registered).toBe(false)
    const writeCall = mockedRunPowerShellVoid.mock.calls[0]?.[0] as string
    // 真实段（含括号）被移除
    expect(writeCall).not.toContain('(x86)')
    // 形似段必须保留 —— 旧实现会把它当成真实段删掉
    expect(writeCall).toContain('Program Files x86')
    expect(writeCall).toContain('C:\\a')
    expect(writeCall).toContain('C:\\b')
  })

  it('带引号的真实段（含括号）同样能被识别与撤销', async () => {
    mockedRunPowerShell.mockResolvedValueOnce(`"${PAREN_DIR}";C:\\b` as never)
    const r = await pathRegistration.unregisterAppFromUserPath()
    expect(r.registered).toBe(false)
    const writeCall = mockedRunPowerShellVoid.mock.calls[0]?.[0] as string
    expect(writeCall).toContain('C:\\b')
    expect(writeCall).not.toContain('(x86)')
  })

  it('isAppInUserPath：含括号路径能精确匹配，形似路径不算', async () => {
    mockedRunPowerShell.mockResolvedValueOnce(`C:\\a;${PAREN_DIR}` as never)
    await expect(pathRegistration.isAppInUserPath()).resolves.toBe(true)
    mockedRunPowerShell.mockResolvedValueOnce(`C:\\a;${LOOKALIKE}` as never)
    await expect(pathRegistration.isAppInUserPath()).resolves.toBe(false)
  })

  it('readServicePendingMarker：注册表路径的反斜杠不能被 JS 吞掉', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('1' as never)
    await pathRegistration.readServicePendingMarker()
    const cmd = mockedRunPowerShell.mock.calls[0]?.[0] as string
    // 期望运行时字符串含单个反斜杠分隔符：HKLM:Software\WinSharePanel
    expect(cmd).toContain('HKLM:Software\\WinSharePanel')
    // 反证：被吞掉的形式（无分隔符）不得出现
    expect(cmd).not.toContain('HKLM:SoftwareWinSharePanel')
  })
})
