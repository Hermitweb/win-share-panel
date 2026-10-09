import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor, within } from '@testing-library/react'
import { App as AntdApp } from 'antd'

// 真实走 src/api.ts（模块加载时读 window.winshare），只打桩 IPC 边界
const stub = vi.hoisted(() => {
  const s = {
    state: { get: vi.fn(), exportAll: vi.fn(), importAll: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import SettingsTransferCard from './SettingsTransferCard'

const APP_STATE = {
  theme: 'light',
  advancedMode: true,
  pinned: [],
  alertRules: { idleAlertMinutes: null, smb1Alert: true, weakPasswordAlert: true, diskLowGb: 20 },
  autoStart: false,
  dashboardHistory: [],
  journal: [],
}

function setup() {
  const view = render(
    <AntdApp>
      <SettingsTransferCard />
    </AntdApp>,
  )
  return {
    card: within(screen.getByTestId('settings-transfer')),
    input: () => view.container.querySelector('input[type="file"]') as HTMLInputElement,
    view,
  }
}

async function pickFile(input: HTMLInputElement, text: string, name = 's.json') {
  const file = new File([text], name, { type: 'application/json' })
  fireEvent.change(input, { target: { files: [file] } })
}

beforeEach(() => {
  stub.state.get.mockReset().mockResolvedValue(APP_STATE)
  stub.state.exportAll.mockReset()
  stub.state.importAll.mockReset()
  // jsdom 不实现对象 URL 与下载导航，打桩以便断言"有释放"
  vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fake')
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined)
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
})
afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

describe('SettingsTransferCard（设置整体导入导出）', () => {
  it('导出：取回 JSON → 触发下载 → 释放对象 URL（不泄漏）', async () => {
    stub.state.exportAll.mockResolvedValue('{"format":"winshare-panel/settings"}')
    const { card } = setup()
    fireEvent.click(card.getByRole('button', { name: /导出设置/ }))
    await waitFor(() => expect(stub.state.exportAll).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(URL.createObjectURL).toHaveBeenCalledTimes(1))
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:fake')
    expect(HTMLAnchorElement.prototype.click).toHaveBeenCalledTimes(1)
  })

  it('导出失败：原因显示在卡片内（不只在瞬时 message 里）', async () => {
    stub.state.exportAll.mockRejectedValue(new Error('需要管理员权限才能执行此操作'))
    const { card } = setup()
    fireEvent.click(card.getByRole('button', { name: /导出设置/ }))
    await waitFor(() => expect(card.getByText('操作失败')).toBeInTheDocument())
    expect(card.getByText(/需要管理员权限/)).toBeInTheDocument()
  })

  it('导入成功：应用后**重新水合**并如实反馈生效字段', async () => {
    stub.state.importAll.mockResolvedValue({ applied: ['theme', 'advancedMode', 'pinned'] })
    const { card, input } = setup()
    await pickFile(input(), '{"format":"winshare-panel/settings","version":1,"settings":{}}')
    await waitFor(() => expect(stub.state.importAll).toHaveBeenCalledTimes(1))
    // 关键：真源在主进程，导入后必须重取一次，否则界面继续显示旧值
    await waitFor(() => expect(stub.state.get).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(card.getByText('已应用 3 项设置')).toBeInTheDocument())
    expect(card.getByText(/theme、advancedMode、pinned/)).toBeInTheDocument()
  })

  it('导入失败：护栏拒绝的原因显示在卡片内', async () => {
    stub.state.importAll.mockRejectedValue(new Error('导入内容过大（上限 512 KB）'))
    const { card, input } = setup()
    await pickFile(input(), 'x')
    await waitFor(() => expect(card.getByText('操作失败')).toBeInTheDocument())
    expect(card.getByText(/导入内容过大/)).toBeInTheDocument()
    // 失败时不应假装已应用
    expect(card.queryByText(/已应用 \d+ 项设置/)).not.toBeInTheDocument()
  })

  it('边界写清楚：卡片明确声明不含共享配置/权限模板/台账/趋势', () => {
    const { card } = setup()
    expect(card.getByText(/不含/)).toBeInTheDocument()
    expect(card.getByText(/共享配置、权限模板、操作台账与连接趋势/)).toBeInTheDocument()
  })
})
