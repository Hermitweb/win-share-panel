import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'

// 批1 t1：disk 单测。mock PowerShell 层与 fs.existsSync（用户目录探测），
// 钉住：单对象/数组两种返回形状归一、total=0 不出现 NaN、
// suggestShareRoot 的三级优先（非系统盘剩余最大 → 用户 Shared/共享的 → 系统盘）。
// psQuote/校验函数保持真实实现（照 importOriginal 手法），只换掉执行层。
const { mockedRunPowerShell, mockedExistsSync } = vi.hoisted(() => ({
  mockedRunPowerShell: vi.fn(),
  mockedExistsSync: vi.fn((_p: unknown) => true),
}))

vi.mock('../lib/powershell', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../lib/powershell')>()
  return { ...actual, runPowerShell: mockedRunPowerShell }
})
vi.mock('fs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('fs')>()
  return { ...actual, existsSync: mockedExistsSync }
})

import { getDiskUsages, driveOfPath, isUnc, suggestShareRoot, suggestRoot } from './disk'
import { join } from 'path'

beforeEach(() => {
  vi.clearAllMocks()
  vi.stubEnv('SystemDrive', 'C:')
})
afterEach(() => {
  vi.unstubAllEnvs()
})

describe('getDiskUsages 形状归一与脏数据', () => {
  it('单对象返回（PowerShell 单条管道不产数组的经典坑）归一为数组', async () => {
    mockedRunPowerShell.mockResolvedValue({ Name: 'C', Free: 50e9, Used: 150e9 })
    expect(await getDiskUsages()).toEqual([{ drive: 'C:', freeGB: 50, totalGB: 200, freePct: 25 }])
  })

  it('数组返回：透传全部合法盘符，滤掉脏条目（多字符/非字符串/null）', async () => {
    mockedRunPowerShell.mockResolvedValue([
      { Name: 'C', Free: 10e9, Used: 90e9 },
      { Name: 'E', Free: 300e9, Used: 100e9 },
      { Name: 'temp', Free: 1, Used: 1 },
      null,
      { Name: 3, Free: 1, Used: 1 },
    ])
    const us = await getDiskUsages()
    expect(us.map((d) => d.drive)).toEqual(['C:', 'E:'])
    expect(us[1]).toEqual({ drive: 'E:', freeGB: 300, totalGB: 400, freePct: 75 })
  })

  it('total=0 / 字段缺失一律归零且不出现 NaN（freePct 取 100）', async () => {
    mockedRunPowerShell.mockResolvedValue([
      { Name: 'F' }, // Free/Used 缺失
      { Name: 'G', Free: 0, Used: 0 },
      { Name: 'H', Free: null, Used: undefined },
    ])
    const us = await getDiskUsages()
    expect(us).toHaveLength(3)
    for (const d of us) {
      expect(Number.isFinite(d.freeGB)).toBe(true)
      expect(Number.isFinite(d.totalGB)).toBe(true)
      expect(Number.isNaN(d.freePct)).toBe(false)
      expect(d.freeGB).toBe(0)
      expect(d.totalGB).toBe(0)
      expect(d.freePct).toBe(100)
    }
  })

  it('GB 保留 1 位小数', async () => {
    mockedRunPowerShell.mockResolvedValue({ Name: 'C', Free: 5.25e9, Used: 0 })
    const [d] = await getDiskUsages()
    expect(d.freeGB).toBe(5.3)
    expect(d.totalGB).toBe(5.3)
    expect(d.freePct).toBe(100)
  })

  it('空/null 返回 → 空数组不抛错', async () => {
    mockedRunPowerShell.mockResolvedValue(null)
    expect(await getDiskUsages()).toEqual([])
  })
})

describe('driveOfPath / isUnc 判定', () => {
  it('driveOfPath：提取盘符并统一大写', () => {
    expect(driveOfPath('E:\\share\\x')).toBe('E:')
    expect(driveOfPath('c:\\Users')).toBe('C:')
    expect(driveOfPath('C:/a/b')).toBe('C:')
  })

  it('driveOfPath：UNC/非法/空/脏值 → null 而不抛错', () => {
    expect(driveOfPath('\\\\server\\share')).toBeNull()
    expect(driveOfPath('1:\\x')).toBeNull()
    expect(driveOfPath('')).toBeNull()
    expect(driveOfPath(undefined as never)).toBeNull()
    expect(driveOfPath(null as never)).toBeNull()
  })

  it('isUnc：双反斜杠+主机名为真；本地路径/单斜杠/脏值', () => {
    expect(isUnc('\\\\srv\\sh')).toBe(true)
    expect(isUnc('\\\\localhost\\public')).toBe(true)
    expect(isUnc('C:\\x')).toBe(false)
    expect(isUnc('\\\\')).toBe(false) // 只有分隔符，无主机名
    expect(isUnc('')).toBe(false)
    expect(isUnc(undefined as never)).toBe(false)
  })
})

describe('suggestShareRoot 智能默认三级优先', () => {
  const cUsage = { drive: 'C:', freeGB: 10, totalGB: 100, freePct: 10 }

  beforeEach(() => {
    mockedExistsSync.mockReturnValue(false)
  })

  it('优先非系统固定盘中剩余空间最大者（系统盘不放共享数据）', () => {
    const got = suggestShareRoot(
      [
        cUsage,
        { drive: 'E:', freeGB: 200, totalGB: 500, freePct: 40 },
        { drive: 'D:', freeGB: 300, totalGB: 1000, freePct: 30 },
      ],
      '',
    )
    expect(got).toBe('D:\\Share')
  })

  it('系统盘按 SystemDrive 环境变量识别：换系统盘后优先极随之改变', () => {
    vi.stubEnv('SystemDrive', 'D:')
    const got = suggestShareRoot(
      [
        { drive: 'D:', freeGB: 1000, totalGB: 1000, freePct: 100 }, // 变系统盘，剔除
        { drive: 'C:', freeGB: 2, totalGB: 100, freePct: 2 },
      ],
      '',
    )
    expect(got).toBe('C:\\Share')
  })

  it('只有系统盘时：退回用户主目录下已存在的 Shared', () => {
    const home = 'C:\\Users\\u'
    mockedExistsSync.mockImplementation((p: unknown) => String(p) === join(home, 'Shared'))
    expect(suggestShareRoot([cUsage], home)).toBe(join(home, 'Shared'))
    expect(mockedExistsSync).toHaveBeenCalledWith(join(home, 'Shared'))
  })

  it('无 Shared 但存在本地化「共享的」目录：用之', () => {
    const home = 'C:\\Users\\u'
    mockedExistsSync.mockImplementation((p: unknown) => String(p) === join(home, '共享的'))
    expect(suggestShareRoot([cUsage], home)).toBe(join(home, '共享的'))
  })

  it('两者皆不存在：回系统盘根 Share', () => {
    mockedExistsSync.mockReturnValue(false)
    expect(suggestShareRoot([cUsage], 'C:\\Users\\u')).toBe('C:\\Share')
  })

  it('无 userHome（空串）：跳过主目录探测直接回系统盘', () => {
    expect(suggestShareRoot([cUsage], '')).toBe('C:\\Share')
    expect(mockedExistsSync).not.toHaveBeenCalled()
  })

  it('空 usages 且无 home：兜底系统盘（不崩）', () => {
    expect(suggestShareRoot([], '')).toBe('C:\\Share')
  })
})

describe('suggestRoot（查盘 + 建议一体化）', () => {
  it('查盘成功 → 非系统盘建议', async () => {
    mockedRunPowerShell.mockResolvedValue({ Name: 'E', Free: 1e11, Used: 0 })
    expect(await suggestRoot()).toBe('E:\\Share')
  })

  it('查盘失败不卡向导：吞错后仍返回可用默认值', async () => {
    mockedRunPowerShell.mockRejectedValue(new Error('pwsh down'))
    mockedExistsSync.mockReturnValue(false)
    expect(await suggestRoot()).toBe('C:\\Share')
  })
})
