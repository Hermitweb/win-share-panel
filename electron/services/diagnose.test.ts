import { describe, it, expect, vi, beforeEach } from 'vitest'

// 批1 t1：diagnose（一键诊断/自动修复）单测。
// 钉住三类关键行为：
// ① 脏/异常数据不崩：单项探测抛错记 warn 而非整体 reject，形状怪异如实归一；
// ② 非法参数在发起写命令之前就被拒（applyFix 的 icacls 注入面）；
// ③ 判定正确性：服务停→service:lanman、无放行规则→firewall:smb、NTFS 无可读授权→acl:read-everyone、共享权限为空→fail。
// 执行层（powershell）、文件系统（existsSync）、审计、以及 smb/user/firewall 服务全部 mock，
// 但 psQuote/validateShareName/validatePath 用真实实现（importOriginal 展开），保证校验防线被测的是生产逻辑。
const { mockedRunPowerShell, mockedRunPowerShellVoid, mockedExistsSync, mockedAudit } = vi.hoisted(
  () => ({
    mockedRunPowerShell: vi.fn(),
    mockedRunPowerShellVoid: vi.fn(),
    mockedExistsSync: vi.fn((_p: unknown) => true),
    mockedAudit: vi.fn(),
  }),
)

vi.mock('../lib/powershell', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../lib/powershell')>()
  return {
    ...actual,
    runPowerShell: mockedRunPowerShell,
    runPowerShellVoid: mockedRunPowerShellVoid,
  }
})
vi.mock('fs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('fs')>()
  return { ...actual, existsSync: mockedExistsSync }
})
vi.mock('../lib/audit', () => ({ audit: mockedAudit }))
vi.mock('./smb', () => ({
  getServiceStatus: vi.fn(),
  getConfig: vi.fn(),
  startService: vi.fn(),
}))
vi.mock('./user', () => ({
  getNtfsPermissions: vi.fn(),
  getSharePermissions: vi.fn(),
}))
vi.mock('./firewall', () => ({
  listManagedRules: vi.fn(),
  ensureRules: vi.fn(),
}))

import { runDiagnose, applyFix } from './diagnose'
import * as smb from './smb'
import * as user from './user'
import * as firewall from './firewall'
import { AppError } from '../lib/errors'
import type { DiagnoseItem } from '../types'

const mockedSmb = vi.mocked(smb)
const mockedUser = vi.mocked(user)
const mockedFw = vi.mocked(firewall)

type PsFixtures = { port?: unknown; sharePath?: unknown; fwGroup?: unknown }
function stubPowerShell(over: PsFixtures = {}): void {
  // 注意用 `in` 判存在而不是 ?? ——测试数据本身允许 null/''/[]（"查无结果"形状）
  mockedRunPowerShell.mockImplementation(async (cmd: string) => {
    if (cmd.includes('Get-NetTCPConnection -LocalPort 445')) return 'port' in over ? over.port : 1
    if (cmd.includes('Get-SmbShare')) return 'sharePath' in over ? over.sharePath : 'E:\\docs'
    if (cmd.includes('@FirewallAPI.dll')) return 'fwGroup' in over ? over.fwGroup : 3
    throw new Error(`unexpected ps command: ${cmd}`)
  })
}

function byKey(items: DiagnoseItem[], key: string): DiagnoseItem {
  const it = items.find((i) => i.key === key)
  if (!it) throw new Error(`缺少诊断项 ${key}`)
  return it
}

beforeEach(() => {
  vi.clearAllMocks()
  stubPowerShell()
  mockedRunPowerShellVoid.mockResolvedValue(undefined)
  mockedExistsSync.mockReturnValue(true)
  mockedSmb.getServiceStatus.mockResolvedValue({
    name: 'LanmanServer',
    status: 'Running',
    startType: 'Automatic',
  })
  mockedSmb.getConfig.mockResolvedValue({
    enableSMB1Protocol: false,
    enableGuestUserAccess: false,
    enableInsecureGuestLogons: false,
  } as never)
  mockedSmb.startService.mockResolvedValue(undefined)
  mockedUser.getNtfsPermissions.mockResolvedValue({
    path: 'E:\\docs',
    entries: [
      { account: 'BUILTIN\\Administrators', rights: 'Full', type: 'Allow', inherited: true },
    ],
  })
  mockedUser.getSharePermissions.mockResolvedValue([
    { shareName: 'docs', account: 'Everyone', accountType: 'Group', access: 'Read', deny: false },
  ])
  mockedFw.listManagedRules.mockResolvedValue([])
  mockedFw.ensureRules.mockResolvedValue([])
})

describe('runDiagnose 全链路', () => {
  it('一切正常 → 9 项全 pass，不带 fix', async () => {
    const items = await runDiagnose({ shareName: 'docs' })
    expect(items).toHaveLength(9)
    expect(items.every((i) => i.status === 'pass')).toBe(true)
    expect(items.every((i) => i.fix === undefined)).toBe(true)
    // 共享路径只查一次，多 item 复用
    expect(
      mockedRunPowerShell.mock.calls.filter((c) => String(c[0]).includes('Get-SmbShare')),
    ).toHaveLength(1)
  })

  it('未指定共享名 → 相关项"跳过"式 pass，共 8 项', async () => {
    const items = await runDiagnose({})
    expect(items).toHaveLength(8)
    expect(items.every((i) => i.status === 'pass')).toBe(true)
    expect(byKey(items, 'share-exists').detail).toContain('跳过')
  })

  it('服务停止 → fail 且 fix=service:lanman', async () => {
    mockedSmb.getServiceStatus.mockResolvedValue({
      name: 'LanmanServer',
      status: 'Stopped',
      startType: 'Manual',
    })
    const items = await runDiagnose({ shareName: 'docs' })
    const it = byKey(items, 'smb-service')
    expect(it.status).toBe('fail')
    expect(it.fix).toBe('service:lanman')
    expect(it.detail).toContain('Stopped')
  })

  it('无本组规则且系统共享规则组未启用 → fail 且 fix=firewall:smb', async () => {
    stubPowerShell({ fwGroup: 0 })
    const items = await runDiagnose({ shareName: 'docs' })
    const it = byKey(items, 'firewall-smb')
    expect(it.status).toBe('fail')
    expect(it.fix).toBe('firewall:smb')
  })

  it('本组已存在启用规则 → pass 且不再探测系统规则组（不发多余读命令）', async () => {
    mockedFw.listManagedRules.mockResolvedValue([
      { name: 'WinShare SMB (445/TCP)', enabled: true, ports: '445' },
    ])
    const items = await runDiagnose({ shareName: 'docs' })
    expect(byKey(items, 'firewall-smb').status).toBe('pass')
    expect(
      mockedRunPowerShell.mock.calls.filter((c) => String(c[0]).includes('@FirewallAPI.dll')),
    ).toHaveLength(0)
  })

  it('NTFS 无可读授权 → fail 且 fix=acl:read-everyone', async () => {
    mockedUser.getNtfsPermissions.mockResolvedValue({
      path: 'E:\\docs',
      entries: [{ account: 'CONTOSO\\bob', rights: 'Write', type: 'Allow', inherited: false }],
    })
    const items = await runDiagnose({ shareName: 'docs' })
    const it = byKey(items, 'ntfs-read')
    expect(it.status).toBe('fail')
    expect(it.fix).toBe('acl:read-everyone')
    expect(it.detail).toContain('未授予任何可读权限')
  })

  it('共享权限为空 → fail「任何人都连不上」', async () => {
    mockedUser.getSharePermissions.mockResolvedValue([])
    const items = await runDiagnose({ shareName: 'docs' })
    const it = byKey(items, 'share-perms')
    expect(it.status).toBe('fail')
    expect(it.detail).toContain('共享权限为空')
  })

  it('Everyone 完全控制 → pass 但给出收敛建议文案', async () => {
    mockedUser.getSharePermissions.mockResolvedValue([
      { shareName: 'docs', account: 'Everyone', accountType: 'Group', access: 'Full', deny: false },
    ])
    const items = await runDiagnose({ shareName: 'docs' })
    const it = byKey(items, 'share-perms')
    expect(it.status).toBe('pass')
    expect(it.detail).toContain('建议收敛')
  })

  it('SMB1 开启 / 访客开放 → 对应安全项 fail', async () => {
    mockedSmb.getConfig.mockResolvedValue({
      enableSMB1Protocol: true,
      enableGuestUserAccess: true,
      enableInsecureGuestLogons: false,
    } as never)
    const items = await runDiagnose({})
    expect(byKey(items, 'smb1').status).toBe('fail')
    expect(byKey(items, 'guest').status).toBe('fail')
  })
})

describe('runDiagnose 脏数据/单项故障容错', () => {
  it('单项探测抛错 → 该项 warn（探测失败），其余照常，整体不 reject', async () => {
    mockedRunPowerShell.mockImplementation(async (cmd: string) => {
      if (cmd.includes('Get-NetTCPConnection')) throw new Error('WMI 炸了')
      if (cmd.includes('Get-SmbShare')) return 'E:\\docs'
      return 1
    })
    const items = await runDiagnose({ shareName: 'docs' })
    expect(items).toHaveLength(9)
    const it = byKey(items, 'port-445')
    expect(it.status).toBe('warn')
    expect(it.detail).toContain('探测失败：WMI 炸了')
    // 其他项不受牵连
    expect(byKey(items, 'smb-service').status).toBe('pass')
  })

  it('Get-SmbShare 空数组/空串（单值查询坑）→ 如实判"共享不存在"，不崩', async () => {
    for (const dirty of [[], '', null]) {
      stubPowerShell({ sharePath: dirty })
      const items = await runDiagnose({ shareName: 'docs' })
      expect(byKey(items, 'share-exists').status).toBe('fail')
      expect(byKey(items, 'path-exists').detail).toContain('共享不存在')
      expect(byKey(items, 'ntfs-read').detail).toContain('共享不存在')
    }
  })

  it('端口计数为字符串数字也判对（"2 个监听"）', async () => {
    stubPowerShell({ port: '2' })
    const items = await runDiagnose({})
    expect(byKey(items, 'port-445').status).toBe('pass')
    expect(byKey(items, 'port-445').detail).toBe('2 个监听')
  })

  it('共享存在但本地路径已被删 → path-exists fail，ntfs-read 直接判路径不存在', async () => {
    stubPowerShell({ sharePath: 'E:\\gone' })
    mockedExistsSync.mockImplementation((p: unknown) => String(p) !== 'E:\\gone')
    const items = await runDiagnose({ shareName: 'docs' })
    expect(byKey(items, 'share-exists').status).toBe('pass')
    expect(byKey(items, 'path-exists').status).toBe('fail')
    expect(byKey(items, 'ntfs-read').status).toBe('fail')
    expect(byKey(items, 'ntfs-read').detail).toContain('路径不存在')
    expect(mockedUser.getNtfsPermissions).not.toHaveBeenCalled()
  })

  it('非法共享名在读命令拼装前即被拒：记 warn，且任何 PowerShell 命令都不含该串', async () => {
    const hostile = "evil'); Get-Content C:\\Windows\\sam"
    const items = await runDiagnose({ shareName: hostile })
    expect(byKey(items, 'share-exists').status).toBe('warn')
    expect(byKey(items, 'share-exists').detail).toContain('共享名非法')
    for (const call of mockedRunPowerShell.mock.calls) {
      expect(String(call[0])).not.toContain('evil')
    }
  })
})

describe('applyFix 各修复动作', () => {
  it('service:lanman → 启动服务并写审计', async () => {
    const msg = await applyFix('service:lanman')
    expect(mockedSmb.startService).toHaveBeenCalledTimes(1)
    expect(msg).toContain('LanmanServer 服务已启动')
    expect(mockedAudit).toHaveBeenCalledWith('system', 'diagnoseFix', 'service:lanman', 'success')
  })

  it('firewall:smb → 新建时答"已添加"，已存在时答"无需重复添加"（不假成功）', async () => {
    mockedFw.ensureRules.mockResolvedValue(['WinShare SMB (445/TCP)'])
    expect(await applyFix('firewall:smb')).toContain('已添加防火墙规则')
    expect(mockedFw.ensureRules).toHaveBeenCalledWith([
      { name: 'WinShare SMB (445/TCP)', ports: '445' },
    ])
    mockedFw.ensureRules.mockResolvedValue([])
    expect(await applyFix('firewall:smb')).toContain('规则已存在，无需重复添加')
  })

  it('未知 fix 键 → INVALID_PARAM', async () => {
    const err = (await applyFix('rm -rf' as never).catch((x) => x)) as AppError
    expect(err.code).toBe('INVALID_PARAM')
    expect(err.message).toContain('未知修复项')
  })
})

describe('applyFix(acl:read-everyone)：icacls 写命令的发起前防线', () => {
  it('缺共享名 → 直接抛，连读命令都不发', async () => {
    await expect(applyFix('acl:read-everyone')).rejects.toThrow(/ACL 修复需要共享名/)
    expect(mockedRunPowerShell).not.toHaveBeenCalled()
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('非法共享名（注入形态）→ validateShareName 拦截，任何命令都不发', async () => {
    await expect(
      applyFix('acl:read-everyone', { shareName: "evil'); Set-Content x" }),
    ).rejects.toThrow(/共享名非法/)
    expect(mockedRunPowerShell).not.toHaveBeenCalled()
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('共享不存在 → SHARE_NOT_FOUND，不发 icacls', async () => {
    stubPowerShell({ sharePath: null })
    const err = (await applyFix('acl:read-everyone', { shareName: 'docs' }).catch(
      (x) => x,
    )) as AppError
    expect(err.code).toBe('SHARE_NOT_FOUND')
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('回读路径含通配符（validatePath 不过）→ 拒绝，icacls 一条不发', async () => {
    stubPowerShell({ sharePath: 'E:\\bad*dir' })
    await expect(applyFix('acl:read-everyone', { shareName: 'docs' })).rejects.toThrow(
      /共享路径非法，无法执行 ACL 修复/,
    )
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('回读路径超 260 → 拒绝，icacls 不发', async () => {
    stubPowerShell({ sharePath: 'C:\\' + 'a'.repeat(258) })
    await expect(applyFix('acl:read-everyone', { shareName: 'docs' })).rejects.toThrow(
      /共享路径非法/,
    )
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('回读路径不存在 → 拒绝，icacls 不发', async () => {
    stubPowerShell({ sharePath: 'E:\\gone' })
    mockedExistsSync.mockImplementation((p: unknown) => String(p) !== 'E:\\gone')
    await expect(applyFix('acl:read-everyone', { shareName: 'docs' })).rejects.toThrow(
      /共享路径不存在：E:\\gone/,
    )
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })

  it('合法存在路径 → 用 Everyone 的 SID(*S-1-1-0) 发只读授权并审计', async () => {
    stubPowerShell({ sharePath: 'E:\\docs' })
    const msg = await applyFix('acl:read-everyone', { shareName: 'docs' })
    expect(msg).toContain('已对 E:\\docs 授予 Everyone 读取')
    const cmd = mockedRunPowerShellVoid.mock.calls[0][0] as string
    expect(cmd).toContain('icacls')
    expect(cmd).toContain("Get-Item -LiteralPath 'E:\\docs'")
    expect(cmd).toContain('*S-1-1-0:(OI)(CI)R')
    expect(mockedAudit).toHaveBeenCalledWith(
      'system',
      'diagnoseFix',
      'acl:read-everyone:E:\\docs',
      'success',
    )
  })
})
