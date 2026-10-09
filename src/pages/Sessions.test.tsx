import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { render, screen, waitFor, fireEvent, cleanup } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

// B1 试点验证：Sessions 页 react-query 数据流（首次加载出表格、冷启动不弹气泡、手动刷新重取）
const stub = vi.hoisted(() => {
  const s = {
    session: {
      list: vi.fn(),
      files: vi.fn(),
      close: vi.fn(),
      closeFile: vi.fn(),
    },
    adapter: { sessions: vi.fn(), closeSession: vi.fn() },
    window: { showBalloon: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import Sessions from './Sessions'
import { useUiStore } from '../stores/uiStore'

beforeEach(() => {
  useUiStore.setState({ selectedSessions: [], sessionCloseTick: 0 })
  stub.session.list.mockResolvedValue([
    {
      clientId: 'alice@PC1',
      clientUserName: 'alice',
      clientComputerName: 'PC1',
      sessionStartTime: '',
      clientOpenFiles: 2,
      clientIdleTime: 5,
      bytesReceived: 1,
      bytesSent: 1,
    },
  ])
  stub.session.files.mockResolvedValue([])
})

afterEach(async () => {
  cleanup()
  // 冲刷卸载竞态产生的微任务（react-query 取消），避免假阳性 unhandled rejections
  await new Promise((r) => setTimeout(r, 0))
  vi.clearAllMocks()
})

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: 0 } } })
  return render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <QueryClientProvider client={qc}>
          <Sessions />
        </QueryClientProvider>
      </AntdApp>
    </ConfigProvider>,
  )
}

describe('Sessions（B1 react-query 试点）', () => {
  it('首挂载拉取 SMB 会话并渲染行；冷启动仅建基线不弹气泡', async () => {
    renderPage()
    expect(await screen.findByText('alice')).toBeInTheDocument()
    expect(screen.getByText('PC1')).toBeInTheDocument()
    await waitFor(() => expect(stub.session.list).toHaveBeenCalledTimes(1))
    expect(stub.session.files).toHaveBeenCalledTimes(1)
    expect(stub.window.showBalloon).not.toHaveBeenCalled()
    expect(stub.adapter.sessions).not.toHaveBeenCalled() // smb 默认 tab，不拉 NFS
  })

  it('点击刷新按钮触发重取', async () => {
    renderPage()
    await screen.findByText('alice')
    fireEvent.click(screen.getByRole('button', { name: /刷\s*新/ }))
    await waitFor(() => expect(stub.session.list.mock.calls.length).toBeGreaterThanOrEqual(2))
  })

  it('断开会话成功后失效缓存触发重取', async () => {
    stub.session.close.mockResolvedValue(undefined)
    renderPage()
    await screen.findByText('alice')
    const before = stub.session.list.mock.calls.length
    const disconnectBtn = await screen.findByRole('button', { name: /断\s*开/ })
    fireEvent.click(disconnectBtn)
    const okBtn = await screen.findByRole('button', { name: /确\s*定/ })
    fireEvent.click(okBtn)
    await waitFor(() => expect(stub.session.close).toHaveBeenCalledWith('alice'))
    await waitFor(() => expect(stub.session.list.mock.calls.length).toBeGreaterThan(before))
  })
})
