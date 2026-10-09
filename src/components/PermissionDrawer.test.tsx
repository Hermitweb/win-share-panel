import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import type { LocalGroup, LocalUser, NtfsAcl, Share, SharePermission } from '../types'

// api.ts 在模块加载时读取 window.winshare —— 必须在 import 组件前注入 stub
const stub = vi.hoisted(() => {
  const s = {
    share: { permissions: vi.fn() },
    user: {
      list: vi.fn(),
      groups: vi.fn(),
      setSharePermissions: vi.fn(),
      ntfsPermissions: vi.fn(),
    },
    adapter: { permissions: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import PermissionDrawer from './PermissionDrawer'

vi.setConfig({ testTimeout: 30_000 })
const settle = () => new Promise((r) => setTimeout(r, 200))
const bodyText = () => document.body.textContent || ''

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
  vi.clearAllMocks()
})

const smb = (name: string, over: Partial<Share> = {}): Share => ({
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

const perm = (
  shareName: string,
  account: string,
  access: SharePermission['access'],
): SharePermission => ({
  shareName,
  account,
  accountType: 'User',
  access,
  deny: false,
})

const ROWS_A = [perm('Docs', 'alice', 'Read')]
const ROWS_B = [perm('Media', 'bob', 'Change')]

const rowAccounts = () =>
  Array.from(document.querySelectorAll('.ant-table-tbody tr[data-row-key]')).map((r) =>
    (r.textContent || '').trim(),
  )

/** antd Popconfirm「保存」的二次确认按钮（默认 okText 走 zhCN 的「确定」） */
async function confirmPopconfirm(trigger: HTMLElement) {
  fireEvent.click(trigger)
  const ok = await waitFor(() => {
    const btn = Array.from(document.querySelectorAll('.ant-popconfirm button')).find((b) =>
      b.classList.contains('ant-btn-primary'),
    )
    if (!btn) throw new Error('确认按钮未出现')
    return btn as HTMLElement
  })
  fireEvent.click(ok)
}

function wrap(open: boolean, share: Share | null) {
  return (
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <PermissionDrawer open={open} share={share} onClose={vi.fn()} />
      </AntdApp>
    </ConfigProvider>
  )
}

function renderDrawer(open: boolean, share: Share | null) {
  const utils = render(wrap(open, share))
  return utils
}

beforeEach(() => {
  stub.share.permissions.mockImplementation((name: string) =>
    Promise.resolve(name === 'Media' ? ROWS_B : ROWS_A),
  )
  stub.user.list.mockResolvedValue([{ name: 'alice' }] as unknown as LocalUser[])
  stub.user.groups.mockResolvedValue([{ name: 'Ops' }] as unknown as LocalGroup[])
  stub.user.setSharePermissions.mockResolvedValue(undefined)
  stub.user.ntfsPermissions.mockResolvedValue({
    path: 'D:\\Shares\\Docs',
    entries: [{ account: 'Everyone', rights: 'Read', type: 'Allow', inherited: false }],
  } as unknown as NtfsAcl)
  stub.adapter.permissions.mockResolvedValue([])
})

describe('PermissionDrawer（共享权限 / NTFS）', () => {
  it('F1 回归：「重新加载」只刷新权限表格，不清空已加载的只读 NTFS 视图；切换共享才回未加载初始态', async () => {
    // NTFS 页被激活后其表格会一并留在 DOM 里，故本用例只数「共享权限」页那张表（DOM 顺序第一张）
    const permRows = () =>
      Array.from(
        (document.querySelectorAll('.ant-table-tbody')[0] as HTMLElement).querySelectorAll(
          'tr[data-row-key]',
        ),
      ).map((r) => (r.textContent || '').trim())

    const { rerender } = renderDrawer(true, smb('Docs'))
    await waitFor(() => expect(permRows()).toHaveLength(1))

    // 先手动加载一次 NTFS ACL（只读视图）
    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    fireEvent.click(await screen.findByRole('button', { name: /加\s*载/ }))
    await waitFor(() => expect(screen.getByText('Everyone')).toBeInTheDocument())
    expect(stub.user.ntfsPermissions).toHaveBeenCalledTimes(1)

    // 回到「共享权限」点「重新加载」：只重取权限行
    fireEvent.click(screen.getByRole('tab', { name: '共享权限' }))
    fireEvent.click(screen.getByRole('button', { name: /重新加载/ }))
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(2))
    // 重读不碰 NTFS：不再发 ntfsPermissions，也不回到「未加载」提示
    expect(stub.user.ntfsPermissions).toHaveBeenCalledTimes(1)
    expect(screen.queryByText('点击「加载」查看 NTFS ACL（只读）')).not.toBeInTheDocument()

    // 切回 NTFS 页：已加载的可见内容仍在（断言可见内容，不断言内部 state）
    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    expect(await screen.findByText('Everyone')).toBeInTheDocument()
    expect(screen.queryByText('点击「加载」查看 NTFS ACL（只读）')).not.toBeInTheDocument()

    // 保存成功后的重读同样不清空 NTFS 视图（同一 nonce 路径）
    fireEvent.click(screen.getByRole('tab', { name: '共享权限' }))
    fireEvent.change(screen.getByPlaceholderText('账号名'), { target: { value: 'carol' } })
    fireEvent.click(screen.getByRole('button', { name: /添\s*加/ }))
    await waitFor(() => expect(permRows()).toHaveLength(2))
    await confirmPopconfirm(screen.getByRole('button', { name: /保\s*存/ }))
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(3))
    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    expect(await screen.findByText('Everyone')).toBeInTheDocument()
    expect(screen.queryByText('点击「加载」查看 NTFS ACL（只读）')).not.toBeInTheDocument()

    // 既有语义不变：切换共享 → NTFS 回到未加载初始态（文案逐字不变）
    rerender(wrap(true, smb('Media')))
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(4))
    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    expect(await screen.findByText('点击「加载」查看 NTFS ACL（只读）')).toBeInTheDocument()
    expect(screen.queryByText('Everyone')).not.toBeInTheDocument()
  })

  it('SMB 共享打开：permissions / user.list / user.groups 各恰好 1 次，重复渲染不重发', async () => {
    const share = smb('Docs')
    const { rerender } = renderDrawer(true, share)
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledWith('Docs'))
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(1))
    expect(stub.user.groups).toHaveBeenCalledTimes(1)

    // 同一 share 引用重复渲染（含 open 保持 true）不得重发
    rerender(wrap(true, share))
    await settle()
    expect(stub.share.permissions).toHaveBeenCalledTimes(1)
    expect(stub.user.list).toHaveBeenCalledTimes(1)
    expect(stub.user.groups).toHaveBeenCalledTimes(1)
    expect(rowAccounts()).toHaveLength(1)
  })

  it('关闭再打开：权限行重载（再各 1 次），NTFS 页回到「未加载」初始态', async () => {
    const { rerender } = renderDrawer(true, smb('Docs'))
    await waitFor(() => expect(rowAccounts()).toHaveLength(1))

    // NTFS 页：手动加载一次，页面上出现真实 ACL 行
    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    fireEvent.click(await screen.findByRole('button', { name: /加\s*载/ }))
    await waitFor(() => expect(screen.getByText('Everyone')).toBeInTheDocument())

    rerender(wrap(false, smb('Docs')))
    await settle()
    rerender(wrap(true, smb('Docs')))

    // 权限行重载：permissions 合计 2 次、user.list 合计 2 次
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(2))
    expect(stub.user.list).toHaveBeenCalledTimes(2)
    expect(stub.user.groups).toHaveBeenCalledTimes(2)
    await waitFor(() => expect(rowAccounts()).toHaveLength(1))

    // NTFS 回到「未加载」：提示可见、ACL 行不在
    fireEvent.click(await screen.findByRole('tab', { name: 'NTFS 权限' }))
    expect(await screen.findByText('点击「加载」查看 NTFS ACL（只读）')).toBeInTheDocument()
    expect(screen.queryByText('Everyone')).not.toBeInTheDocument()
  })

  it('切换 share：重载并以新共享数据为准（旧响应迟到也被丢弃）', async () => {
    let resolveSlow: (v: SharePermission[]) => void = () => {}
    stub.share.permissions.mockImplementation((name: string) => {
      if (name === 'Slow') return new Promise<SharePermission[]>((res) => (resolveSlow = res))
      return Promise.resolve(ROWS_B)
    })

    const { rerender } = renderDrawer(true, smb('Slow'))
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledWith('Slow'))

    rerender(wrap(true, smb('Media')))
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledWith('Media'))
    await waitFor(() => expect(rowAccounts()[0]).toContain('bob'))

    // 迟到的 Slow 响应不得覆盖新共享的行
    resolveSlow(ROWS_A)
    await settle()
    expect(rowAccounts()[0]).toContain('bob')
    expect(rowAccounts().join(' ')).not.toContain('alice')
  })

  it('加载失败：原因可见、表格保持空态、不假成功', async () => {
    stub.share.permissions.mockRejectedValue(new Error('共享权限读取被拒绝'))
    renderDrawer(true, smb('Docs'))

    await waitFor(() => expect(bodyText()).toContain('共享权限读取被拒绝'))
    expect(screen.getByText('暂无权限条目')).toBeInTheDocument()
    expect(rowAccounts()).toHaveLength(0)
    expect(stub.user.setSharePermissions).not.toHaveBeenCalled()
  })

  it('本地加行后保存成功：setSharePermissions 1 次 + 重读 1 次 + 成功文案', async () => {
    renderDrawer(true, smb('Docs'))
    await waitFor(() => expect(rowAccounts()).toHaveLength(1))

    fireEvent.change(screen.getByPlaceholderText('账号名'), { target: { value: 'carol' } })
    fireEvent.click(screen.getByRole('button', { name: /添\s*加/ }))
    await waitFor(() => expect(rowAccounts()).toHaveLength(2))

    await confirmPopconfirm(screen.getByRole('button', { name: /保\s*存/ }))

    await waitFor(() => expect(stub.user.setSharePermissions).toHaveBeenCalledTimes(1))
    const [name, perms] = stub.user.setSharePermissions.mock.calls[0] as [string, SharePermission[]]
    expect(name).toBe('Docs')
    expect(perms).toEqual([
      { shareName: 'Docs', account: 'alice', accountType: 'User', access: 'Read', deny: false },
      { shareName: 'Docs', account: 'carol', accountType: 'User', access: 'Read', deny: false },
    ])
    // 保存成功后重读一次（合计 2 次取数）
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(bodyText()).toContain('权限已保存'))
  })

  it('保存失败：原因可见、不重读、本地编辑行仍在', async () => {
    stub.user.setSharePermissions.mockRejectedValue(new Error('权限写入失败：拒绝访问'))
    renderDrawer(true, smb('Docs'))
    await waitFor(() => expect(rowAccounts()).toHaveLength(1))

    fireEvent.change(screen.getByPlaceholderText('账号名'), { target: { value: 'carol' } })
    fireEvent.click(screen.getByRole('button', { name: /添\s*加/ }))
    await waitFor(() => expect(rowAccounts()).toHaveLength(2))

    await confirmPopconfirm(screen.getByRole('button', { name: /保\s*存/ }))

    await waitFor(() => expect(bodyText()).toContain('权限写入失败：拒绝访问'))
    expect(bodyText()).not.toContain('权限已保存')
    // 不重读（仍是最初那 1 次取数）
    expect(stub.share.permissions).toHaveBeenCalledTimes(1)
    // 本地编辑不丢
    expect(rowAccounts()).toHaveLength(2)
    expect(rowAccounts()[1]).toContain('carol')
  })

  it('非 SMB 共享：走对应协议面板，不走遗留共享权限通道', async () => {
    const nfs = smb('NfsShare', { protocol: 'nfs', path: 'D:\\nfs' })
    renderDrawer(true, nfs)

    // 协议面板挂载后走 adapter.permissions('nfs', name)，而不是 share.permissions
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledWith('nfs', 'NfsShare'))
    expect(stub.share.permissions).not.toHaveBeenCalled()
    expect(stub.user.setSharePermissions).not.toHaveBeenCalled()
    // 遗留的「共享权限 / NTFS 权限」Tabs 不出现
    expect(screen.queryByRole('tab', { name: 'NTFS 权限' })).not.toBeInTheDocument()
    // 面板自身的空态文案可见
    expect(await screen.findByText('暂无客户端规则')).toBeInTheDocument()
  })
})
