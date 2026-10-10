import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'

// 只打桩 IPC 边界（真实走 src/api.ts），照抄 Settings.test.tsx 的手法
const stub = vi.hoisted(() => {
  const s = {
    state: { get: vi.fn(), patch: vi.fn(), exportAll: vi.fn(), importAll: vi.fn() },
    system: {
      appInfo: vi.fn(),
      openExternal: vi.fn(),
      openLogFolder: vi.fn(),
      auditLog: vi.fn(),
      autoStart: vi.fn(),
      setAutoStart: vi.fn(),
      // 「侧栏入口」用例渲染的是 Layout → HealthBar，它首帧就并行拉健康态与协议探测。
      // 少一个通道，Promise.all 的数组求值会同步抛错，先建出的 rejected promise 无人接住 →
      // vitest 报 unhandled rejection（用例全绿但运行确实不干净），故三个通道都要在。
      health: vi.fn(),
    },
    smb: { serviceStatus: vi.fn() },
    protocol: { detect: vi.fn() },
    security: { report: vi.fn() },
    firewall: { list: vi.fn(), preset: vi.fn(), ensure: vi.fn(), remove: vi.fn() },
    update: { check: vi.fn() },
    log: { tail: vi.fn() },
    // Layout 会渲染 TitleBar，它要 window 通道
    window: {
      isMaximized: vi.fn(async () => false),
      minimize: vi.fn(),
      toggleMaximize: vi.fn(),
      close: vi.fn(),
      onMaximizeChange: vi.fn(),
      showBalloon: vi.fn(),
    },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import AppSettings from './AppSettings'
import Layout from '../components/Layout'

const APP_STATE = {
  theme: 'light',
  advancedMode: true,
  pinned: [],
  alertRules: { idleAlertMinutes: null, smb1Alert: true, weakPasswordAlert: true, diskLowGb: 20 },
  autoStart: false,
  dashboardHistory: [],
  journal: [],
}

function renderPage(ui: React.ReactElement) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: 0, refetchOnWindowFocus: false } },
  })
  return render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <QueryClientProvider client={qc}>
          <MemoryRouter>{ui}</MemoryRouter>
        </QueryClientProvider>
      </AntdApp>
    </ConfigProvider>,
  )
}

beforeEach(() => {
  stub.window.isMaximized.mockReset().mockResolvedValue(false)
  stub.state.get.mockReset().mockResolvedValue(APP_STATE)
  stub.state.patch.mockReset().mockResolvedValue(APP_STATE)
  stub.system.appInfo.mockReset().mockResolvedValue({
    version: '9.9.9',
    electron: '31.3.0',
    chrome: '126.0.0.0',
    node: '20.15.1',
    platform: 'win32',
    arch: 'x64',
  })
  stub.system.openExternal.mockReset().mockResolvedValue(null)
  stub.system.openLogFolder.mockReset().mockResolvedValue('C:\\logs')
  stub.system.auditLog
    .mockReset()
    .mockResolvedValue(
      '{"ts":"2026-10-10T01:02:03.000Z","action":"createShare","target":"Reports","result":"success"}\n' +
        '{"ts":"2026-10-10T01:03:04.000Z","action":"deleteShare","target":"Old","result":"fail","detail":"拒绝访问"}\n',
    )
  stub.system.autoStart.mockReset().mockResolvedValue(true)
  stub.system.health.mockReset().mockResolvedValue({ ok: true, detail: 'SMB 模块可用' })
  stub.smb.serviceStatus.mockReset().mockResolvedValue({
    name: 'LanmanServer',
    status: 'Running',
    startType: 'Auto',
  })
  stub.protocol.detect.mockReset().mockResolvedValue(null)
  stub.security.report.mockReset().mockResolvedValue({ checked: 3, issues: [] })
  stub.firewall.list.mockReset().mockResolvedValue([])
  stub.update.check.mockReset()
  stub.log.tail.mockReset().mockResolvedValue('[2026-10-10T01:00:00Z] [INFO] [main] 应用启动完成')
})

afterEach(cleanup)

describe('应用设置页 · 关于', () => {
  it('显示版本号、项目地址、下载页与运行时时（版本来自主进程 appInfo，不是前端写死）', async () => {
    renderPage(<AppSettings />)
    await waitFor(() => expect(screen.getByText('v9.9.9')).toBeInTheDocument())
    expect(screen.getByText(/github\.com\/Hermitweb\/win-share-panel$/)).toBeInTheDocument()
    expect(screen.getByText(/releases\/latest$/)).toBeInTheDocument()
    expect(screen.getByText(/Electron 31\.3\.0 · Chromium 126/)).toBeInTheDocument()
    expect(screen.getByText(/win32 \/ x64/)).toBeInTheDocument()
  })

  it('「在浏览器打开」走受校验的 system:openExternal，且传的是 https 地址', async () => {
    renderPage(<AppSettings />)
    await waitFor(() => expect(screen.getByText('v9.9.9')).toBeInTheDocument())
    const buttons = screen.getAllByRole('button', { name: /在浏览器打开/ })
    expect(buttons.length).toBeGreaterThan(0)
    fireEvent.click(buttons[0])
    await waitFor(() => expect(stub.system.openExternal).toHaveBeenCalledTimes(1))
    const url = String(stub.system.openExternal.mock.calls[0][0])
    expect(url.startsWith('https://github.com/Hermitweb/win-share-panel')).toBe(true)
  })
})

describe('应用设置页 · 日志（从设置页 SMB 页签下搬来）', () => {
  it('应用日志面板显示 app.log 尾部内容，并可打开日志文件夹', async () => {
    renderPage(<AppSettings />)
    await waitFor(() => expect(screen.getByText(/应用启动完成/)).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: /打开日志文件夹/ }))
    await waitFor(() => expect(stub.system.openLogFolder).toHaveBeenCalledTimes(1))
  })

  it('审计日志把 JSONL 解析成结构化行，成败如实分列', async () => {
    renderPage(<AppSettings />)
    await waitFor(() => expect(screen.getByText('createShare')).toBeInTheDocument())
    expect(screen.getByText('Reports')).toBeInTheDocument()
    expect(screen.getByText('deleteShare')).toBeInTheDocument()
    expect(screen.getByText('成功')).toBeInTheDocument()
    expect(screen.getByText('失败')).toBeInTheDocument()
    expect(screen.getByText('拒绝访问')).toBeInTheDocument()
  })
})

describe('侧栏入口', () => {
  it('侧栏有「应用设置」一项，指向 /app-settings（不再是设置页里的一个页签）', () => {
    renderPage(<Layout onCommand={() => undefined}>子内容</Layout>)
    const link = screen.getByRole('link', { name: /应用设置/ })
    expect(link).toHaveAttribute('href', '/app-settings')
  })
})
