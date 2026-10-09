import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import type { LocalUser } from '../types'

// api.ts 在模块加载时读取 window.winshare —— 必须在 import 组件前注入 stub
const stub = vi.hoisted(() => {
  const s = {
    user: {
      groups: vi.fn(),
      sharePermissionsForUser: vi.fn(),
      update: vi.fn(),
      rename: vi.fn(),
      setPassword: vi.fn(),
    },
    group: { addMember: vi.fn(), removeMember: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import UserDetailDrawer from './UserDetailDrawer'

vi.setConfig({ testTimeout: 30_000 })
const settle = () => new Promise((r) => setTimeout(r, 200))
const bodyText = () => document.body.textContent || ''

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
  vi.clearAllMocks()
})

const mkUser = (name: string, over: Partial<LocalUser> = {}): LocalUser => ({
  name,
  fullName: `${name} 全名`,
  enabled: true,
  description: '',
  groups: [],
  passwordRequired: true,
  passwordChangeable: true,
  passwordExpires: false,
  userMayChangePassword: true,
  passwordLastSet: '2026-01-01T00:00:00Z',
  lastLogon: '2026-02-01T00:00:00Z',
  sid: 'S-1-5-21-1',
  principalSource: 'Local',
  ...over,
})

const activeTab = () => document.querySelector('.ant-tabs-tab-active')?.textContent ?? ''
/** 按 Form.Item 的 label 定位「属性」表单里的输入框（antd v6 的 label 与控件同容器） */
const propInput = (label: string): HTMLInputElement => {
  const item = Array.from(document.querySelectorAll('.ant-form-item')).find((it) =>
    (it.querySelector('label')?.textContent || '').includes(label),
  )
  const input = item?.querySelector('input, textarea')
  if (!input) throw new Error(`未找到表单项：${label}`)
  return input as HTMLInputElement
}
/** 「属性」Tab 的第 4 个开关＝重设密码区开关（前三个是三个默认勾选） */
const pwdAreaSwitch = () => screen.getAllByRole('switch')[3] as HTMLButtonElement

function wrap(open: boolean, user: LocalUser | null) {
  return (
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <UserDetailDrawer open={open} user={user} onClose={vi.fn()} onSuccess={vi.fn()} />
      </AntdApp>
    </ConfigProvider>
  )
}

function renderDrawer(open: boolean, user: LocalUser | null) {
  const onClose = vi.fn()
  const onSuccess = vi.fn()
  const utils = render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <UserDetailDrawer open={open} user={user} onClose={onClose} onSuccess={onSuccess} />
      </AntdApp>
    </ConfigProvider>,
  )
  return { ...utils, onClose, onSuccess }
}

beforeEach(() => {
  stub.user.groups.mockResolvedValue([{ name: 'Ops', description: '运维', members: [] }])
  stub.user.sharePermissionsForUser.mockResolvedValue([
    { shareName: 'Docs', account: 'alice', accountType: 'User', access: 'Read', deny: false },
  ])
  stub.user.update.mockResolvedValue(undefined)
  stub.user.rename.mockResolvedValue(undefined)
  stub.user.setPassword.mockResolvedValue(undefined)
  stub.group.addMember.mockResolvedValue(undefined)
  stub.group.removeMember.mockResolvedValue(undefined)
})

describe('UserDetailDrawer（用户详情）', () => {
  it('关闭再打开：tab 回到「属性」、密码区收起并清空、未保存编辑不残留', async () => {
    const user = mkUser('alice')
    const { rerender } = renderDrawer(true, user)
    await waitFor(() => expect(activeTab()).toContain('属性'))

    // 制造非初始态：切到「共享权限」、展开密码区并生成密码、改全名
    fireEvent.click(screen.getByRole('tab', { name: /共享权限/ }))
    await waitFor(() => expect(activeTab()).toContain('共享权限'))
    fireEvent.click(screen.getByRole('tab', { name: /属性/ }))
    fireEvent.click(pwdAreaSwitch())
    fireEvent.click(await screen.findByRole('button', { name: /生\s*成/ }))
    await waitFor(() =>
      expect(screen.getByPlaceholderText('输入新密码或点击生成')).toBeInTheDocument(),
    )
    fireEvent.change(propInput('全名'), { target: { value: '改过的全名' } })
    await waitFor(() => expect(propInput('全名').value).toBe('改过的全名'))

    rerender(wrap(false, user))
    await settle()
    rerender(wrap(true, user))

    // tab 回到「属性」、密码区收起（输入框不再渲染）
    await waitFor(() => expect(activeTab()).toContain('属性'))
    await waitFor(() =>
      expect(screen.queryByPlaceholderText('输入新密码或点击生成')).not.toBeInTheDocument(),
    )
    expect(pwdAreaSwitch()).not.toBeChecked()
    // 未保存编辑不残留：表单按 user 重填
    await waitFor(() => expect(propInput('全名').value).toBe('alice 全名'))
  })

  it('切换 user：表单按新用户重填，tab 回到「属性」', async () => {
    const { rerender } = renderDrawer(true, mkUser('alice'))
    await waitFor(() => expect(activeTab()).toContain('属性'))
    fireEvent.click(screen.getByRole('tab', { name: /共享权限/ }))
    await waitFor(() => expect(activeTab()).toContain('共享权限'))

    const bob = mkUser('bob', { fullName: 'Bob 全名', description: '备份账号' })
    rerender(wrap(true, bob))

    await waitFor(() => expect(activeTab()).toContain('属性'))
    await waitFor(() => expect(propInput('用户名').value).toBe('bob'))
    expect(propInput('全名').value).toBe('Bob 全名')
    expect(propInput('描述').value).toBe('备份账号')
    expect(bodyText()).toContain('用户详情：bob')
  })

  it('保存失败：原因可见、不关窗、改动不丢', async () => {
    stub.user.update.mockRejectedValue(new Error('拒绝访问 SAM'))
    const { onClose, onSuccess } = renderDrawer(true, mkUser('alice'))
    await waitFor(() => expect(activeTab()).toContain('属性'))

    fireEvent.change(propInput('全名'), { target: { value: '新全名' } })
    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }))

    await waitFor(() => expect(bodyText()).toContain('拒绝访问 SAM'))
    expect(bodyText()).not.toContain('已保存')
    expect(onClose).not.toHaveBeenCalled()
    expect(onSuccess).not.toHaveBeenCalled()
    expect(propInput('全名').value).toBe('新全名')
  })

  it('Tab 切换触发的组/权限加载不受影响（事件路径，切换即取数）', async () => {
    renderDrawer(true, mkUser('alice'))
    await waitFor(() => expect(activeTab()).toContain('属性'))
    // 打开时不预拉组/权限
    expect(stub.user.groups).not.toHaveBeenCalled()
    expect(stub.user.sharePermissionsForUser).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('tab', { name: /所属组/ }))
    await waitFor(() => expect(stub.user.groups).toHaveBeenCalledTimes(1))
    expect(stub.user.groups).toHaveBeenCalledWith()

    // 已加载过就不重复拉（既有语义：groups.length===0 才拉）
    fireEvent.click(screen.getByRole('tab', { name: /属性/ }))
    fireEvent.click(screen.getByRole('tab', { name: /所属组/ }))
    await settle()
    expect(stub.user.groups).toHaveBeenCalledTimes(1)

    // 共享权限每次都按切换重新取
    fireEvent.click(screen.getByRole('tab', { name: /共享权限/ }))
    await waitFor(() => expect(stub.user.sharePermissionsForUser).toHaveBeenCalledWith('alice'))
    await waitFor(() => expect(screen.getByText('Docs')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: /属性/ }))
    fireEvent.click(screen.getByRole('tab', { name: /共享权限/ }))
    await waitFor(() => expect(stub.user.sharePermissionsForUser).toHaveBeenCalledTimes(2))
  })
})
