import { describe, it, expect, vi, beforeEach } from 'vitest'

// vi.hoisted 确保 mock 变量在 vi.mock 工厂函数（被提升到文件顶部）执行时可用
const { mockSmbListShares, mockNfsListShares, mockFtpListShares, mockWebdavListShares } = vi.hoisted(() => ({
  mockSmbListShares: vi.fn(),
  mockNfsListShares: vi.fn(),
  mockFtpListShares: vi.fn(),
  mockWebdavListShares: vi.fn()
}))

vi.mock('./adapters/smbAdapter', () => ({
  smbAdapter: { protocol: 'smb', listShares: mockSmbListShares }
}))
vi.mock('./adapters/nfsAdapter', () => ({
  nfsAdapter: { protocol: 'nfs', listShares: mockNfsListShares }
}))
vi.mock('./adapters/ftpAdapter', () => ({
  ftpAdapter: { protocol: 'ftp', listShares: mockFtpListShares }
}))
vi.mock('./adapters/webdavAdapter', () => ({
  webdavAdapter: { protocol: 'webdav', listShares: mockWebdavListShares }
}))

import { adapterList } from './registry'
import type { Share } from '../../types'

function makeShare(name: string, protocol: Share['protocol']): Share {
  return {
    name, protocol, path: `C:\\${name}`, description: '', type: 'Disk',
    hidden: false, encrypted: false, concurrentUsers: 0, status: 'Enabled', cached: false
  }
}

describe('adapterList - 并行化', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('四协议全部成功 → 合并结果，并行启动（非串行）', async () => {
    // 用 deferred 验证并行性：所有 listShares 在任一 resolve 前全部被调用
    const callOrder: string[] = []
    const barriers: Record<string, (v: Share[]) => void> = {}

    mockSmbListShares.mockReturnValue(new Promise<Share[]>((res) => {
      callOrder.push('smb-start')
      barriers.smb = res
    }))
    mockNfsListShares.mockReturnValue(new Promise<Share[]>((res) => {
      callOrder.push('nfs-start')
      barriers.nfs = res
    }))
    mockFtpListShares.mockReturnValue(new Promise<Share[]>((res) => {
      callOrder.push('ftp-start')
      barriers.ftp = res
    }))
    mockWebdavListShares.mockReturnValue(new Promise<Share[]>((res) => {
      callOrder.push('webdav-start')
      barriers.webdav = res
    }))

    const promise = adapterList()

    // 让微任务队列刷新，让所有 listShares 都被调用
    await vi.waitFor(() => {
      expect(callOrder).toHaveLength(4)
    })

    // 关键断言：4 个适配器全部启动后才开始 resolve（证明是并行的）
    expect(callOrder).toEqual(['smb-start', 'nfs-start', 'ftp-start', 'webdav-start'])

    // 逐个 resolve
    barriers.smb([makeShare('s1', 'smb')])
    barriers.nfs([makeShare('n1', 'nfs')])
    barriers.ftp([makeShare('f1', 'ftp')])
    barriers.webdav([makeShare('w1', 'webdav')])

    const result = await promise
    expect(result).toHaveLength(4)
    expect(result.map((s) => s.protocol).sort()).toEqual(['ftp', 'nfs', 'smb', 'webdav'])
  })

  it('一个协议失败，其他协议结果不受影响', async () => {
    mockSmbListShares.mockResolvedValue([makeShare('s1', 'smb')])
    mockNfsListShares.mockRejectedValue(new Error('NFS 未安装'))
    mockFtpListShares.mockResolvedValue([makeShare('f1', 'ftp')])
    mockWebdavListShares.mockResolvedValue([makeShare('w1', 'webdav')])

    const result = await adapterList()

    expect(result).toHaveLength(3)
    expect(result.map((s) => s.protocol).sort()).toEqual(['ftp', 'smb', 'webdav'])
    // NFS 失败不抛错，静默跳过
    expect(result.find((s) => s.protocol === 'nfs')).toBeUndefined()
  })

  it('全部协议失败 → 返回空数组，不抛错', async () => {
    mockSmbListShares.mockRejectedValue(new Error('SMB error'))
    mockNfsListShares.mockRejectedValue(new Error('NFS error'))
    mockFtpListShares.mockRejectedValue(new Error('FTP error'))
    mockWebdavListShares.mockRejectedValue(new Error('WebDAV error'))

    const result = await adapterList()
    expect(result).toEqual([])
  })

  it('指定协议 → 仅调用该协议适配器', async () => {
    mockSmbListShares.mockResolvedValue([makeShare('s1', 'smb')])

    const result = await adapterList('smb')

    expect(result).toHaveLength(1)
    expect(result[0].name).toBe('s1')
    // 其他适配器未被调用
    expect(mockNfsListShares).not.toHaveBeenCalled()
    expect(mockFtpListShares).not.toHaveBeenCalled()
    expect(mockWebdavListShares).not.toHaveBeenCalled()
  })

  it('指定协议但该协议抛错 → 返回空数组（不向上传播）', async () => {
    mockNfsListShares.mockRejectedValue(new Error('NFS 未安装'))

    const result = await adapterList('nfs')
    expect(result).toEqual([])
  })

  it('并行执行验证：全部在同一个微任务批次启动', async () => {
    // 使用时间戳验证：所有 listShares 调用应在同一 tick 内发生
    const timestamps: number[] = []
    mockSmbListShares.mockImplementation(() => {
      timestamps.push(Date.now())
      return Promise.resolve([makeShare('s1', 'smb')])
    })
    mockNfsListShares.mockImplementation(() => {
      timestamps.push(Date.now())
      return Promise.resolve([makeShare('n1', 'nfs')])
    })
    mockFtpListShares.mockImplementation(() => {
      timestamps.push(Date.now())
      return Promise.resolve([makeShare('f1', 'ftp')])
    })
    mockWebdavListShares.mockImplementation(() => {
      timestamps.push(Date.now())
      return Promise.resolve([makeShare('w1', 'webdav')])
    })

    await adapterList()

    // 所有 4 个调用应在几乎同一时刻发生（并行而非串行）
    expect(timestamps).toHaveLength(4)
    const maxDiff = Math.max(...timestamps) - Math.min(...timestamps)
    // 串行执行时每轮至少有 1 个 await tick 的间隔，并行时应 < 10ms
    expect(maxDiff).toBeLessThan(10)
  })
})
