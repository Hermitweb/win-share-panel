import { describe, it, expect, vi, beforeEach } from 'vitest'

// 批1 t1：firewall 单测。
// 钉住：presetRules 各 kind 端口/协议、ftpPassive 非法范围 INVALID_PARAM、未知 kind 报错且回显消毒；
// ensureRules 幂等（已存在不新建、只返回本次新建名）、非法名/端口在发起任何写命令之前抛出（注入面）。
// 校验/引号辅助用真实实现（importOriginal 展开），只 mock 执行层 runPowerShell/Void。
const { mockedRunPowerShell, mockedRunPowerShellVoid } = vi.hoisted(() => ({
  mockedRunPowerShell: vi.fn(),
  mockedRunPowerShellVoid: vi.fn(),
}))

vi.mock('../lib/powershell', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../lib/powershell')>()
  return {
    ...actual,
    runPowerShell: mockedRunPowerShell,
    runPowerShellVoid: mockedRunPowerShellVoid,
  }
})

import { presetRules, ensureRules, removeRule, listManagedRules, FW_GROUP } from './firewall'
import { AppError } from '../lib/errors'

/** 抓同步抛出物（断言 code 用） */
function grabError(fn: () => unknown): AppError | null {
  try {
    fn()
    return null
  } catch (e) {
    return e as AppError
  }
}

/** 让 listManagedRules 读到"已存在的规则名集" */
function existingRules(...names: string[]) {
  mockedRunPowerShell.mockResolvedValue(
    names.map((n) => ({ DisplayName: n, Enabled: 'True', Ports: '445' })),
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  mockedRunPowerShellVoid.mockResolvedValue(undefined)
})

describe('presetRules 预设表', () => {
  it('smb/ftp/webdav/quic 端口与协议正确', () => {
    expect(presetRules('smb')).toEqual([{ name: 'WinShare SMB (445/TCP)', ports: '445' }])
    const ftp = presetRules('ftp')
    expect(ftp).toHaveLength(2)
    expect(ftp.map((r) => r.ports)).toEqual(['21', '50000-51000'])
    expect(presetRules('webdav')).toEqual([{ name: 'WinShare WebDAV (80/TCP)', ports: '80' }])
    expect(presetRules('quic')).toEqual([
      { name: 'WinShare SMB QUIC (445/UDP)', ports: '445', protocol: 'UDP' },
    ])
  })

  it('ftpPassive：缺省与合法自定义范围（含边界 1-65535）', () => {
    expect(presetRules('ftpPassive')).toEqual([
      { name: 'WinShare FTP Passive (50000-51000/TCP)', ports: '50000-51000' },
    ])
    expect(presetRules('ftpPassive', { passiveFrom: 20000, passiveTo: 20100 })).toEqual([
      { name: 'WinShare FTP Passive (20000-20100/TCP)', ports: '20000-20100' },
    ])
    expect(presetRules('ftpPassive', { passiveFrom: 1, passiveTo: 65535 })[0].ports).toBe('1-65535')
  })

  it('ftpPassive：非法范围 → INVALID_PARAM（0 起点 / 超 65535 / from>to）', () => {
    for (const opts of [
      { passiveFrom: 0, passiveTo: 100 },
      { passiveFrom: 100, passiveTo: 65536 },
      { passiveFrom: 200, passiveTo: 100 },
    ]) {
      const e = grabError(() => presetRules('ftpPassive', opts))
      expect(e).toBeInstanceOf(AppError)
      expect(e?.code).toBe('INVALID_PARAM')
      expect(e?.message).toContain('被动端口范围非法')
    }
  })

  it('ftpPassive：非整数参数静默回落到默认范围（记录现状行为）', () => {
    expect(
      presetRules('ftpPassive', { passiveFrom: 50000.5, passiveTo: 'x' as never })[0].ports,
    ).toBe('50000-51000')
  })

  it('未知 kind：抛 INVALID_PARAM；非法字符的 kind 在错误消息中消毒为 ***（报错回显也是注入面）', () => {
    const safe = grabError(() => presetRules('foo'))
    expect(safe?.code).toBe('INVALID_PARAM')
    expect(safe?.message).toContain('foo')

    const hostile = grabError(() => presetRules("evil'); Set-Content \\tmp"))
    expect(hostile?.message).toContain('***')
    expect(hostile?.message).not.toContain('evil')
  })
})

describe('ensureRules 幂等与写前拦截', () => {
  it('空数组/非数组入参 → 直接返回 []，零条命令', async () => {
    await expect(ensureRules([])).resolves.toEqual([])
    await expect(ensureRules(undefined as never)).resolves.toEqual([])
    await expect(ensureRules('nope' as never)).resolves.toEqual([])
    expect(mockedRunPowerShell).not.toHaveBeenCalled()
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('超过 20 条一次性请求 → 读和写都不发起', async () => {
    const desired = Array.from({ length: 21 }, (_, i) => ({ name: `r${i}`, ports: '445' }))
    await expect(ensureRules(desired)).rejects.toThrow(/单次规则请求过多/)
    expect(mockedRunPowerShell).not.toHaveBeenCalled()
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('幂等：已存在规则不重建，只返回本次新建的名', async () => {
    // 注：NAME_RE 不含 '/'，而 presetRules 产出的名字含 "(445/TCP)"——预设名实际过不了
    // 本模块自校验（已作为源码缺陷报给 captain）。此处用 NAME_RE 合法名钉幂等语义。
    existingRules('WinShare SMB 445')
    const created = await ensureRules([
      { name: 'WinShare SMB 445', ports: '445' },
      { name: 'WinShare FTP 21', ports: '21' },
    ])
    expect(created).toEqual(['WinShare FTP 21'])
    expect(mockedRunPowerShellVoid).toHaveBeenCalledTimes(1)
    const cmd = mockedRunPowerShellVoid.mock.calls[0][0] as string
    expect(cmd).toContain("-DisplayName 'WinShare FTP 21'")
    expect(cmd).toContain("-Protocol 'TCP'")
    expect(cmd).toContain("-LocalPort '21'")
    expect(cmd).toContain(`-Group '${FW_GROUP}'`)
  })

  it('非法规则名（注入形态）→ 抛出，写命令一条都不发', async () => {
    existingRules() // 空表
    await expect(
      ensureRules([{ name: "evil'; Remove-NetFirewallRule; #", ports: '445' }]),
    ).rejects.toThrow(/规则名非法/)
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('非法端口（分号/字母/空格/空串）→ 抛出，写命令零调用', async () => {
    for (const ports of ['445;446', '4p5', '445 446', '']) {
      await expect(ensureRules([{ name: 'ok', ports: ports as never }])).rejects.toThrow(
        /端口格式非法/,
      )
    }
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('UDP 显式透传；多端口拆分逐个加引号', async () => {
    existingRules()
    await ensureRules([{ name: 'r', ports: '445,446', protocol: 'UDP' }])
    const cmd = mockedRunPowerShellVoid.mock.calls[0][0] as string
    expect(cmd).toContain("-LocalPort '445','446'")
    expect(cmd).toContain("-Protocol 'UDP'")
  })

  it('protocol 只接受 TCP/UDP/未提供，其他值写前拒绝（原"静默归一为 TCP"契约已收紧）', async () => {
    // 契约变更（captain）：旧实现用三元把任意非法 protocol 归一成 TCP，紧随其后的
    // `proto !== 'TCP' && proto !== 'UDP'` 因此恒为假（死分支，校验意图未落实）。
    // 现在显式拒绝非法值：注入安全性不变（协议字段仍不可能成为载体），但非法入参不再被悄悄接受。
    existingRules()
    await expect(
      ensureRules([{ name: 'r', ports: '445', protocol: "TCP'; x" as never }]),
    ).rejects.toThrow(/协议非法/)
    await expect(ensureRules([{ name: 'r', ports: '445', protocol: '' as never }])).rejects.toThrow(
      /协议非法/,
    )
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()

    // 合法值与未提供照常下发（未提供默认 TCP）
    await ensureRules([
      { name: 'r1', ports: '445', protocol: 'TCP' },
      { name: 'r2', ports: '445', protocol: 'UDP' },
      { name: 'r3', ports: '445' },
    ])
    const cmds = mockedRunPowerShellVoid.mock.calls.map((c) => c[0] as string)
    expect(cmds[0]).toContain("-Protocol 'TCP'")
    expect(cmds[1]).toContain("-Protocol 'UDP'")
    expect(cmds[2]).toContain("-Protocol 'TCP'")
  })

  it('混合非法项（合法在前）也零写入——不留部分提交（回归：原校验与写入同循环交错）', async () => {
    existingRules()
    await expect(
      ensureRules([
        { name: 'WinShare OK 21', ports: '21' },
        { name: "evil'; Remove-NetFirewallRule; #", ports: '445' },
      ]),
    ).rejects.toThrow(/规则名非法/)
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('★ 每个预设 kind 都能通过本模块自校验（回归：白名单缺 "/" 曾让全部预设恒抛）', async () => {
    // 这条正是 qa-main t1 报出的 blocker 回归钉：预设名含 "(445/TCP)"，
    // NAME_RE 不放行 "/" 时 ensureRules(presetRules(kind)) 恒 INVALID_PARAM，
    // 一键诊断的 firewall:smb 修复项与预设建/删规则在运行时全挂。
    existingRules() // 组内为空 → 全部待建
    for (const kind of ['smb', 'ftp', 'webdav', 'quic', 'ftpPassive'] as const) {
      const desired = presetRules(kind)
      await expect(ensureRules(desired)).resolves.toEqual(desired.map((d) => d.name))
    }
    const names = mockedRunPowerShellVoid.mock.calls.map(
      (c) => (c[0] as string).match(/-DisplayName '([^']+)'/)?.[1],
    )
    expect(names).toContain('WinShare SMB (445/TCP)')
    expect(names).toContain('WinShare SMB QUIC (445/UDP)')
    // removeRule 用同一个白名单，预设名也必须能被删除，否则规则只进不出
    await expect(removeRule('WinShare SMB (445/TCP)')).resolves.toBeUndefined()
  })
})

describe('listManagedRules / removeRule', () => {
  it('单对象/null/数组三种 PowerShell 形状归一；Ports 缺失转空串', async () => {
    mockedRunPowerShell.mockResolvedValue({ DisplayName: 'a', Enabled: 'True', Ports: '445' })
    expect(await listManagedRules()).toEqual([{ name: 'a', enabled: true, ports: '445' }])

    mockedRunPowerShell.mockResolvedValue(null)
    expect(await listManagedRules()).toEqual([])

    mockedRunPowerShell.mockResolvedValue([
      { DisplayName: 'b', Enabled: false },
      { DisplayName: '' }, // 无名脏条目滤掉
    ])
    expect(await listManagedRules()).toEqual([{ name: 'b', enabled: false, ports: '' }])
  })

  it('removeRule：非法名先拒（零写）；合法名只删 WinShare Panel 组内规则', async () => {
    await expect(removeRule("x'")).rejects.toThrow(/规则名非法/)
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()

    await removeRule('WinShare SMB 445')
    const cmd = mockedRunPowerShellVoid.mock.calls[0][0] as string
    expect(cmd).toContain(`-DisplayGroup '${FW_GROUP}'`)
    expect(cmd).toContain("-eq 'WinShare SMB 445'")
    expect(cmd).toContain('Remove-NetFirewallRule')
  })
})
