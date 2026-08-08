import { describe, it, expect, vi, beforeEach } from 'vitest'

// vi.hoisted 确保 mock 变量在 vi.mock 工厂函数（被提升到文件顶部）执行时可用
const { mockedRunPowerShell, mockedAdapterList, mockedAdapterSessions, mockedGetServiceStatus } = vi.hoisted(() => ({
  mockedRunPowerShell: vi.fn(),
  mockedAdapterList: vi.fn(),
  mockedAdapterSessions: vi.fn(),
  mockedGetServiceStatus: vi.fn()
}))

vi.mock('../lib/powershell', () => ({
  runPowerShell: mockedRunPowerShell,
  runPowerShellVoid: vi.fn(),
  psQuote: (v: string) => `'${v}'`,
  psBool: (v: unknown) => (typeof v === 'boolean' ? `$${v}` : null),
  psNumber: (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? String(v) : null),
  psEnum: (v: unknown, allowed: ReadonlySet<string>) => (typeof v === 'string' && allowed.has(v) ? v : null),
  validateName: (v: string) => !!v && v.length <= 80,
  validatePath: (v: string) => /^[A-Za-z]:[\\/]([^\u0000-\u001f<>:"|?*][^<>:"|?*]*)*$/.test(v),
  psEscapeSingle: (v: string) => v.replace(/'/g, "''")
}))

// Mock adapterList / adapterSessions（从 protocol/registry）
vi.mock('./protocol/registry', () => ({
  adapterList: mockedAdapterList,
  adapterSessions: mockedAdapterSessions
}))

// Mock getServiceStatus（从 ./smb）
vi.mock('./smb', () => ({
  getServiceStatus: mockedGetServiceStatus
}))

// Mock electron app（relaunchAsAdmin 引用，但 getDashboardStats 不用）
vi.mock('electron', () => ({ app: { isPackaged: false, quit: vi.fn() } }))

// Mock audit（同模块其他导出引用，不影响 getDashboardStats）
vi.mock('../lib/audit', () => ({ readAuditLog: vi.fn().mockResolvedValue('') }))

import { getDashboardStats } from './system'
import type { Share, ServiceStatus } from '../types'

function makeShare(name: string, protocol: Share['protocol'], users = 0): Share {
  return {
    name, protocol, path: `C:\\${name}`, description: '', type: 'Disk',
    hidden: false, encrypted: false, concurrentUsers: users, status: 'Enabled', cached: false
  }
}

describe('getDashboardStats - 并行化与容错', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('全部成功 → 正确聚合统计', async () => {
    const shares: Share[] = [
      makeShare('s1', 'smb', 5),
      makeShare('s2', 'smb', 2),
      makeShare('n1', 'nfs', 1),
      makeShare('f1', 'ftp'),
      makeShare('w1', 'webdav', 3)
    ]
    mockedAdapterList.mockResolvedValue(shares)
    mockedRunPowerShell.mockImplementation((cmd: string) => {
      if (cmd.includes('Get-SmbSession')) return Promise.resolve([{ id: 1 }, { id: 2 }, { id: 3 }])
      if (cmd.includes('Get-SmbOpenFile')) return Promise.resolve([{ id: 1 }, { id: 2 }])
      return Promise.resolve([])
    })
    mockedAdapterSessions.mockResolvedValue([{ sessionId: 'n1' }, { sessionId: 'n2' }])
    mockedGetServiceStatus.mockResolvedValue({ name: 'LanmanServer', status: 'Running', startType: 'Automatic' })

    const stats = await getDashboardStats()

    expect(stats.shareCount).toBe(5)
    expect(stats.activeSessions).toBe(5) // 3 SMB + 2 NFS
    expect(stats.openFiles).toBe(2)
    expect(stats.serviceStatus).toBe('Running')
    expect(stats.byProtocol.smb).toEqual({ shares: 2, sessions: 3 })
    expect(stats.byProtocol.nfs).toEqual({ shares: 1, sessions: 2 })
    expect(stats.byProtocol.ftp).toEqual({ shares: 1, sessions: 0 })
    expect(stats.byProtocol.webdav).toEqual({ shares: 1, sessions: 0 })
    // topShares 按 connections 降序，取前 8
    expect(stats.topShares[0]).toEqual({ name: 's1', connections: 5, protocol: 'smb' })
    expect(stats.topShares[1]).toEqual({ name: 'w1', connections: 3, protocol: 'webdav' })
  })

  it('并行执行验证：5 个调用在同一 tick 启动', async () => {
    const callTimes: number[] = []
    mockedAdapterList.mockImplementation(() => {
      callTimes.push(Date.now())
      return Promise.resolve([])
    })
    mockedRunPowerShell.mockImplementation(() => {
      callTimes.push(Date.now())
      return Promise.resolve([])
    })
    mockedAdapterSessions.mockImplementation(() => {
      callTimes.push(Date.now())
      return Promise.resolve([])
    })
    mockedGetServiceStatus.mockImplementation(() => {
      callTimes.push(Date.now())
      return Promise.resolve({ name: '', status: 'Running', startType: '' })
    })

    await getDashboardStats()

    // 5 个调用应几乎同时启动（adapterList + Get-SmbSession + Get-SmbOpenFile + adapterSessions + getServiceStatus）
    expect(callTimes).toHaveLength(5)
    const maxDiff = Math.max(...callTimes) - Math.min(...callTimes)
    expect(maxDiff).toBeLessThan(10)
  })

  it('adapterList 失败 → 其余统计正常返回（容错）', async () => {
    mockedAdapterList.mockRejectedValue(new Error('全部协议失败'))
    mockedRunPowerShell.mockResolvedValue([{ id: 1 }])
    mockedAdapterSessions.mockResolvedValue([])
    mockedGetServiceStatus.mockResolvedValue({ name: 'LanmanServer', status: 'Running', startType: 'Automatic' })

    const stats = await getDashboardStats()

    // adapterList 失败 → shares 为空
    expect(stats.shareCount).toBe(0)
    // 其他数据正常
    expect(stats.activeSessions).toBe(1) // SMB session
    expect(stats.serviceStatus).toBe('Running')
  })

  it('Get-SmbSession 失败 → sessions=0，openFiles 不受影响', async () => {
    mockedAdapterList.mockResolvedValue([makeShare('s1', 'smb')])
    mockedRunPowerShell.mockImplementation((cmd: string) => {
      if (cmd.includes('Get-SmbSession')) return Promise.reject(new Error('SMB 未装'))
      if (cmd.includes('Get-SmbOpenFile')) return Promise.resolve([{ id: 1 }, { id: 2 }, { id: 3 }])
      return Promise.resolve([])
    })
    mockedAdapterSessions.mockResolvedValue([])
    mockedGetServiceStatus.mockResolvedValue({ name: 'LanmanServer', status: 'Stopped', startType: 'Manual' })

    const stats = await getDashboardStats()

    expect(stats.activeSessions).toBe(0) // SMB 会话查询失败
    expect(stats.openFiles).toBe(3) // 打开文件不受影响
    expect(stats.byProtocol.smb.sessions).toBe(0)
  })

  it('getServiceStatus 失败 → serviceStatus 默认 Unknown', async () => {
    mockedAdapterList.mockResolvedValue([])
    mockedRunPowerShell.mockResolvedValue([])
    mockedAdapterSessions.mockResolvedValue([])
    mockedGetServiceStatus.mockRejectedValue(new Error('服务查询失败'))

    const stats = await getDashboardStats()

    expect(stats.serviceStatus).toBe('Unknown')
  })

  it('NFS 会话查询失败 → nfs.sessions=0，SMB 会话不受影响', async () => {
    mockedAdapterList.mockResolvedValue([makeShare('n1', 'nfs')])
    mockedRunPowerShell.mockImplementation((cmd: string) => {
      if (cmd.includes('Get-SmbSession')) return Promise.resolve([{ id: 1 }])
      return Promise.resolve([])
    })
    mockedAdapterSessions.mockRejectedValue(new Error('NFS 未装'))
    mockedGetServiceStatus.mockResolvedValue({ name: 'LanmanServer', status: 'Running', startType: 'Automatic' })

    const stats = await getDashboardStats()

    expect(stats.byProtocol.nfs.sessions).toBe(0)
    expect(stats.byProtocol.smb.sessions).toBe(1)
    expect(stats.activeSessions).toBe(1) // 仅 SMB
  })

  it('全部失败 → 所有字段安全降级，不抛错', async () => {
    mockedAdapterList.mockRejectedValue(new Error('fail'))
    mockedRunPowerShell.mockRejectedValue(new Error('fail'))
    mockedAdapterSessions.mockRejectedValue(new Error('fail'))
    mockedGetServiceStatus.mockRejectedValue(new Error('fail'))

    const stats = await getDashboardStats()

    expect(stats.shareCount).toBe(0)
    expect(stats.activeSessions).toBe(0)
    expect(stats.openFiles).toBe(0)
    expect(stats.serviceStatus).toBe('Unknown')
    expect(stats.topShares).toEqual([])
    expect(stats.byProtocol).toEqual({
      smb: { shares: 0, sessions: 0 },
      nfs: { shares: 0, sessions: 0 },
      ftp: { shares: 0, sessions: 0 },
      webdav: { shares: 0, sessions: 0 }
    })
  })

  it('Special/IPC 类型共享不计入 shareCount', async () => {
    mockedAdapterList.mockResolvedValue([
      makeShare('normal', 'smb'),
      { ...makeShare('ADMIN$', 'smb'), type: 'Special' },
      { ...makeShare('IPC$', 'smb'), type: 'IPC' }
    ])
    mockedRunPowerShell.mockResolvedValue([])
    mockedAdapterSessions.mockResolvedValue([])
    mockedGetServiceStatus.mockResolvedValue({ name: '', status: 'Running', startType: '' })

    const stats = await getDashboardStats()

    expect(stats.shareCount).toBe(1) // 仅 normal
    expect(stats.topShares).toHaveLength(1)
  })
})
