import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'

// 与 powershell.test.ts 同一手法：vi.mock('../lib/powershell') 把真实 PS 调用替掉，
// 只断言"命令拼对了没"，不触发任何子进程。
vi.mock('../lib/powershell', () => ({
  runPowerShell: vi.fn(async () => ''),
  runPowerShellVoid: vi.fn(async () => undefined),
}))
vi.mock('../lib/logger', () => ({ log: { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn() } }))
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
Object.defineProperty(process, 'resourcesPath', { value: 'C:\\Program Files\\WinShare Panel\\resources' })

const mockedRunPowerShell = vi.mocked(runPowerShell)
const mockedRunPowerShellVoid = vi.mocked(runPowerShellVoid)

describe('isAppInUserPath', () => {
  beforeEach(() => {
    mockedRunPowerShell.mockReset()
  })

  it('已含本应用目录（精确匹配）→ true', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('C:\\other;C:\\Program Files\\WinShare Panel;D:\\bin' as never)
    await expect(pathRegistration.isAppInUserPath()).resolves.toBe(true)
  })

  it('已含带引号的目录 → true', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('"C:\\Program Files\\WinShare Panel";C:\\other' as never)
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
    mockedRunPowerShell.mockResolvedValueOnce('C:\\existing;C:\\Program Files\\WinShare Panel' as never)
    const r = await pathRegistration.registerAppInUserPath()
    expect(r.registered).toBe(true)
    expect(r.message).toMatch(/无需重复注册/)
    // 只有 1 次读，没有写入
    expect(mockedRunPowerShell.mock.calls.length).toBe(1)
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('PATH 广播失败（PS 抛错）：registered 仍为 true，message 注明需重开终端', async () => {
    mockedRunPowerShell.mockResolvedValueOnce('' as never)
    // 第一次调用（read）成功；广播（broadcastPathChange 内的 runPowerShellVoid）抛错
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
    mockedRunPowerShell.mockResolvedValueOnce('C:\\a;C:\\Program Files\\WinShare Panel;C:\\b' as never)
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
