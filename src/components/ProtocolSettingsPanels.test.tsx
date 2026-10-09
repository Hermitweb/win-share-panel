import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import {
  render,
  renderHook,
  screen,
  fireEvent,
  cleanup,
  waitFor,
  act,
} from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement, ReactNode } from 'react'
import type {
  Protocol,
  ProtocolDetectionResult,
  ProtocolFeatureState,
  ServiceStatus,
} from '../types'

// api.ts 在模块加载时读取 window.winshare —— 必须在 import 组件前注入 stub
const stub = vi.hoisted(() => {
  const svc = () => ({
    getConfig: vi.fn(),
    serviceStatus: vi.fn(),
    setConfig: vi.fn(),
    restoreDefault: vi.fn(),
    restart: vi.fn(),
    start: vi.fn(),
    stop: vi.fn(),
  })
  const s = { protocol: { detect: vi.fn() }, nfs: svc(), ftp: svc(), webdav: svc() }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import NfsSettingsPanel from './NfsSettingsPanel'
import FtpSettingsPanel from './FtpSettingsPanel'
import WebdavSettingsPanel from './WebdavSettingsPanel'
import { useProtocolSettings } from '../hooks/useProtocolSettings'
import { useUiStore } from '../stores/uiStore'
import type { NfsServerConfig } from '../types'

// 本文件是 antd + jsdom 的重渲染用例（三面板 × 门控/保存/服务控制/恢复默认/刷新 tick），
// 跑全量（尤其 test:coverage 插桩）时与其余重型文件并行，个别用例会超过默认 5s 上限——
// 那是负载不是功能失败，与仓库其余重型 antd+jsdom 测试文件统一放宽。
vi.setConfig({ testTimeout: 30_000 })

// ============================================================================
// t2 · 三协议设置面板（R-5 设置半边 + useQuery 原样保留）
// 断言口径（docs/audit/05-renderer-debt.md §3.3）：
//   ① "只加载一次" = 次数断言 + 静置 200ms 后仍为 1；③ 断言可见文本；
//   ④ 失败路径同时断言"不该发生的没发生"；⑤ 降级分支断言"不发生该协议专属 IPC"。
// 数据层是 react-query（与抽象前逐字一致）：load = invalidateQueries（setState-free）。
// ============================================================================

const RUNNING: ServiceStatus = { name: 'svc', status: 'Running', startType: 'Automatic' }
const STOPPED: ServiceStatus = { name: 'svc', status: 'Stopped', startType: 'Manual' }

function feature(protocol: Protocol, installed: boolean): ProtocolFeatureState {
  return {
    protocol,
    installed,
    installType: 'server-feature',
    serviceName: `${protocol}-svc`,
    serviceStatus: installed ? 'Running' : 'Unknown',
    installCommand: '',
    installHint: `${protocol.toUpperCase()} 未安装提示`,
  }
}

function caps(installed: { nfs: boolean; ftp: boolean; webdav: boolean }): ProtocolDetectionResult {
  return {
    smb: feature('smb', true),
    nfs: feature('nfs', installed.nfs),
    ftp: feature('ftp', installed.ftp),
    webdav: feature('webdav', installed.webdav),
  }
}

interface PanelCase {
  key: 'nfs' | 'ftp' | 'webdav'
  Panel: () => ReactElement
  fieldLabel: string
  switchId: string
  config: Record<string, unknown>
  detecting: string
  banner: string
  restartConfirm: string
  stopConfirm: string
  restoreConfirm: string
  restarted: string
  started: string
  stopped: string
}

const CASES: PanelCase[] = [
  {
    key: 'nfs',
    Panel: NfsSettingsPanel,
    fieldLabel: '优雅卸载',
    switchId: 'gracefulUnmount',
    config: { gracefulUnmount: true },
    detecting: '正在检测 NFS 协议...',
    banner: 'NFS 协议未安装',
    restartConfirm: '重启 NfsService 服务？',
    stopConfirm: '停止 NfsService 服务？',
    restoreConfirm: '确认恢复 NFS 默认配置？',
    restarted: 'NFS 服务已重启',
    started: 'NFS 服务已启动',
    stopped: 'NFS 服务已停止',
  },
  {
    key: 'ftp',
    Panel: FtpSettingsPanel,
    fieldLabel: '控制通道 SSL',
    switchId: 'sslClientCertRequired',
    config: { sslClientCertRequired: true },
    detecting: '正在检测 FTP 协议...',
    banner: 'FTP 协议未安装',
    restartConfirm: '重启 ftpsvc 服务？',
    stopConfirm: '停止 ftpsvc 服务？',
    restoreConfirm: '确认恢复 FTP 默认配置？',
    restarted: 'FTP 服务已重启',
    started: 'FTP 服务已启动',
    stopped: 'FTP 服务已停止',
  },
  {
    key: 'webdav',
    Panel: WebdavSettingsPanel,
    fieldLabel: '启用 Authoring',
    switchId: 'authoringEnabled',
    config: { authoringEnabled: true },
    detecting: '正在检测 WebDAV 协议...',
    banner: 'WEBDAV 协议未安装',
    restartConfirm: '重启 W3SVC 服务？',
    stopConfirm: '停止 W3SVC 服务？',
    restoreConfirm: '确认恢复 WebDAV 默认配置？',
    restarted: 'WebDAV 服务已重启',
    started: 'WebDAV 服务已启动',
    stopped: 'WebDAV 服务已停止',
  },
]

const settle = (ms = 200) => new Promise((r) => setTimeout(r, ms))
const bodyText = () => document.body.textContent || ''
async function expectNotice(re: RegExp) {
  await waitFor(() => expect(bodyText()).toMatch(re))
}

// 与 main.tsx 一致：中文 locale（Popconfirm 的默认确认文案是「确定」而非「OK」）
function renderPanel(ui: ReactElement) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: 0, refetchOnWindowFocus: false } },
  })
  return render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <QueryClientProvider client={qc}>{ui}</QueryClientProvider>
      </AntdApp>
    </ConfigProvider>,
  )
}

/** 点触发按钮并取当前**可见**的 Popconfirm 浮层（关闭后的浮层带 ant-popover-hidden，须排除） */
async function openPop(triggerName: RegExp): Promise<HTMLElement> {
  fireEvent.click(screen.getByRole('button', { name: triggerName }))
  return waitFor(() => {
    const pops = Array.from(document.querySelectorAll('.ant-popover, .ant-popconfirm')).filter(
      (p) => !p.classList.contains('ant-popover-hidden'),
    )
    if (pops.length === 0) throw new Error('Popconfirm 浮层未出现')
    return pops[pops.length - 1] as HTMLElement
  })
}

/** 在给定浮层内按去空格文本点按钮（okText/cancelText 与 antd 内部结构解耦） */
function clickPopBtn(pop: HTMLElement, text: string): void {
  const btn = Array.from(pop.querySelectorAll('button')).find(
    (b) => (b.textContent || '').replace(/\s/g, '') === text,
  )
  expect(btn).toBeTruthy()
  fireEvent.click(btn!)
}

/** 点触发按钮并确认（okText） */
async function confirmPop(triggerName: RegExp, okText: string): Promise<void> {
  const pop = await openPop(triggerName)
  clickPopBtn(pop, okText)
}

/** 表单控件当前态（按 antd 用 name 生成的 id 定位，避免依赖标签可访问名） */
function switchChecked(id: string): boolean {
  return document.querySelector(`#${id}`)?.getAttribute('aria-checked') === 'true'
}

beforeEach(() => {
  useUiStore.setState({
    protocolCaps: caps({ nfs: true, ftp: true, webdav: true }),
    refreshTick: 0,
    activeProtocol: 'all',
  })
  for (const c of CASES) {
    const a = stub[c.key]
    a.getConfig.mockReset().mockResolvedValue({ ...c.config })
    a.serviceStatus.mockReset().mockResolvedValue({ ...RUNNING, name: `${c.key}-svc` })
    a.setConfig.mockReset().mockResolvedValue(undefined)
    a.restoreDefault.mockReset().mockResolvedValue({ ...c.config })
    a.restart.mockReset().mockResolvedValue(undefined)
    a.start.mockReset().mockResolvedValue(undefined)
    a.stop.mockReset().mockResolvedValue(undefined)
  }
  // 默认让探测永不返回：需要「探测中」态时 protocolCaps 置 null
  stub.protocol.detect.mockReset().mockImplementation(() => new Promise(() => {}))
})

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
})

for (const c of CASES) {
  const a = stub[c.key]

  describe(`ProtocolSettingsPanel · ${c.key}`, () => {
    it('installed===true：挂载→getConfig/serviceStatus 各 1 次（enabled 门控），静置 200ms 不重取；其它协议零调用', async () => {
      renderPanel(<c.Panel />)
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(1))
      expect(a.serviceStatus).toHaveBeenCalledTimes(1)
      await settle()
      expect(a.getConfig).toHaveBeenCalledTimes(1)
      expect(a.serviceStatus).toHaveBeenCalledTimes(1)
      for (const other of CASES) {
        if (other.key === c.key) continue
        expect(stub[other.key].getConfig).not.toHaveBeenCalled()
        expect(stub[other.key].serviceStatus).not.toHaveBeenCalled()
      }
    })

    it('installed===false：渲染协议能力降级提示，且不调用 getConfig', async () => {
      useUiStore.setState({
        protocolCaps: caps({
          nfs: c.key !== 'nfs',
          ftp: c.key !== 'ftp',
          webdav: c.key !== 'webdav',
        }),
      })
      renderPanel(<c.Panel />)
      expect(await screen.findByText(c.banner)).toBeInTheDocument()
      await settle()
      expect(a.getConfig).not.toHaveBeenCalled()
      expect(a.serviceStatus).not.toHaveBeenCalled()
      expect(screen.queryByRole('button', { name: /保存配置/ })).not.toBeInTheDocument()
    })

    it('installed===null（探测中）：渲染「正在检测 … 协议...」，且不调用 getConfig', async () => {
      useUiStore.setState({ protocolCaps: null })
      renderPanel(<c.Panel />)
      expect(await screen.findByText(c.detecting)).toBeInTheDocument()
      await settle()
      expect(a.getConfig).not.toHaveBeenCalled()
      expect(stub.protocol.detect).toHaveBeenCalledTimes(1)
    })

    it('保存：setConfig(表单值) 1 次 + message.success("已保存") + 失效重取 1 次', async () => {
      renderPanel(<c.Panel />)
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(1))
      await waitFor(() => expect(switchChecked(c.switchId)).toBe(true))

      fireEvent.click(screen.getByRole('button', { name: /保存配置/ }))

      await waitFor(() => expect(a.setConfig).toHaveBeenCalledTimes(1))
      expect(a.setConfig).toHaveBeenCalledWith(expect.objectContaining(c.config))
      await expectNotice(/已保存/)
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(2))
    })

    it('保存失败：原因可见、不重取（不假成功）', async () => {
      a.setConfig.mockRejectedValue(new Error('配置写入失败'))
      renderPanel(<c.Panel />)
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(1))

      fireEvent.click(screen.getByRole('button', { name: /保存配置/ }))

      await expectNotice(/配置写入失败/)
      expect(bodyText()).not.toMatch(/已保存/)
      await settle()
      expect(a.getConfig).toHaveBeenCalledTimes(1)
    })

    it('服务控制：重启/停止各 1 次 IPC + 协议特有成功文案 + 每次失效重取', async () => {
      renderPanel(<c.Panel />)
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(1))

      // Running 时按钮是「停止」而非「启动」
      expect(screen.getByRole('button', { name: /停\s*止/ })).toBeInTheDocument()

      await confirmPop(/重启服务/, '确定')
      await waitFor(() => expect(a.restart).toHaveBeenCalledTimes(1))
      await expectNotice(new RegExp(c.restarted))
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(2))

      await confirmPop(/停\s*止/, '确定')
      await waitFor(() => expect(a.stop).toHaveBeenCalledTimes(1))
      await expectNotice(new RegExp(c.stopped))
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(3))
      expect(a.start).not.toHaveBeenCalled()
    })

    it('服务状态为 Stopped 时显示「启动」而非「停止」：点击→start 1 次 + 成功文案 + 重取', async () => {
      a.serviceStatus.mockResolvedValue({ ...STOPPED, name: `${c.key}-svc` })
      renderPanel(<c.Panel />)
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(1))
      await waitFor(() =>
        expect(screen.getByRole('button', { name: /启\s*动/ })).toBeInTheDocument(),
      )
      expect(screen.queryByRole('button', { name: /停\s*止/ })).not.toBeInTheDocument()

      fireEvent.click(screen.getByRole('button', { name: /启\s*动/ }))

      await waitFor(() => expect(a.start).toHaveBeenCalledTimes(1))
      await expectNotice(new RegExp(c.started))
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(2))
      expect(a.stop).not.toHaveBeenCalled()
    })

    it('服务控制失败：原因可见且不重取', async () => {
      a.restart.mockRejectedValue(new Error('服务重启被拒绝'))
      renderPanel(<c.Panel />)
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(1))

      await confirmPop(/重启服务/, '确定')

      await expectNotice(/服务重启被拒绝/)
      expect(bodyText()).not.toMatch(new RegExp(c.restarted))
      await settle()
      expect(a.getConfig).toHaveBeenCalledTimes(1)
    })

    it('恢复默认：IPC 1 次 + "已恢复默认配置" + 表单被填入默认值 + 重取', async () => {
      // 首轮读到"关"，恢复默认返回"开"：恢复后表单应显示默认值
      const off = Object.fromEntries(Object.keys(c.config).map((k) => [k, false]))
      a.getConfig.mockResolvedValueOnce(off).mockResolvedValue({ ...c.config })
      renderPanel(<c.Panel />)
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(1))
      await waitFor(() => expect(switchChecked(c.switchId)).toBe(false))

      await confirmPop(/恢复默认/, '恢复默认')

      await waitFor(() => expect(a.restoreDefault).toHaveBeenCalledTimes(1))
      await expectNotice(/已恢复默认配置/)
      await waitFor(() => expect(switchChecked(c.switchId)).toBe(true))
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(2))
    })

    it('刷新 tick：已安装时重取 1 次；未安装时不重取', async () => {
      renderPanel(<c.Panel />)
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(1))
      await act(async () => {
        useUiStore.setState({ refreshTick: 1 })
      })
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(2))

      cleanup()
      a.getConfig.mockClear()
      a.serviceStatus.mockClear()
      useUiStore.setState({
        protocolCaps: caps({
          nfs: c.key !== 'nfs',
          ftp: c.key !== 'ftp',
          webdav: c.key !== 'webdav',
        }),
        refreshTick: 0,
      })
      renderPanel(<c.Panel />)
      await settle(50)
      expect(a.getConfig).not.toHaveBeenCalled()
      await act(async () => {
        useUiStore.setState({ refreshTick: 1 })
      })
      await settle(100)
      expect(a.getConfig).not.toHaveBeenCalled()
    })

    it('协议特有字段与确认文案逐字可见（含降级之外的正常分支）', async () => {
      renderPanel(<c.Panel />)
      await waitFor(() => expect(a.getConfig).toHaveBeenCalledTimes(1))
      // 等脱数据落地后再断言服务状态区（外壳在 service 非空时渲染 Descriptions）
      await waitFor(() => expect(screen.getByText(`${c.key}-svc`)).toBeInTheDocument())

      // 协议特有字段
      expect(screen.getByText(c.fieldLabel)).toBeInTheDocument()
      // 服务状态 Descriptions（来自共享外壳）
      expect(bodyText()).toMatch(/服务状态/)
      expect(bodyText()).toMatch(/启动类型/)
      expect(bodyText()).toMatch(/服务名/)
      // 确认文案逐字（每条确认框打开后断言标题、再按「取消」关闭，避免浮层串扰）
      const restartPop = await openPop(/重启服务/)
      expect(restartPop.textContent || '').toMatch(new RegExp(c.restartConfirm))
      clickPopBtn(restartPop, '取消')
      const restorePop = await openPop(/恢复默认/)
      expect(restorePop.textContent || '').toMatch(new RegExp(c.restoreConfirm))
      clickPopBtn(restorePop, '取消')
      const stopPop = await openPop(/停\s*止/)
      expect(stopPop.textContent || '').toMatch(new RegExp(c.stopConfirm))
      clickPopBtn(stopPop, '取消')
    })
  })
}

describe('useProtocolSettings · 保存顺序契约', () => {
  it('校验失败：validateFields 在 try 之外——不弹 message、不调 setConfig（saving 不被置位）', async () => {
    const qc = new QueryClient({
      defaultOptions: { queries: { retry: 0, refetchOnWindowFocus: false } },
    })
    const wrapper = ({ children }: { children: ReactNode }) => (
      <ConfigProvider locale={zhCN}>
        <AntdApp>
          <QueryClientProvider client={qc}>{children}</QueryClientProvider>
        </AntdApp>
      </ConfigProvider>
    )
    const { result } = renderHook(
      () =>
        useProtocolSettings<NfsServerConfig>({
          protocol: 'nfs',
          api: {
            getConfig: stub.nfs.getConfig,
            serviceStatus: stub.nfs.serviceStatus,
            setConfig: stub.nfs.setConfig,
            restoreDefault: stub.nfs.restoreDefault,
            restart: stub.nfs.restart,
            start: stub.nfs.start,
            stop: stub.nfs.stop,
          },
          texts: {
            saved: '已保存',
            restarted: 'NFS 服务已重启',
            started: 'NFS 服务已启动',
            stopped: 'NFS 服务已停止',
            restored: '已恢复默认配置',
          },
        }),
      { wrapper },
    )
    await waitFor(() => expect(result.current.installed).toBe(true))

    vi.spyOn(result.current.form, 'validateFields').mockRejectedValue(new Error('表单校验失败'))
    await expect(result.current.save()).rejects.toBeTruthy()
    await settle(50)

    expect(stub.nfs.setConfig).not.toHaveBeenCalled()
    // 校验失败不弹 message（与抽象前逐字一致：validateFields 在 try 之外）
    expect(bodyText()).not.toMatch(/表单校验失败/)
    expect(bodyText()).not.toMatch(/已保存/)
  })

  it('首轮配置读取失败：原因可见，表单保持空', async () => {
    stub.nfs.getConfig.mockRejectedValue(new Error('NFS 配置读取失败'))
    renderPanel(<NfsSettingsPanel />)
    await expectNotice(/NFS 配置读取失败/)
    expect(switchChecked('gracefulUnmount')).toBe(false)
  })
})
