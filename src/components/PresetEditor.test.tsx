import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor, within } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import type { LocalGroup, LocalUser, PermissionPreset } from '../types'

// api.ts 在模块加载时读取 window.winshare —— 必须在 import 组件前注入 stub
const stub = vi.hoisted(() => {
  const s = {
    user: { list: vi.fn(), groups: vi.fn() },
    preset: { save: vi.fn(), update: vi.fn(), duplicate: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import PresetEditor from './PresetEditor'

vi.setConfig({ testTimeout: 30_000 })
const settle = () => new Promise((r) => setTimeout(r, 200))
const bodyText = () => document.body.textContent || ''

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
  vi.clearAllMocks()
})

const P1: PermissionPreset = {
  id: 'p1',
  name: '部门只读',
  description: '只读模板',
  builtIn: false,
  category: '基础',
  entries: [{ account: 'alice', accountType: 'User', access: 'Read' }],
}
const P2: PermissionPreset = {
  id: 'p2',
  name: '协作模板',
  description: '协作',
  builtIn: false,
  entries: [{ account: 'bob', accountType: 'Group', access: 'Change' }],
}

const tbody = () => document.querySelector('.ant-table-tbody') as HTMLElement
/** 表格里那一行的容器（以账号单元格为锚，避开 rc-table 的测量行） */
const rowOf = (account: string) => within(tbody()).getByText(account).closest('tr') as HTMLElement
/** 表格行里的「拒绝」开关（作用域限定该行，避开添加区的同名开关） */
const rowDenySwitch = (account: string) =>
  rowOf(account).querySelector('.ant-switch') as HTMLElement
/** 添加区容器：以「添加」按钮为锚，避开表格行里的同名控件 */
const addArea = () =>
  screen.getByRole('button', { name: /添\s*加/ }).closest('div.flex') as HTMLElement
const addSelects = () => Array.from(addArea().querySelectorAll('.ant-select')) as HTMLElement[]
const addDenySwitch = () => within(addArea()).getByRole('switch') as HTMLButtonElement
/** antd v6 单选 Select 把当前值放在 .ant-select-content 的 title 上 */
const selectionText = (sel: Element) =>
  sel.querySelector('.ant-select-content')?.getAttribute('title') ?? ''
const hasPlaceholder = (sel: Element) => !!sel.querySelector('.ant-select-placeholder')

/** 当前展开（非 hidden）的下拉里的选项，避开已关闭但仍留在 DOM 里的旧浮层 */
function visibleOptions(): HTMLElement[] {
  const dropdowns = Array.from(document.querySelectorAll('.ant-select-dropdown')).filter(
    (d) => !d.classList.contains('ant-select-dropdown-hidden'),
  )
  const dd = dropdowns[dropdowns.length - 1]
  if (!dd) return []
  return Array.from(dd.querySelectorAll('.ant-select-item-option-content')) as HTMLElement[]
}

/** antd Select 选项在 portal 里：先点开（mousedown 落在 combobox input 上），再按文本点选 */
async function pickOption(trigger: HTMLElement, label: string) {
  fireEvent.mouseDown(trigger.querySelector('input') ?? trigger)
  const option = await waitFor(() => {
    const el = visibleOptions().find((n) => (n.textContent || '').includes(label))
    if (!el) throw new Error(`下拉项未出现：${label}`)
    return el
  })
  fireEvent.click(option)
}

function wrap(open: boolean, preset: PermissionPreset | null) {
  return (
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <PresetEditor open={open} preset={preset} onClose={vi.fn()} onSuccess={vi.fn()} />
      </AntdApp>
    </ConfigProvider>
  )
}

function renderModal(open: boolean, preset: PermissionPreset | null) {
  const onClose = vi.fn()
  const onSuccess = vi.fn()
  const utils = render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <PresetEditor open={open} preset={preset} onClose={onClose} onSuccess={onSuccess} />
      </AntdApp>
    </ConfigProvider>,
  )
  return { ...utils, onClose, onSuccess }
}

beforeEach(() => {
  stub.user.list.mockResolvedValue([
    { name: 'alice', fullName: 'Alice A' },
  ] as unknown as LocalUser[])
  stub.user.groups.mockResolvedValue([{ name: 'Ops' }] as unknown as LocalGroup[])
  stub.preset.update.mockResolvedValue(undefined)
  stub.preset.save.mockResolvedValue(undefined)
  stub.preset.duplicate.mockResolvedValue({ ...P1, id: 'p1-copy' })
})

describe('PresetEditor（权限模板编辑）', () => {
  it('打开→候选账号取数各 1 次（既有 effect 不许顺手重写：静置 200ms 不重发）', async () => {
    renderModal(true, P1)
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(1))
    expect(stub.user.groups).toHaveBeenCalledTimes(1)
    await settle()
    expect(stub.user.list).toHaveBeenCalledTimes(1)
    expect(stub.user.groups).toHaveBeenCalledTimes(1)
  })

  it('条目来自 preset.entries 的深拷贝：改一行不会改写传入的 preset 对象', async () => {
    renderModal(true, P1)
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(1))
    expect(within(tbody()).getByText('alice')).toBeInTheDocument()

    // 改这一行的「拒绝」开关 → 只能改本地 entries
    expect(rowDenySwitch('alice')).not.toBeChecked()
    fireEvent.click(rowDenySwitch('alice'))
    await waitFor(() => expect(rowDenySwitch('alice')).toBeChecked())
    expect(P1.entries[0].deny).toBeUndefined()
    expect(P1.entries[0].access).toBe('Read')
  })

  it('关闭再打开：新增账号输入/类型/权限/拒绝开关回到初始', async () => {
    const { rerender } = renderModal(true, P1)
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(1))

    // 先把添加区全部改成非初始值
    await pickOption(addSelects()[0], 'alice')
    await pickOption(addSelects()[1], '组')
    await pickOption(addSelects()[2], '完全控制')
    fireEvent.click(addDenySwitch())
    await waitFor(() => expect(selectionText(addSelects()[0])).toBe('alice (Alice A)'))
    expect(selectionText(addSelects()[1])).toBe('组')
    expect(selectionText(addSelects()[2])).toBe('完全控制')
    expect(addDenySwitch()).toBeChecked()

    rerender(wrap(false, P1))
    await settle()
    rerender(wrap(true, P1))

    await waitFor(() => expect(hasPlaceholder(addSelects()[0])).toBe(true))
    expect(selectionText(addSelects()[1])).toBe('用户')
    expect(selectionText(addSelects()[2])).toBe('只读')
    expect(addDenySwitch()).not.toBeChecked()
    // 条目仍来自 preset（新建实例，不是被改脏的那份）
    expect(rowDenySwitch('alice')).not.toBeChecked()
  })

  it('切换 preset：条目替换为新预置', async () => {
    const { rerender } = renderModal(true, P1)
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(1))
    expect(within(tbody()).getByText('alice')).toBeInTheDocument()

    rerender(wrap(true, P2))
    await waitFor(() => expect(within(tbody()).getByText('bob')).toBeInTheDocument())
    expect(within(tbody()).queryByText('alice')).not.toBeInTheDocument()
    expect(bodyText()).toContain('编辑模板：协作模板')
  })

  it('保存失败：原因可见、不关窗、编辑不丢', async () => {
    stub.preset.update.mockRejectedValue(new Error('模板已被占用'))
    const { onClose, onSuccess } = renderModal(true, P1)
    await waitFor(() => expect(stub.user.list).toHaveBeenCalledTimes(1))

    // 把这一行权限改成「完全控制」，再触发一次失败的保存
    const rowAccessSelect = rowOf('alice').querySelectorAll('.ant-select')[1] as HTMLElement
    await pickOption(rowAccessSelect, '完全控制')
    await waitFor(() => expect(selectionText(rowAccessSelect)).toBe('完全控制'))

    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }))
    await waitFor(() => expect(bodyText()).toContain('模板已被占用'))
    expect(bodyText()).not.toContain('模板已更新')
    expect(onClose).not.toHaveBeenCalled()
    expect(onSuccess).not.toHaveBeenCalled()
    // 窗口没关 + 本地编辑还在
    expect(bodyText()).toContain('编辑模板：部门只读')
    expect(selectionText(rowAccessSelect)).toBe('完全控制')
  })
})
