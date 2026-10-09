import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import type { LocalGroup } from '../types'

// api.ts 在模块加载时读取 window.winshare —— 必须在 import 组件前注入 stub
const stub = vi.hoisted(() => {
  const s = {
    user: { groups: vi.fn(), create: vi.fn() },
    group: { addMember: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import UserCreateModal from './UserCreateModal'

vi.setConfig({ testTimeout: 30_000 })
const settle = () => new Promise((r) => setTimeout(r, 200))
const bodyText = () => document.body.textContent || ''

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
  vi.clearAllMocks()
})

const GROUPS = [{ name: 'Ops', description: '运维' }] as unknown as LocalGroup[]

const nameInput = () => screen.getByPlaceholderText('如 john.doe') as HTMLInputElement
const pwdInput = () => screen.getByPlaceholderText('输入密码或点击生成') as HTMLInputElement
const confirmInput = () => screen.getByPlaceholderText('再次输入密码') as HTMLInputElement
/** 三个默认勾选的开关：启用账号 / 允许用户修改密码 / 密码永不过期（DOM 顺序） */
const formSwitches = () => screen.getAllByRole('switch') as HTMLButtonElement[]
/** 分配到的组（antd multiple Select 的已选标签，label 写在 title 上） */
const selectedGroups = () =>
  Array.from(document.querySelectorAll('.ant-select-selection-item')).map(
    (el) => el.getAttribute('title') ?? el.textContent ?? '',
  )

function wrap(open: boolean) {
  return (
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <UserCreateModal open={open} onClose={vi.fn()} onSuccess={vi.fn()} />
      </AntdApp>
    </ConfigProvider>
  )
}

function renderModal(open: boolean) {
  const onClose = vi.fn()
  const onSuccess = vi.fn()
  const utils = render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <UserCreateModal open={open} onClose={onClose} onSuccess={onSuccess} />
      </AntdApp>
    </ConfigProvider>,
  )
  return { ...utils, onClose, onSuccess }
}

beforeEach(() => {
  stub.user.groups.mockResolvedValue(GROUPS)
  stub.user.create.mockResolvedValue(undefined)
  stub.group.addMember.mockResolvedValue(undefined)
})

describe('UserCreateModal（新建本地用户）', () => {
  it('打开：user.groups 恰好 1 次 + 表单重置为默认勾选；重复渲染不重发', async () => {
    const { rerender } = renderModal(true)
    await waitFor(() => expect(stub.user.groups).toHaveBeenCalledTimes(1))

    const sw = formSwitches()
    expect(sw).toHaveLength(3)
    sw.forEach((s) => expect(s).toBeChecked())
    expect(nameInput().value).toBe('')

    rerender(wrap(true))
    await settle()
    expect(stub.user.groups).toHaveBeenCalledTimes(1)
  })

  it('关闭再打开：已选组与密码强度提示清空，默认勾选回到初始', async () => {
    const { rerender } = renderModal(true)
    await waitFor(() => expect(stub.user.groups).toHaveBeenCalledTimes(1))

    // 填一个强密码 → 出现强度标签；勾一个组 → 出现标签；取消一个默认勾选
    fireEvent.change(pwdInput(), { target: { value: 'Str0ng!Passw0rd' } })
    await waitFor(() => expect(screen.getByText('强')).toBeInTheDocument())
    fireEvent.mouseDown(screen.getByRole('combobox'))
    const option = await waitFor(() => {
      const el = Array.from(document.querySelectorAll('.ant-select-item-option-content')).find(
        (n) => (n.textContent || '').includes('Ops'),
      )
      if (!el) throw new Error('组下拉项未出现')
      return el as HTMLElement
    })
    fireEvent.click(option)
    await waitFor(() => expect(selectedGroups()).toHaveLength(1))
    fireEvent.click(formSwitches()[0])
    await waitFor(() => expect(formSwitches()[0]).not.toBeChecked())

    rerender(wrap(false))
    await settle()
    rerender(wrap(true))

    await waitFor(() => expect(stub.user.groups).toHaveBeenCalledTimes(2))
    // 密码强度提示与已选组归零
    await waitFor(() => expect(screen.queryByText('强')).not.toBeInTheDocument())
    expect(selectedGroups()).toEqual([])
    // 密码框内容也被 form.resetFields 清空（“关闭再打开回到初始态”）
    expect(pwdInput().value).toBe('')
    // 默认勾选回到初始
    await waitFor(() => expect(formSwitches()[0]).toBeChecked())
  })

  it('两次密码不一致：不调用 api.user.create，且给出明确原因', async () => {
    renderModal(true)
    await waitFor(() => expect(stub.user.groups).toHaveBeenCalledTimes(1))

    fireEvent.change(nameInput(), { target: { value: 'john.doe' } })
    fireEvent.change(pwdInput(), { target: { value: 'Str0ng!Passw0rd' } })
    fireEvent.change(confirmInput(), { target: { value: 'Str0ng!Passw0rdX' } })
    fireEvent.click(screen.getByRole('button', { name: /创\s*建/ }))

    await waitFor(() => expect(bodyText()).toContain('两次输入的密码不一致'))
    expect(stub.user.create).not.toHaveBeenCalled()
  })

  it('创建失败：原因可见、不清空已填、不关窗', async () => {
    stub.user.create.mockRejectedValue(new Error('用户名已存在'))
    const { onClose, onSuccess } = renderModal(true)
    await waitFor(() => expect(stub.user.groups).toHaveBeenCalledTimes(1))

    fireEvent.change(nameInput(), { target: { value: 'john.doe' } })
    fireEvent.change(pwdInput(), { target: { value: 'Str0ng!Passw0rd' } })
    fireEvent.change(confirmInput(), { target: { value: 'Str0ng!Passw0rd' } })
    fireEvent.click(screen.getByRole('button', { name: /创\s*建/ }))

    await waitFor(() => expect(bodyText()).toContain('用户名已存在'))
    expect(onClose).not.toHaveBeenCalled()
    expect(onSuccess).not.toHaveBeenCalled()
    // 已填内容不丢 + 窗口没关
    expect(nameInput().value).toBe('john.doe')
    expect(bodyText()).toContain('新建本地用户')
  })

  it('组列表拉取失败：静默（不弹错），创建仍可用', async () => {
    stub.user.groups.mockRejectedValue(new Error('PowerShell 通道不可用'))
    renderModal(true)
    await settle()
    expect(bodyText()).not.toContain('PowerShell 通道不可用')

    fireEvent.change(nameInput(), { target: { value: 'john.doe' } })
    fireEvent.change(pwdInput(), { target: { value: 'Str0ng!Passw0rd' } })
    fireEvent.change(confirmInput(), { target: { value: 'Str0ng!Passw0rd' } })
    fireEvent.click(screen.getByRole('button', { name: /创\s*建/ }))

    await waitFor(() => expect(stub.user.create).toHaveBeenCalledTimes(1))
    expect(stub.user.create.mock.calls[0][0]).toMatchObject({
      name: 'john.doe',
      enabled: true,
      passwordChangeable: true,
      passwordExpires: false,
    })
  })
})
