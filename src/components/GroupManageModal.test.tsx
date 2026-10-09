import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor, within } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import type { LocalGroup, LocalUser } from '../types'

// api.ts 在模块加载时读取 window.winshare —— 必须在 import 组件前注入 stub
const stub = vi.hoisted(() => {
  const s = {
    user: { list: vi.fn(), groups: vi.fn() },
    group: { update: vi.fn(), rename: vi.fn(), addMember: vi.fn(), removeMember: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import GroupManageModal from './GroupManageModal'

// 本机并发跑多套组件测试时 jsdom 会明显变慢：放宽异步等待预算
vi.setConfig({ testTimeout: 30_000 })
const settle = () => new Promise((r) => setTimeout(r, 200))
const bodyText = () => document.body.textContent || ''

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
  vi.clearAllMocks()
})

const member = (name: string) => ({
  name,
  objectClass: 'User' as const,
  principalSource: 'Local',
})

const G1: LocalGroup = {
  name: 'Sales',
  description: '销售组',
  members: [member('alice')],
}
const G2: LocalGroup = {
  name: 'Ops',
  description: '运维组',
  members: [member('carol')],
}

const USERS = [
  { name: 'alice', fullName: 'Alice A' },
  { name: 'bob', fullName: '' },
] as unknown as LocalUser[]

const descInput = () => screen.getByPlaceholderText('组用途描述') as unknown as HTMLTextAreaElement
const newMemberInput = () =>
  screen.getByPlaceholderText('手动输入用户名或组名后回车添加') as unknown as HTMLInputElement
const tbody = () => document.querySelector('.ant-table-tbody') as HTMLElement

/** 批量添加多选框里当前已选中的标签（antd 把 label 写进 title） */
function selectedTags(): string[] {
  return Array.from(document.querySelectorAll('.ant-select-selection-item')).map(
    (el) => el.getAttribute('title') ?? el.textContent ?? '',
  )
}

async function pickBatchMember(label: string) {
  fireEvent.mouseDown(screen.getByRole('combobox'))
  const option = await waitFor(() => {
    const el = Array.from(document.querySelectorAll('.ant-select-item-option-content')).find((n) =>
      (n.textContent || '').includes(label),
    )
    if (!el) throw new Error(`下拉项未出现：${label}`)
    return el as HTMLElement
  })
  fireEvent.click(option)
}

/** 与各用例 rerender 时保持完全相同的包裹层级，避免 ConfigProvider 增删导致整棵子树重挂 */
function wrap(open: boolean, group: LocalGroup | null) {
  return (
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <GroupManageModal open={open} group={group} onClose={vi.fn()} onSuccess={vi.fn()} />
      </AntdApp>
    </ConfigProvider>
  )
}

function renderModal(open: boolean, group: LocalGroup | null) {
  const onClose = vi.fn()
  const onSuccess = vi.fn()
  const utils = render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <GroupManageModal open={open} group={group} onClose={onClose} onSuccess={onSuccess} />
      </AntdApp>
    </ConfigProvider>,
  )
  return { ...utils, onClose, onSuccess }
}

beforeEach(() => {
  stub.user.list.mockResolvedValue(USERS)
  stub.user.groups.mockResolvedValue([
    { name: 'Sales', description: '销售组', members: [member('alice'), member('bob')] },
  ])
  stub.group.update.mockResolvedValue(undefined)
  stub.group.rename.mockResolvedValue(undefined)
  stub.group.addMember.mockResolvedValue(undefined)
  stub.group.removeMember.mockResolvedValue(undefined)
})

describe('GroupManageModal（管理组）', () => {
  it('打开→恰好 1 次 api.user.list；重复渲染不重发（静置 200ms）', async () => {
    const { rerender } = renderModal(true, G1)
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(1))

    // 同 props 重复渲染（open 保持 true）不得重发
    rerender(wrap(true, G1))
    await settle()
    expect(stub.user.list).toHaveBeenCalledTimes(1)

    // 批量添加区拿到真实用户（取数落到了可见选项里）：alice 已是成员，可选的是 bob
    await pickBatchMember('bob')
    expect(selectedTags()).toEqual(['bob'])
  })

  it('关闭再打开：成员/新增输入/重命名态/批量选中项全部回到初始（描述仍来自 group）', async () => {
    const { rerender } = renderModal(true, G1)
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(1))
    expect(descInput().value).toBe('销售组')
    expect(within(tbody()).getByText('alice')).toBeInTheDocument()

    // 勾批量选中、填新增成员、进入重命名态
    await pickBatchMember('bob')
    expect(selectedTags()).toEqual(['bob'])
    fireEvent.change(newMemberInput(), { target: { value: 'dave' } })
    fireEvent.click(document.querySelector('.anticon-edit')!.closest('button') as HTMLButtonElement)
    await waitFor(() => expect(screen.getByPlaceholderText('输入新组名')).toBeInTheDocument())

    // 刷新成员：stub 里 Sales 多了一个 bob → 证明列表真的会被外部数据改写
    fireEvent.click(screen.getByRole('button', { name: /刷\s*新/ }))
    await waitFor(() => expect(within(tbody()).getByText('bob')).toBeInTheDocument())

    // 关闭
    rerender(wrap(false, G1))
    await settle()
    // 重开
    rerender(wrap(true, G1))

    // 成员列表回到 group.members（刷新带进来的 bob 不残留）
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(within(tbody()).queryByText('bob')).not.toBeInTheDocument())
    expect(within(tbody()).getByText('alice')).toBeInTheDocument()
    // 新增成员输入、批量选中项归零，重命名态退出
    expect(newMemberInput().value).toBe('')
    expect(selectedTags()).toEqual([])
    expect(screen.queryByPlaceholderText('输入新组名')).not.toBeInTheDocument()
    // 描述仍来自 group
    expect(descInput().value).toBe('销售组')
  })

  it('切换 group：成员与描述按新组重填，且 api.user.list 再 1 次', async () => {
    const { rerender } = renderModal(true, G1)
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(1))
    expect(descInput().value).toBe('销售组')

    stub.user.groups.mockResolvedValue([{ ...G2, members: [member('carol')] }])
    rerender(wrap(true, G2))
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(descInput().value).toBe('运维组'))
    expect(within(tbody()).getByText('carol')).toBeInTheDocument()
    expect(within(tbody()).queryByText('alice')).not.toBeInTheDocument()
    // 标题跟着新组
    expect(bodyText()).toContain('管理组：Ops')
  })

  it('保存描述失败：原因可见、不关窗、不假成功', async () => {
    stub.group.update.mockRejectedValue(new Error('拒绝访问注册表'))
    const { onClose, onSuccess } = renderModal(true, G1)
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(1))

    fireEvent.click(screen.getByRole('button', { name: /保存描述/ }))
    await waitFor(() => expect(bodyText()).toContain('拒绝访问注册表'))
    expect(bodyText()).not.toContain('描述已更新')
    expect(onClose).not.toHaveBeenCalled()
    expect(onSuccess).not.toHaveBeenCalled()
    // 窗口没关：成员管理区仍在
    expect(screen.getByText('成员管理')).toBeInTheDocument()
  })

  it('用户列表拉取失败：静默（不弹错），批量添加区为空但主功能可用', async () => {
    stub.user.list.mockRejectedValue(new Error('PowerShell 通道不可用'))
    renderModal(true, G1)
    await settle()
    expect(bodyText()).not.toContain('PowerShell 通道不可用')
    // 成员表仍在（主功能可用），批量添加无可选项
    expect(within(tbody()).getByText('alice')).toBeInTheDocument()
    expect(selectedTags()).toEqual([])
  })
})
