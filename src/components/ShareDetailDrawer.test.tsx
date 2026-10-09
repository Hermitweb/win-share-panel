import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import type { Share, ShareConnections, ShareOpenFile } from '../types'

// api.ts 在模块加载时读取 window.winshare —— 必须在 import 组件前注入 stub
const stub = vi.hoisted(() => {
  const s = {
    share: { connections: vi.fn(), openFiles: vi.fn(), closeOpenFiles: vi.fn(), update: vi.fn() },
    adapter: { update: vi.fn() },
    system: { currentUser: vi.fn(), selectFolder: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import ShareDetailDrawer from './ShareDetailDrawer'

vi.setConfig({ testTimeout: 30_000 })
const settle = () => new Promise((r) => setTimeout(r, 200))
const bodyText = () => document.body.textContent || ''

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
  vi.clearAllMocks()
})

const mkShare = (name: string, over: Partial<Share> = {}): Share => ({
  name,
  path: `D:\\Shares\\${name}`,
  description: '',
  protocol: 'smb',
  type: 'Disk',
  hidden: false,
  encrypted: false,
  concurrentUsers: 0,
  status: 'Enabled',
  cached: false,
  ...over,
})

const conns = (user: string): ShareConnections => ({
  concurrentUsers: 1,
  clientConnections: [{ clientUserName: user, clientComputerName: `PC-${user}`, openFiles: 1 }],
})

const files = (path: string): ShareOpenFile[] => [
  {
    fileId: 1,
    path,
    clientUserName: 'alice',
    clientComputerName: 'PC-alice',
    lockCount: 0,
  } as unknown as ShareOpenFile,
]

const activeTab = () => document.querySelector('.ant-tabs-tab-active')?.textContent ?? ''
const tableText = () =>
  Array.from(document.querySelectorAll('.ant-table-tbody tr[data-row-key]'))
    .map((r) => (r.textContent || '').trim())
    .join(' | ')

function wrap(open: boolean, share: Share | null) {
  return (
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <ShareDetailDrawer open={open} share={share} onClose={vi.fn()} onSuccess={vi.fn()} />
      </AntdApp>
    </ConfigProvider>
  )
}

beforeEach(() => {
  stub.share.connections.mockImplementation((name: string) =>
    Promise.resolve(conns(name === 'Media' ? 'bob' : 'alice')),
  )
  stub.share.openFiles.mockImplementation((name: string) =>
    Promise.resolve(
      files(name === 'Media' ? 'D:\\Shares\\Media\\b.txt' : 'D:\\Shares\\Docs\\a.txt'),
    ),
  )
  stub.share.closeOpenFiles.mockResolvedValue({ closed: 1, failed: 0 })
  stub.share.update.mockResolvedValue(undefined)
  stub.adapter.update.mockResolvedValue(undefined)
  stub.system.currentUser.mockResolvedValue({
    username: 'admin',
    isAdmin: true,
    computerName: 'SRV01',
  })
  stub.system.selectFolder.mockResolvedValue(null)
})

describe('ShareDetailDrawer（共享详情）', () => {
  it('SMB 打开：connections / openFiles 各恰好 1 次；重复渲染不重发', async () => {
    const share = mkShare('Docs')
    const { rerender } = render(wrap(true, share))
    await waitFor(() => expect(stub.share.connections).toHaveBeenCalledWith('Docs'))
    expect(stub.share.openFiles).toHaveBeenCalledWith('Docs')
    expect(stub.share.connections).toHaveBeenCalledTimes(1)
    expect(stub.share.openFiles).toHaveBeenCalledTimes(1)

    rerender(wrap(true, share))
    await settle()
    expect(stub.share.connections).toHaveBeenCalledTimes(1)
    expect(stub.share.openFiles).toHaveBeenCalledTimes(1)
  })

  it('关闭再打开：tab 回到「基本信息」，连接/打开文件再各 1 次', async () => {
    const share = mkShare('Docs')
    const { rerender } = render(wrap(true, share))
    await waitFor(() => expect(stub.share.connections).toHaveBeenCalledTimes(1))

    // 切到「连接」Tab 并确认数据落地
    fireEvent.click(screen.getByRole('tab', { name: /连\s*接/ }))
    await waitFor(() => expect(tableText()).toContain('alice'))
    expect(activeTab()).toContain('连接')

    rerender(wrap(false, share))
    await settle()
    rerender(wrap(true, share))

    await waitFor(() => expect(stub.share.connections).toHaveBeenCalledTimes(2))
    expect(stub.share.openFiles).toHaveBeenCalledTimes(2)
    // tab 回到基本信息（可见的选中项，不是内部 state）
    await waitFor(() => expect(activeTab()).toContain('基本信息'))
  })

  it('切换 share：重载并重置 tab，新共享数据最终胜出（旧响应迟到也被丢弃）', async () => {
    let resolveSlow: (v: ShareConnections) => void = () => {}
    stub.share.connections.mockImplementation((name: string) => {
      if (name === 'Slow') return new Promise<ShareConnections>((res) => (resolveSlow = res))
      return Promise.resolve(conns('bob'))
    })

    const { rerender } = render(wrap(true, mkShare('Slow')))
    await waitFor(() => expect(stub.share.connections).toHaveBeenCalledWith('Slow'))
    fireEvent.click(screen.getByRole('tab', { name: /连\s*接/ }))

    rerender(wrap(true, mkShare('Media')))
    await waitFor(() => expect(stub.share.connections).toHaveBeenCalledWith('Media'))
    await waitFor(() => expect(activeTab()).toContain('基本信息'))

    // 迟到的 Slow 响应不得覆盖新共享
    resolveSlow(conns('alice'))
    await settle()
    fireEvent.click(screen.getByRole('tab', { name: /连\s*接/ }))
    await waitFor(() => expect(tableText()).toContain('bob'))
    expect(tableText()).not.toContain('alice')
  })

  it('非 SMB 共享：不拉连接/打开文件，只按协议预填站点配置', async () => {
    const nfs = mkShare('NfsShare', { protocol: 'nfs', path: 'D:\\nfs', nfsPermission: 'rw' })
    render(wrap(true, nfs))
    await settle()

    expect(stub.share.connections).not.toHaveBeenCalled()
    expect(stub.share.openFiles).not.toHaveBeenCalled()
    // SMB 专属 Tab 不出现
    expect(screen.queryByRole('tab', { name: /连\s*接/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('tab', { name: /打开文件/ })).not.toBeInTheDocument()
    // 协议预填：NFS 的「共享权限」按 share.nfsPermission 落到表单
    fireEvent.click(screen.getByRole('tab', { name: '站点配置' }))
    const permSelect = (await screen.findByText('共享权限')).closest(
      '.ant-form-item',
    ) as HTMLElement
    await waitFor(() =>
      expect(permSelect.querySelector('.ant-select-content')?.getAttribute('title')).toBe(
        '读写 (rw)',
      ),
    )
  })

  it('连接加载失败：原因可见；打开文件加载失败：静默（不弹错、列表为空）', async () => {
    stub.share.connections.mockRejectedValue(new Error('LanmanServer 查询失败'))
    stub.share.openFiles.mockRejectedValue(new Error('枚举打开文件失败'))
    render(wrap(true, mkShare('Docs')))

    await waitFor(() => expect(bodyText()).toContain('LanmanServer 查询失败'))
    // 打开文件的失败静默：不进 message
    expect(bodyText()).not.toContain('枚举打开文件失败')
    fireEvent.click(screen.getByRole('tab', { name: /打开文件/ }))
    expect(await screen.findByText('暂无打开文件')).toBeInTheDocument()
  })
})
