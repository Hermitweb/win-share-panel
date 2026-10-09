import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'

// t7：仪表板 24h 连接趋势 / 磁盘水位 / 「需要关注」聚合的行为测试。
// echarts-for-react 用 DOM 桩件替身：把 option 序列化进 data-option，断言真实配置
// （type:'time'、断点 null、双系列）而不是断言 canvas 像素。
const stub = vi.hoisted(() => {
  const s = {
    system: { dashboard: vi.fn() },
    state: { get: vi.fn() },
    disk: { usages: vi.fn() },
    security: { report: vi.fn() },
    smb: { getConfig: vi.fn() },
    protocol: { detect: vi.fn() },
    window: { showBalloon: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

vi.mock('echarts-for-react', async () => {
  const { forwardRef, useImperativeHandle, createElement } = await import('react')
  const ChartStub = forwardRef((props: any, ref: any) => {
    useImperativeHandle(ref, () => ({
      getEchartsInstance: () => ({ getDataURL: () => 'data:,stub' }),
    }))
    return createElement('div', {
      'data-testid': 'echart',
      'data-option': JSON.stringify(props.option ?? null),
      style: props.style,
    })
  })
  return { default: ChartStub }
})

import Dashboard, {
  buildAttentionItems,
  buildTrendOption,
  recentHistoryPoints,
  toTimeSeries,
  DISK_LEVEL_META,
  HISTORY_SAMPLE_MS,
} from './Dashboard'
import { diskLevel } from '../utils/shareDefaults'
import { useAppStore } from '../stores/appStore'
import { useUiStore } from '../stores/uiStore'
import type {
  AppState,
  DashboardStats,
  DiskUsage,
  HistoryPoint,
  SecurityReport,
  SmbServerConfig,
} from '../types'

const MIN = 60_000
const HOUR = 60 * MIN

const makeAppState = (
  over: Partial<Omit<AppState, 'alertRules'>> = {},
  rules: Partial<AppState['alertRules']> = {},
): AppState => ({
  theme: 'light',
  advancedMode: true,
  pinned: [],
  autoStart: false,
  dashboardHistory: [],
  journal: [],
  ...over,
  alertRules: {
    idleAlertMinutes: null,
    smb1Alert: true,
    weakPasswordAlert: true,
    diskLowGb: 20,
    ...rules,
  },
})

const makeStats = (topShares: DashboardStats['topShares'] = []): DashboardStats => ({
  shareCount: 1,
  activeSessions: 3,
  openFiles: 5,
  serviceStatus: 'Running',
  topShares,
  byProtocol: {
    smb: { shares: 1, sessions: 3 },
    nfs: { shares: 0, sessions: 0 },
    ftp: { shares: 0, sessions: 0 },
    webdav: { shares: 0, sessions: 0 },
  },
})

const makeSmbConfig = (over: Partial<SmbServerConfig> = {}): SmbServerConfig =>
  ({
    enableSMB1Protocol: false,
    enableSMB2Protocol: true,
    enableSMB3Protocol: true,
    enableGuestUserAccess: false,
    enableInsecureGuestLogons: false,
    auditSmb1Access: false,
    ...over,
  }) as unknown as SmbServerConfig

const DANGER_DISK: DiskUsage = { drive: 'C:', freeGB: 8, totalGB: 200, freePct: 4 }
const OK_DISK: DiskUsage = { drive: 'E:', freeGB: 120, totalGB: 512, freePct: 63 }

const SECURITY_WITH_ISSUES: SecurityReport = {
  checked: 3,
  issues: [
    { user: 'deploy', level: 'fail', issue: '账号设置了"密码永远不需要"，可空口令登录' },
    { user: 'share01', level: 'warn', issue: '密码设置为永不过期（共享账号建议定期轮换）' },
  ],
  at: 0,
}

/** 连续 3 点 + 约 3 小时停机缺口 + 2 点：稀疏段必须按真实时间戳散开且断线 */
function gapHistory(base: number): HistoryPoint[] {
  return [
    { ts: base - 4 * HOUR, sessions: 2, openFiles: 4 },
    { ts: base - 4 * HOUR + 5 * MIN, sessions: 3, openFiles: 4 },
    { ts: base - 4 * HOUR + 10 * MIN, sessions: 3, openFiles: 6 },
    { ts: base - 50 * MIN, sessions: 1, openFiles: 2 },
    { ts: base - 45 * MIN, sessions: 2, openFiles: 2 },
  ]
}

// —— 路由探针：记录跳转路径与携带 state（验证"跳转后不丢失上下文"） ——
let navLog: { pathname: string; state: unknown }[] = []
function LocationProbe() {
  const loc = useLocation()
  const entry = { pathname: loc.pathname, state: loc.state }
  const last = navLog[navLog.length - 1]
  if (!last || last.pathname !== entry.pathname || last.state !== entry.state) navLog.push(entry)
  return <div data-testid="route">{loc.pathname}</div>
}

function renderPage() {
  navLog = []
  const qc = new QueryClient({ defaultOptions: { queries: { retry: 0 } } })
  return render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <QueryClientProvider client={qc}>
          <MemoryRouter initialEntries={['/']}>
            <LocationProbe />
            <Routes>
              <Route path="/" element={<Dashboard />} />
              <Route path="/shares" element={<div>page-shares</div>} />
              <Route path="/users" element={<div>page-users</div>} />
              <Route path="/settings" element={<div>page-settings</div>} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </AntdApp>
    </ConfigProvider>,
  )
}

function chartOptions(): any[] {
  return screen
    .queryAllByTestId('echart')
    .map((el) => JSON.parse((el as HTMLElement).dataset.option ?? 'null'))
    .filter(Boolean)
}

function trendChart(): any {
  return chartOptions().find((o) => o?.xAxis?.type === 'time')
}

beforeEach(() => {
  useAppStore.setState({ state: makeAppState(), hydrated: false })
  useUiStore.setState({ protocolCaps: null, refreshTick: 0 })
  stub.system.dashboard.mockResolvedValue(
    makeStats([{ name: 'pub', connections: 3, protocol: 'smb' }]),
  )
  stub.state.get.mockResolvedValue(makeAppState())
  stub.disk.usages.mockResolvedValue([])
  stub.security.report.mockResolvedValue({ checked: 0, issues: [], at: Date.now() })
  stub.smb.getConfig.mockResolvedValue(makeSmbConfig())
  stub.protocol.detect.mockResolvedValue({})
})

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
  vi.clearAllMocks()
})

describe('趋势图纯函数（time 轴 / 停机断点 / 窗口）', () => {
  it('x 轴为 type:time，双系列并列默认存在连接数与打开文件', () => {
    const pts = gapHistory(Date.now())
    const opt: any = buildTrendOption(pts, 'both')
    expect(opt.xAxis.type).toBe('time')
    expect(opt.series.map((s: any) => s.name)).toEqual(['连接数', '打开文件'])
    for (const s of opt.series) expect(s.connectNulls).toBe(false)
  })

  it('稀疏采样保留真实时间戳：缺口两侧是原 ts，且缺口中心插入 null 断点', () => {
    const base = Date.now()
    const pts = gapHistory(base)
    const series = toTimeSeries(pts, (p) => p.sessions)
    // 3 个等距点 + 1 个断点 + 2 个点
    expect(series).toHaveLength(6)
    expect(series[0]).toEqual([base - 4 * HOUR, 2])
    expect(series[3]).toEqual([Math.round((pts[2].ts + pts[3].ts) / 2), null])
    expect(series[4][1]).toBe(1)
    // 断点前后仍是真实 ts：停机 3h 的间隔没有被"等距连续"抹平
    const ts = series.map(([t]) => t)
    expect(ts[4] - ts[2]).toBeGreaterThan(2 * HOUR)
    expect(ts[1] - ts[0]).toBe(5 * MIN)
  })

  it('窗口过滤 + 288 点上限：24h 前的点被丢弃，超限取最近 288 个', () => {
    const now = 100 * HOUR
    const pts: HistoryPoint[] = [
      { ts: now - 25 * HOUR, sessions: 1, openFiles: 1 },
      // 300 个 5min 间隔点，最后一个正好落在 now：最老的约 24.9h 前，超出 24h 窗口
      ...Array.from({ length: 300 }, (_, i) => ({
        ts: now - (299 - i) * HISTORY_SAMPLE_MS,
        sessions: i,
        openFiles: i,
      })),
    ]
    const kept = recentHistoryPoints(pts, now)
    expect(kept).toHaveLength(288)
    expect(kept[0].ts).toBeGreaterThan(now - 24 * HOUR)
    expect(kept[kept.length - 1].ts).toBe(now)
  })
})

describe('磁盘分级纯函数', () => {
  it('diskLevel 命中告警阈值分级，色板覆盖四档且分级色不同', () => {
    expect(diskLevel(DANGER_DISK, { lowGb: 20 })).toBe('danger')
    expect(diskLevel(OK_DISK, { lowGb: 20 })).toBe('ok')
    expect(Object.keys(DISK_LEVEL_META).sort()).toEqual(['danger', 'ok', 'unknown', 'warn'])
    expect(DISK_LEVEL_META.danger.color).not.toBe(DISK_LEVEL_META.ok.color)
    expect(DISK_LEVEL_META.warn.color).not.toBe(DISK_LEVEL_META.ok.color)
  })
})

describe('仪表板：空数据分支', () => {
  it('无采样点→解释性空态（不画 0 线）；无风险→需要关注空态；不臆造会话空闲告警', async () => {
    renderPage()
    expect(
      await screen.findByText('采样每 5 分钟记录一个点，趋势曲线需要服务运行一段时间'),
    ).toBeInTheDocument()
    expect(screen.getByText(/不画 0 线冒充数据/)).toBeInTheDocument()
    expect(screen.getByText('未发现本地磁盘卷')).toBeInTheDocument()
    expect(screen.getByText(/暂无需要关注项/)).toBeInTheDocument()
    // 空态下没有任何趋势图；热门共享仍渲染既有 category 图
    expect(trendChart()).toBeUndefined()
    expect(chartOptions().some((o) => o.xAxis?.type === 'category')).toBe(true)
    // 会话空闲时长在仪表板数据里不存在 → 不得出现该告警
    expect(screen.queryByText(/空闲/)).not.toBeInTheDocument()
  })

  it('不新增高频轮询：稳定后各数据源只拉取一次', async () => {
    renderPage()
    await screen.findByText(/暂无需要关注项/)
    await new Promise((r) => setTimeout(r, 150))
    expect(stub.system.dashboard).toHaveBeenCalledTimes(1)
    expect(stub.disk.usages).toHaveBeenCalledTimes(1)
    expect(stub.security.report).toHaveBeenCalledTimes(1)
    expect(stub.smb.getConfig).toHaveBeenCalledTimes(1)
    expect(stub.state.get).toHaveBeenCalledTimes(1) // 挂载时一次水合，无定时器
  })
})

describe('仪表板：有数据分支', () => {
  const withData = (rules: Partial<AppState['alertRules']> = {}) => {
    stub.state.get.mockResolvedValue(
      makeAppState({ dashboardHistory: gapHistory(Date.now()) }, rules),
    )
    stub.disk.usages.mockResolvedValue([OK_DISK, DANGER_DISK])
    stub.security.report.mockResolvedValue(SECURITY_WITH_ISSUES)
    stub.smb.getConfig.mockResolvedValue(
      makeSmbConfig({ enableSMB1Protocol: true, enableInsecureGuestLogons: true }),
    )
  }

  it('趋势卡渲染 time 轴双系列，停机缺口画成断线；可切换单系列', async () => {
    withData()
    renderPage()
    await waitFor(() => expect(trendChart()).toBeTruthy())
    const opt = trendChart()
    expect(opt.xAxis.type).toBe('time')
    expect(opt.series.map((s: any) => s.name)).toEqual(['连接数', '打开文件'])
    const sessionsData = opt.series[0].data
    expect(sessionsData.some((p: [number, number | null]) => p[1] === null)).toBe(true)

    fireEvent.click(screen.getByText('仅打开文件'))
    await waitFor(() => {
      const single = trendChart()
      expect(single.series.map((s: any) => s.name)).toEqual(['打开文件'])
    })
    // 既有的热门共享 category 图不被破坏
    expect(chartOptions().some((o) => o.xAxis?.type === 'category')).toBe(true)
  })

  it('磁盘水位卡按分级着色展示每盘 freeGB/totalGB（紧张盘排序在前）', async () => {
    withData()
    renderPage()
    // 精确全文匹配：告警条目里的同类数字串前后有更多文字，不会误命中
    expect(await screen.findByText('剩余 8 GB / 共 200 GB（剩余 4%）')).toBeInTheDocument()
    expect(screen.getByText('剩余 120 GB / 共 512 GB（剩余 63%）')).toBeInTheDocument()
    expect(screen.getByText('紧张')).toBeInTheDocument()
    expect(screen.getByText('正常')).toBeInTheDocument()
    expect(screen.getByText('C:')).toBeInTheDocument()
    expect(screen.getByText('E:')).toBeInTheDocument()
    const drives = screen.getAllByText(/剩余 \d+ GB \/ 共 \d+ GB/).map((el) => el.textContent)
    expect(drives.indexOf('剩余 8 GB / 共 200 GB（剩余 4%）')).toBeLessThan(
      drives.indexOf('剩余 120 GB / 共 512 GB（剩余 63%）'),
    )
  })

  it('需要关注聚合出 SMB1/访客/空口令/磁盘四类告警，且不臆造空闲告警', async () => {
    withData()
    renderPage()
    expect(await screen.findByText(/SMB1 协议仍在启用/)).toBeInTheDocument()
    expect(screen.getByText(/不安全来宾登录/)).toBeInTheDocument()
    expect(screen.getByText(/1 个启用账户可空口令登录/)).toBeInTheDocument()
    expect(screen.getByText(/已低于告警阈值 20 GB/)).toBeInTheDocument()
    expect(screen.getByText('关闭 SMB1')).toBeInTheDocument()
    expect(screen.getByText('调整访客策略')).toBeInTheDocument()
    expect(screen.getByText('查看账户')).toBeInTheDocument()
    expect(screen.getByText('查看共享')).toBeInTheDocument()
    // 告警项里没有"会话空闲"（数据不存在 → 如实省略）
    expect(screen.queryByText(/空闲/)).not.toBeInTheDocument()
  })

  it('三类告警开关全部关闭→对应条目不出现（数据仍在，仅告警静音）', async () => {
    withData({ smb1Alert: false, weakPasswordAlert: false, diskLowGb: 0 })
    renderPage()
    await screen.findByText(/暂无需要关注项/)
    expect(screen.queryByText(/SMB1 协议仍在启用/)).not.toBeInTheDocument()
    expect(screen.queryByText(/不安全来宾登录/)).not.toBeInTheDocument()
    expect(screen.queryByText(/空口令登录/)).not.toBeInTheDocument()
    expect(screen.queryByText(/已低于告警阈值/)).not.toBeInTheDocument()
    // 趋势与磁盘概览不受告警开关影响，数据照常展示
    expect(trendChart()).toBeTruthy()
    expect(screen.getByText(/剩余 8 GB \/ 共 200 GB/)).toBeInTheDocument()
  })

  it('单项开关可独立静音：仅关 SMB1 时其余三项仍在', async () => {
    withData({ smb1Alert: false })
    renderPage()
    await screen.findByText(/不安全来宾登录/)
    expect(screen.queryByText(/SMB1 协议仍在启用/)).not.toBeInTheDocument()
    expect(screen.getByText(/空口令登录/)).toBeInTheDocument()
    expect(screen.getByText(/已低于告警阈值/)).toBeInTheDocument()
  })

  it('部分检查源不可用时如实标注跳过，不误报"均在可接受范围"', async () => {
    stub.state.get.mockResolvedValue(makeAppState({ dashboardHistory: gapHistory(Date.now()) }))
    stub.smb.getConfig.mockRejectedValue(new Error('SMB 服务不可用'))
    stub.disk.usages.mockResolvedValue([OK_DISK])
    stub.security.report.mockResolvedValue({ checked: 2, issues: [], at: 0 })
    renderPage()
    expect(await screen.findByText(/因数据不可用而跳过的检查：SMB 配置探测/)).toBeInTheDocument()
    expect(screen.queryByText(/SMB1 协议仍在启用/)).not.toBeInTheDocument()
    expect(screen.getByText(/部分检查源不可用，已如实跳过/)).toBeInTheDocument()
  })
})

describe('仪表板：告警跳转携带上下文', () => {
  it('SMB1 告警跳转到 /settings 并携带 attention 状态', async () => {
    stub.smb.getConfig.mockResolvedValue(makeSmbConfig({ enableSMB1Protocol: true }))
    renderPage()
    fireEvent.click(await screen.findByText('关闭 SMB1'))
    expect(await screen.findByText('page-settings')).toBeInTheDocument()
    const last = navLog[navLog.length - 1]
    expect(last.pathname).toBe('/settings')
    expect(last.state).toMatchObject({ attention: 'smb1' })
  })

  it('磁盘低空间告警跳转到 /shares 并携带盘符上下文', async () => {
    stub.disk.usages.mockResolvedValue([DANGER_DISK])
    renderPage()
    fireEvent.click(await screen.findByText('查看共享'))
    expect(await screen.findByText('page-shares')).toBeInTheDocument()
    const last = navLog[navLog.length - 1]
    expect(last.pathname).toBe('/shares')
    expect(last.state).toMatchObject({ attention: 'disk', drive: 'C:' })
  })

  it('空口令账户告警跳转到 /users', async () => {
    stub.security.report.mockResolvedValue(SECURITY_WITH_ISSUES)
    renderPage()
    fireEvent.click(await screen.findByText('查看账户'))
    expect(await screen.findByText('page-users')).toBeInTheDocument()
    expect(navLog[navLog.length - 1].pathname).toBe('/users')
  })
})

describe('buildAttentionItems 纯函数（开关与数据门）', () => {
  it('diskLowGb<=0 时磁盘告警整类关闭；null 数据源不产生条目', () => {
    expect(
      buildAttentionItems({
        alertRules: { smb1Alert: true, weakPasswordAlert: true, diskLowGb: 0 },
        smbConfig: null,
        security: null,
        disks: [DANGER_DISK],
        skippedSources: [],
      }),
    ).toEqual([])
  })

  it('空闲规则（idleAlertMinutes）不产生任何仪表板告警——没有对应数据就不臆造', () => {
    const items = buildAttentionItems({
      alertRules: { smb1Alert: false, weakPasswordAlert: false, diskLowGb: 20 },
      smbConfig: makeSmbConfig({ enableSMB1Protocol: true, enableGuestUserAccess: true }),
      security: SECURITY_WITH_ISSUES,
      disks: [DANGER_DISK],
      skippedSources: [],
    })
    // diskLowGb=20 → 磁盘告警仍在，其余开关关闭 → 只剩磁盘项
    expect(items.map((i) => i.key)).toEqual(['disk:C:'])
  })
})
