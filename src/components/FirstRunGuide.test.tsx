import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup } from '@testing-library/react'
import { App as AntdApp } from 'antd'

// api.ts 模块加载时读取 window.winshare —— 在 import 前注入 stub（jsdom 环境已有 window）
const { suggestRoot, selectFolder, create } = vi.hoisted(() => {
  const suggestRoot = vi.fn()
  const selectFolder = vi.fn()
  const create = vi.fn()
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = {
    disk: { usages: vi.fn(), suggestRoot },
    system: { selectFolder },
    adapter: { create },
  }
  return { suggestRoot, selectFolder, create }
})

import FirstRunGuide from './FirstRunGuide'
import { FIRST_RUN_DONE_KEY, PERM_TIERS, shouldShowGuide } from '../utils/firstRun'

// globals:false 下 RTL auto-cleanup 不注册，手动逐用例卸载
afterEach(cleanup)

const SUGGESTED = 'D:\\Shared\\Photos'

/** antd 按钮会给中文文案插空格（「创 建」），按字面拼 \s* 匹配，不依赖内部实现 */
const nameRe = (text: string) => new RegExp(text.split('').join('\\s*'))
const byLabel = (text: string) => screen.getByRole('button', { name: nameRe(text) })
const pathInput = () => screen.getByLabelText('共享文件夹路径')
const nameInput = () => screen.getByLabelText('共享名')

function openGuide() {
  const onClose = vi.fn()
  const utils = render(
    <AntdApp>
      <FirstRunGuide open onClose={onClose} />
    </AntdApp>,
  )
  return { ...utils, onClose }
}

async function toStep2() {
  await vi.waitFor(() => expect(pathInput()).toHaveValue(SUGGESTED))
  fireEvent.click(byLabel('下一步'))
}

async function toLastStep() {
  await toStep2()
  fireEvent.click(byLabel('下一步'))
}

beforeEach(() => {
  localStorage.clear()
  suggestRoot.mockReset().mockResolvedValue(SUGGESTED)
  selectFolder.mockReset().mockResolvedValue(null)
  create.mockReset().mockResolvedValue({ name: 'Photos' })
})

describe('FirstRunGuide 结构：三步 + 高级参数折叠（新手零术语）', () => {
  it('步骤标题来自 GUIDE_STEPS：选文件夹 → 给谁用 → 完成', async () => {
    openGuide()
    for (const t of ['选文件夹', '给谁用', '完成']) {
      expect(screen.getByText(t)).toBeInTheDocument()
    }
  })

  it('新手全程不遇到 SMB1/枚举/租约/NTFS——术语只活在折叠的高级参数里，点开才出现', async () => {
    const { baseElement } = openGuide()
    const jargon = /SMB1|NTFS|租约|枚举/
    await toLastStep() // 走完三步都没展开
    expect(baseElement.textContent).not.toMatch(jargon)

    // 回到第一步展开高级参数：术语此刻才进 DOM（Collapse 懒渲染，收起时不可见）
    fireEvent.click(byLabel('上一步'))
    fireEvent.click(byLabel('上一步'))
    fireEvent.click(screen.getByText('高级参数（绝大多数人不需要碰）'))
    await vi.waitFor(() => expect(baseElement.textContent).toMatch(jargon))
  })
})

describe('FirstRunGuide 步骤一：选文件夹', () => {
  it('默认路径来自 api.disk.suggestRoot()', async () => {
    openGuide()
    await vi.waitFor(() => expect(pathInput()).toHaveValue(SUGGESTED))
    expect(suggestRoot).toHaveBeenCalledTimes(1)
    expect(byLabel('下一步')).toBeEnabled()
  })

  it('路径为空时「下一步」禁用', async () => {
    openGuide()
    await vi.waitFor(() => expect(pathInput()).toHaveValue(SUGGESTED))
    fireEvent.change(pathInput(), { target: { value: '' } })
    expect(byLabel('下一步')).toBeDisabled()
  })

  it('可用原生选择换目录（api.system.selectFolder）', async () => {
    openGuide()
    await toStep2() // 等 suggestRoot 落地
    fireEvent.click(byLabel('上一步'))
    selectFolder.mockResolvedValue('E:\\Movies')
    fireEvent.click(byLabel('选个文件夹'))
    await vi.waitFor(() => expect(pathInput()).toHaveValue('E:\\Movies'))
    expect(selectFolder).toHaveBeenCalledTimes(1)
  })
})

describe('FirstRunGuide 步骤二：给谁用', () => {
  it('三档人话选项全部可见，默认选中最安全的「只让他们看看」', async () => {
    openGuide()
    await toStep2()
    for (const o of PERM_TIERS) {
      expect(screen.getByText(o.label)).toBeInTheDocument()
      expect(screen.getByText(o.desc)).toBeInTheDocument()
    }
    expect(screen.getByRole('radio', { name: nameRe('只让他们看看') })).toBeChecked()
  })
})

describe('FirstRunGuide 步骤三：完成 + 创建', () => {
  it('共享名默认由路径末级目录带出且可改', async () => {
    openGuide()
    await toLastStep()
    expect(nameInput()).toHaveValue('Photos')
    fireEvent.change(nameInput(), { target: { value: '家庭照片' } })
    fireEvent.click(byLabel('创建'))
    await vi.waitFor(() => expect(create).toHaveBeenCalledTimes(1))
    expect(create.mock.calls[0][0]).toMatchObject({ name: '家庭照片', path: SUGGESTED })
  })

  it('点「创建」调用 api.adapter.create，参数与所选档一致（完全交给→fullAccess）', async () => {
    openGuide()
    await toStep2()
    fireEvent.click(screen.getByRole('radio', { name: nameRe('完全交给他们') }))
    fireEvent.click(byLabel('下一步'))
    fireEvent.click(byLabel('创建'))
    await vi.waitFor(() => expect(create).toHaveBeenCalledTimes(1))
    expect(create.mock.calls[0][0]).toEqual({
      protocol: 'smb',
      name: 'Photos',
      path: SUGGESTED,
      fullAccess: ['Users'],
    })
  })

  it('创建失败：错误文案可见、不丢失已填内容、不写完成标记', async () => {
    const { onClose } = openGuide()
    await toLastStep()
    create.mockRejectedValueOnce(new Error('名称已被占用'))
    fireEvent.click(byLabel('创建'))
    await vi.waitFor(() => expect(screen.getByText('名称已被占用')).toBeInTheDocument())
    expect(nameInput()).toHaveValue('Photos')
    expect(localStorage.getItem(FIRST_RUN_DONE_KEY)).toBeNull()
    expect(onClose).not.toHaveBeenCalled()
  })

  it('创建成功：写入一次性「已完成初始化」标记并关闭，之后判定不再打扰', async () => {
    const { onClose } = openGuide()
    await toLastStep()
    fireEvent.click(byLabel('创建'))
    await vi.waitFor(() => expect(onClose).toHaveBeenCalledTimes(1))
    expect(localStorage.getItem(FIRST_RUN_DONE_KEY)).toBe('1')
    expect(shouldShowGuide([], false, localStorage.getItem(FIRST_RUN_DONE_KEY))).toBe(false)
  })

  it('「以后再说」：只关闭；不创建、也不写标记（还没完成初始化）', () => {
    const { onClose } = openGuide()
    fireEvent.click(byLabel('以后再说'))
    expect(onClose).toHaveBeenCalledTimes(1)
    expect(create).not.toHaveBeenCalled()
    expect(localStorage.getItem(FIRST_RUN_DONE_KEY)).toBeNull()
  })
})
