import { describe, it, expect, vi, beforeEach } from 'vitest'

// 批1 t1：security（账号安全体检）单测。
// 钉住：PasswordRequired=false→fail（且短路该用户其余检查）、PasswordExpires=false→warn、
// 密码超 180 天→warn、禁用账号跳过、单对象/数组两种 PowerShell 返回形状归一、脏条目不崩。
const { mockedRunPowerShell } = vi.hoisted(() => ({ mockedRunPowerShell: vi.fn() }))

vi.mock('../lib/powershell', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../lib/powershell')>()
  return { ...actual, runPowerShell: mockedRunPowerShell }
})

import { securityReport } from './security'

const DAY = 24 * 3600 * 1000
const iso = (daysAgo: number) => new Date(Date.now() - daysAgo * DAY).toISOString()

type RawUser = {
  Name: string
  Enabled: boolean
  PasswordRequired: boolean
  PasswordExpires: boolean
  PasswordLastSet: string | null
}

function user(partial: Partial<RawUser> = {}): RawUser {
  return {
    Name: 'u1',
    Enabled: true,
    PasswordRequired: true,
    PasswordExpires: true,
    PasswordLastSet: iso(10),
    ...partial,
  }
}

beforeEach(() => {
  vi.clearAllMocks()
  mockedRunPowerShell.mockResolvedValue([])
})

describe('securityReport 判级', () => {
  it('PasswordRequired=false → fail，并短路该用户后续检查（不再叠 warn）', async () => {
    mockedRunPowerShell.mockResolvedValue([
      user({
        Name: 'guest01',
        PasswordRequired: false,
        PasswordExpires: false,
        PasswordLastSet: iso(400),
      }),
    ])
    const r = await securityReport()
    expect(r.checked).toBe(1)
    expect(r.issues).toEqual([
      { user: 'guest01', level: 'fail', issue: '账号设置了"密码永不需要"，可空口令登录' },
    ])
  })

  it('PasswordExpires=false → warn（密码永不过期）', async () => {
    mockedRunPowerShell.mockResolvedValue([
      user({ PasswordExpires: false, PasswordLastSet: iso(1) }),
    ])
    const r = await securityReport()
    expect(r.issues).toEqual([
      { user: 'u1', level: 'warn', issue: '密码设置为永不过期（共享账号建议定期轮换）' },
    ])
  })

  it('密码超 180 天 → warn；恰好未超 → 干净', async () => {
    mockedRunPowerShell.mockResolvedValue([
      user({ Name: 'old', PasswordLastSet: iso(200) }),
      user({ Name: 'fresh', PasswordLastSet: iso(100) }),
    ])
    const r = await securityReport()
    expect(r.issues).toHaveLength(1)
    expect(r.issues[0].user).toBe('old')
    expect(r.issues[0].level).toBe('warn')
    expect(r.issues[0].issue).toMatch(/超过 180 天未修改/)
  })

  it('过期 + 永不过期可叠加为两条 warn', async () => {
    mockedRunPowerShell.mockResolvedValue([
      user({ PasswordExpires: false, PasswordLastSet: iso(199) }),
    ])
    const r = await securityReport()
    expect(r.issues.map((i) => i.issue)).toHaveLength(2)
    expect(r.issues.every((i) => i.level === 'warn')).toBe(true)
  })

  it('禁用账号整体跳过：不出 issue、不计入 checked', async () => {
    mockedRunPowerShell.mockResolvedValue([
      user({ Name: 'off', Enabled: false, PasswordRequired: false, PasswordLastSet: iso(999) }),
      user({ Name: 'on' }),
    ])
    const r = await securityReport()
    expect(r.checked).toBe(1)
    expect(r.issues).toEqual([])
  })
})

describe('securityReport 形状归一与脏数据', () => {
  it('单对象返回（只查到一个用户时 PowerShell 不给数组）也归一处理', async () => {
    mockedRunPowerShell.mockResolvedValue(user({ PasswordRequired: false }))
    const r = await securityReport()
    expect(r.checked).toBe(1)
    expect(r.issues).toHaveLength(1)
    expect(r.issues[0].level).toBe('fail')
  })

  it('null/无 Name 脏条目滤掉且不崩', async () => {
    mockedRunPowerShell.mockResolvedValue([
      null,
      user({ Name: '' }),
      user({ Name: 'real', PasswordExpires: false }),
    ])
    const r = await securityReport()
    expect(r.checked).toBe(1)
    expect(r.issues).toHaveLength(1)
  })

  it('PasswordLastSet 非法字符串/null → 不抛错、不误报过期', async () => {
    mockedRunPowerShell.mockResolvedValue([
      user({ Name: 'weird', PasswordLastSet: 'not-a-date' }),
      user({ Name: 'nul', PasswordLastSet: null }),
    ])
    const r = await securityReport()
    expect(r.issues).toEqual([])
  })

  it('checked 只数启用账号；at 落在调用时刻附近', async () => {
    const before = Date.now()
    mockedRunPowerShell.mockResolvedValue([user(), user({ Name: 'b', Enabled: false })])
    const r = await securityReport()
    expect(r.checked).toBe(1)
    expect(r.at).toBeGreaterThanOrEqual(before)
    expect(r.at).toBeLessThanOrEqual(Date.now())
  })
})
