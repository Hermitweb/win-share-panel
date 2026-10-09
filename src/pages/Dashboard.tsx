import { useEffect, useMemo, useRef, useState } from 'react'
import {
  App,
  Button,
  Card,
  Col,
  Empty,
  Progress,
  Row,
  Segmented,
  Space,
  Spin,
  Statistic,
  Tag,
} from 'antd'
import {
  ReloadOutlined,
  DownloadOutlined,
  FileImageOutlined,
  RightOutlined,
} from '@ant-design/icons'
import ReactECharts from 'echarts-for-react'
import type { EChartsOption } from 'echarts'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { api } from '../api'
import type { DiskUsage, HistoryPoint, Protocol } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useAppStore } from '../stores/appStore'
import { useTickEffect } from '../hooks/useTickEffect'
import { useEnsureProtocolCaps } from '../hooks/useEnsureProtocolCaps'
import { diskLevel } from '../utils/shareDefaults'

// 协议配色（与共享管理页保持一致）
const PROTOCOL_COLOR: Record<Protocol, string> = {
  smb: '#7EC8F0',
  nfs: '#B37FEB',
  ftp: '#73D13D',
  webdav: '#FFA940',
}
const PROTOCOL_TAG_COLOR: Record<Protocol, string> = {
  smb: 'blue',
  nfs: 'purple',
  ftp: 'green',
  webdav: 'orange',
}
const PROTOCOL_LABEL: Record<Protocol, string> = {
  smb: 'SMB',
  nfs: 'NFS',
  ftp: 'FTP',
  webdav: 'WebDAV',
}

// ===== 24h 连接趋势 =====
// 数据源是主进程每 5 分钟一次的采样（electron/main.ts startHistorySampler，上限 288 点），
// 落在 appState.dashboardHistory。服务停止时采样跳过（不写垃圾 0 点），所以曲线天然稀疏：
// x 轴必须用 type:'time' + 真实时间戳，稀疏段才会按真实时间拉开，而不是被画成等距连续；
// 相邻采样间隔超过两个周期时在线性数据中插入 null 断点（配合 connectNulls:false），
// 停机区间留白而不是斜线假连接。
export const HISTORY_SAMPLE_MS = 5 * 60 * 1000
export const HISTORY_WINDOW_MS = 24 * 60 * 60 * 1000
export const HISTORY_MAX_POINTS = 288

export type TrendMode = 'both' | 'sessions' | 'openFiles'

/** 取最近 24h 的采样点：排序 + 窗口过滤（容忍小幅时钟偏差）+ 288 点上限 */
export function recentHistoryPoints(points: HistoryPoint[], now = Date.now()): HistoryPoint[] {
  return (points ?? [])
    .filter(
      (p) =>
        !!p &&
        Number.isFinite(p.ts) &&
        p.ts > now - HISTORY_WINDOW_MS &&
        p.ts <= now + HISTORY_SAMPLE_MS,
    )
    .sort((a, b) => a.ts - b.ts)
    .slice(-HISTORY_MAX_POINTS)
}

/** 采样点 → [ts, value] 时间序列；停机缺口处插入 null 断点，让线段在此断开 */
export function toTimeSeries(
  points: HistoryPoint[],
  pick: (p: HistoryPoint) => number,
): [number, number | null][] {
  const out: [number, number | null][] = []
  points.forEach((p, i) => {
    if (i > 0 && p.ts - points[i - 1].ts > HISTORY_SAMPLE_MS * 2) {
      // 缺口中心放一个 null 点：它两侧都不连线，视觉上等于整段留白
      out.push([Math.round((points[i - 1].ts + p.ts) / 2), null])
    }
    out.push([p.ts, pick(p)])
  })
  return out
}

const TREND_COLOR_SESSIONS = '#1677FF'
const TREND_COLOR_OPEN_FILES = '#FA8C16'

export function buildTrendOption(points: HistoryPoint[], mode: TrendMode): EChartsOption {
  const series: Array<Record<string, unknown>> = []
  if (mode === 'both' || mode === 'sessions') {
    series.push({
      name: '连接数',
      type: 'line',
      data: toTimeSeries(points, (p) => p.sessions),
      connectNulls: false,
      itemStyle: { color: TREND_COLOR_SESSIONS },
      lineStyle: { color: TREND_COLOR_SESSIONS },
      showSymbol: points.length < 12,
    })
  }
  if (mode === 'both' || mode === 'openFiles') {
    series.push({
      name: '打开文件',
      type: 'line',
      data: toTimeSeries(points, (p) => p.openFiles),
      connectNulls: false,
      itemStyle: { color: TREND_COLOR_OPEN_FILES },
      lineStyle: { color: TREND_COLOR_OPEN_FILES },
      showSymbol: points.length < 12,
    })
  }
  return {
    tooltip: { trigger: 'axis' },
    legend: mode === 'both' ? { data: ['连接数', '打开文件'], top: 0 } : { show: false },
    xAxis: { type: 'time', axisLabel: { hideOverlap: true } },
    yAxis: { type: 'value', minInterval: 1 },
    grid: { left: 44, right: 20, top: mode === 'both' ? 30 : 12, bottom: 24 },
    series,
  } as unknown as EChartsOption
}

// ===== 磁盘水位分级 =====
export const DISK_LEVEL_META: Record<
  'ok' | 'warn' | 'danger' | 'unknown',
  { color: string; tagColor: string; label: string }
> = {
  ok: { color: '#52C41A', tagColor: 'green', label: '正常' },
  warn: { color: '#FAAD14', tagColor: 'orange', label: '偏低' },
  danger: { color: '#FF4D4F', tagColor: 'red', label: '紧张' },
  unknown: { color: 'rgba(128,128,128,0.55)', tagColor: 'default', label: '未知' },
}

// ===== 「需要关注」聚合 =====
// 只呈现仪表板上下文里真实可判定的四类：SMB1、访客/不安全来宾登录、空口令/永不过期账户、
// 磁盘低于阈值。映射到 alertRules 的对应开关：
//   smb1Alert        → SMB1
//   weakPasswordAlert→ 访客/不安全来宾登录 + 空口令/永不过期账户（凭据弱化族告警）
//   diskLowGb        → 磁盘低空间（>0 才告警；它同时是 diskLevel 的绝对阈值）
// idleAlertMinutes 有意不出现在这里：appState/DashboardStats 没有会话空闲数据，
// 空闲时长只在会话页逐条可见——按"不臆造告警"原则如实省略。
export interface AttentionItem {
  key: string
  level: 'danger' | 'warn'
  text: string
  actionLabel: string
  to: string
  navState: Record<string, unknown>
}

export interface AttentionInput {
  alertRules: { smb1Alert: boolean; weakPasswordAlert: boolean; diskLowGb: number }
  smbConfig?: {
    enableSMB1Protocol: boolean
    enableGuestUserAccess: boolean
    enableInsecureGuestLogons: boolean
  } | null
  security?: { checked: number; issues: { user: string; level: string; issue: string }[] } | null
  disks: DiskUsage[]
  skippedSources: string[]
}

export function buildAttentionItems(input: AttentionInput): AttentionItem[] {
  const items: AttentionItem[] = []
  const { alertRules, smbConfig, security, disks } = input

  if (alertRules.smb1Alert && smbConfig?.enableSMB1Protocol) {
    items.push({
      key: 'smb1',
      level: 'danger',
      text: 'SMB1 协议仍在启用，这是被公开攻击的旧协议，建议关闭并观察旧客户端',
      actionLabel: '关闭 SMB1',
      to: '/settings',
      navState: { attention: 'smb1' },
    })
  }
  if (alertRules.weakPasswordAlert && smbConfig) {
    const flags: string[] = []
    if (smbConfig.enableGuestUserAccess) flags.push('访客用户访问')
    if (smbConfig.enableInsecureGuestLogons) flags.push('不安全来宾登录')
    if (flags.length) {
      items.push({
        key: 'guest',
        level: smbConfig.enableInsecureGuestLogons ? 'danger' : 'warn',
        text: `已开启${flags.join('与')}，匿名客户端无需有效凭据即可探测共享`,
        actionLabel: '调整访客策略',
        to: '/settings',
        navState: { attention: 'guest' },
      })
    }
  }
  if (alertRules.weakPasswordAlert && security) {
    const blank = security.issues.filter((i) => i.level === 'fail')
    const neverExpire = security.issues.filter((i) => i.issue.includes('永不过期'))
    if (blank.length || neverExpire.length) {
      const parts: string[] = []
      if (blank.length) parts.push(`${blank.length} 个启用账户可空口令登录`)
      if (neverExpire.length) parts.push(`${neverExpire.length} 个账户密码永不过期`)
      items.push({
        key: 'password',
        level: blank.length ? 'danger' : 'warn',
        text: `${parts.join('，')}（共检查 ${security.checked} 个启用账户）`,
        actionLabel: '查看账户',
        to: '/users',
        navState: { attention: 'weak-password' },
      })
    }
  }
  if (alertRules.diskLowGb > 0) {
    for (const d of disks) {
      const lvl = diskLevel(d, { lowGb: alertRules.diskLowGb })
      if (lvl === 'warn' || lvl === 'danger') {
        items.push({
          key: `disk:${d.drive}`,
          level: lvl === 'danger' ? 'danger' : 'warn',
          text: `${d.drive} 盘剩余 ${d.freeGB} GB / 共 ${d.totalGB} GB，已低于告警阈值 ${alertRules.diskLowGb} GB，共享写入可能失败`,
          actionLabel: '查看共享',
          to: '/shares',
          navState: { attention: 'disk', drive: d.drive },
        })
      }
    }
  }
  return items
}

export default function Dashboard() {
  const { message } = App.useApp()
  const navigate = useNavigate()
  const chartRef = useRef<ReactECharts>(null)

  const refreshTick = useUiStore((s) => s.refreshTick)
  const protocolCaps = useUiStore((s) => s.protocolCaps)

  const hydrated = useAppStore((s) => s.hydrated)
  const alertRules = useAppStore((s) => s.state.alertRules)
  const dashboardHistory = useAppStore((s) => s.state.dashboardHistory)
  const hydrate = useAppStore((s) => s.hydrate)

  // B1：数据层 react-query（原 stats/loading state + mount/tick 手写 load）
  const queryClient = useQueryClient()
  const { data, isFetching, error } = useQuery({
    queryKey: ['dashboard'],
    queryFn: () => api.system.dashboard(),
  })
  const stats = data ?? null
  const loading = isFetching && !data

  // 趋势/磁盘/安全数据只在挂载与手动刷新时读取——主进程采样本来就是 5 分钟粒度，
  // 本页不新增任何轮询定时器（刷新节奏上限 60s 一次，这里取"按需刷新"更省）。
  const diskQuery = useQuery({ queryKey: ['disk-usage'], queryFn: () => api.disk.usages() })
  const securityQuery = useQuery({
    queryKey: ['security-report'],
    queryFn: () => api.security.report(),
  })
  const smbConfigQuery = useQuery({ queryKey: ['smb-config'], queryFn: () => api.smb.getConfig() })

  // 首轮失败提示（与原 load catch 语义一致）
  useEffect(() => {
    if (error && !data) message.error((error as Error).message)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- error/data 引用稳定于单次状态迁移
  }, [error, data])

  // 协议安装状态检测（统一入口 useEnsureProtocolCaps）：
  // 仪表盘需用 installed 字段判断"已安装/未安装"，而非用共享数判断
  useEnsureProtocolCaps()

  // dashboardHistory 是 appState 的镜像：进入仪表板时重新水合一次，
  // 让主进程新写入的采样点可见（state:get 命中主进程内存缓存，代价极低）
  useEffect(() => {
    void hydrate()
  }, [hydrate])

  // hotkey F5 / 刷新按钮 → 失效本页全部查询 + 重新水合 appState（含采样历史）
  const refresh = () => {
    for (const key of ['dashboard', 'disk-usage', 'security-report', 'smb-config']) {
      void queryClient.invalidateQueries({ queryKey: [key] }).catch(() => {})
    }
    void hydrate()
  }
  useTickEffect(refreshTick, refresh)

  const chartOption = {
    tooltip: {
      trigger: 'axis',
      formatter: (params: Array<{ dataIndex: number; value: number }>) => {
        const p = params[0]
        if (!p || !stats) return ''
        const s = stats.topShares[p.dataIndex]
        return s ? `${PROTOCOL_LABEL[s.protocol]} · ${s.name}<br/>连接数：${p.value}` : ''
      },
    },
    xAxis: { type: 'category', data: stats?.topShares.map((s) => s.name) || [] },
    yAxis: { type: 'value' },
    series: [
      {
        type: 'bar',
        data:
          stats?.topShares.map((s) => ({
            value: s.connections,
            itemStyle: { color: PROTOCOL_COLOR[s.protocol], borderRadius: [6, 6, 0, 0] },
          })) || [],
        barMaxWidth: 40,
      },
    ],
    grid: { left: 40, right: 20, top: 20, bottom: 30 },
  }

  // —— 趋势卡 ——
  const [trendMode, setTrendMode] = useState<TrendMode>('both')
  const recentPoints = useMemo(() => recentHistoryPoints(dashboardHistory), [dashboardHistory])
  const trendOption = useMemo(
    () => buildTrendOption(recentPoints, trendMode),
    [recentPoints, trendMode],
  )
  const trendAllEmpty = hydrated && dashboardHistory.length === 0
  const trendWindowEmpty = hydrated && dashboardHistory.length > 0 && recentPoints.length === 0

  // —— 磁盘卡 ——
  const diskRows = useMemo(() => {
    const rows = (diskQuery.data ?? []).map((d) => ({
      d,
      level: diskLevel(d, { lowGb: alertRules.diskLowGb }),
    }))
    const order = { danger: 0, warn: 1, ok: 2, unknown: 3 } as const
    return rows.sort(
      (a, b) => order[a.level] - order[b.level] || a.d.drive.localeCompare(b.d.drive),
    )
  }, [diskQuery.data, alertRules.diskLowGb])

  // —— 需要关注卡 ——
  const attentionLoading =
    !hydrated || diskQuery.isPending || securityQuery.isPending || smbConfigQuery.isPending
  const attentionItems = useMemo(
    () =>
      buildAttentionItems({
        alertRules,
        smbConfig: smbConfigQuery.data ?? null,
        security: securityQuery.data ?? null,
        disks: diskQuery.data ?? [],
        skippedSources: [],
      }),
    [alertRules, smbConfigQuery.data, securityQuery.data, diskQuery.data],
  )
  const skippedSources = [
    smbConfigQuery.isError ? 'SMB 配置探测' : null,
    securityQuery.isError ? '账户安全体检' : null,
    diskQuery.isError ? '磁盘水位读取' : null,
  ].filter((x): x is string => x !== null)

  const svc = stats?.serviceStatus
  const svcText = svc === 'Running' ? '运行中' : svc === 'Stopped' ? '已停止' : '未知'
  const svcColor = svc === 'Running' ? 'green' : 'red'

  const exportPng = () => {
    const inst = chartRef.current?.getEchartsInstance()
    if (!inst) {
      message.warning('图表尚未渲染')
      return
    }
    const url = inst.getDataURL({
      type: 'png',
      pixelRatio: 2,
      // echarts 的 getDataURL 走 canvas，不认 CSS 变量，故此处必须是具体色值；
      // 与 index.css 的 --chart-bg 同值，随 <html data-theme> 选择。
      backgroundColor: document.documentElement.dataset.theme === 'dark' ? '#141B23' : '#F4FAFD',
    })
    const a = document.createElement('a')
    a.href = url
    a.download = `dashboard-${Date.now()}.png`
    a.click()
    message.success('已导出 PNG')
  }

  const exportCsv = () => {
    if (!stats) {
      message.warning('暂无数据')
      return
    }
    const rows: (string | number)[][] = [
      ['指标', '值'],
      ['共享总数', stats.shareCount],
      ['活跃会话', stats.activeSessions],
      ['打开文件', stats.openFiles],
      ['服务状态', stats.serviceStatus],
    ]
    ;(['smb', 'nfs', 'ftp', 'webdav'] as Protocol[]).forEach((p) => {
      const info = stats.byProtocol[p]
      rows.push([`协议:${PROTOCOL_LABEL[p]}`, `共享 ${info.shares} / 会话 ${info.sessions}`])
    })
    stats.topShares.forEach((s) => {
      rows.push([`热门:${PROTOCOL_LABEL[s.protocol]}:${s.name}`, s.connections])
    })
    const csv = rows
      .map((r) => r.map((c) => `"${String(c).replace(/"/g, '""')}"`).join(','))
      .join('\n')
    const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `dashboard-${Date.now()}.csv`
    a.click()
    URL.revokeObjectURL(url)
    message.success('已导出 CSV')
  }

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-2 mb-4">
        <h1 className="text-xl font-semibold">仪表板</h1>
        <Space wrap>
          <Button icon={<FileImageOutlined />} onClick={exportPng} disabled={!stats}>
            导出 PNG
          </Button>
          <Button icon={<DownloadOutlined />} onClick={exportCsv} disabled={!stats}>
            导出 CSV
          </Button>
          <Button icon={<ReloadOutlined />} onClick={refresh}>
            刷新
          </Button>
        </Space>
      </div>
      <Spin spinning={loading}>
        <Row gutter={16}>
          <Col span={6}>
            <Card className="glass-card">
              <Statistic title="共享总数" value={stats?.shareCount ?? 0} />
            </Card>
          </Col>
          <Col span={6}>
            <Card className="glass-card">
              <Statistic title="活跃会话" value={stats?.activeSessions ?? 0} />
            </Card>
          </Col>
          <Col span={6}>
            <Card className="glass-card">
              <Statistic title="打开文件" value={stats?.openFiles ?? 0} />
            </Card>
          </Col>
          <Col span={6}>
            <Card className="glass-card">
              <div className="text-sm text-fog mb-1">服务状态</div>
              <Tag color={svcColor} style={{ fontSize: 14, padding: '2px 12px' }}>
                {svcText}
              </Tag>
            </Card>
          </Col>
        </Row>
        <Card className="glass-card mt-4" title="需要关注">
          {attentionLoading ? (
            <Spin size="small" />
          ) : attentionItems.length === 0 ? (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={
                skippedSources.length
                  ? '可判定的检查项均未命中（部分检查源不可用，已如实跳过）'
                  : '暂无需要关注项（已按当前告警规则检查 SMB1 / 访客策略 / 账户口令 / 磁盘水位）'
              }
            />
          ) : (
            attentionItems.map((it) => (
              <div
                key={it.key}
                className="flex flex-wrap items-center justify-between gap-2 mb-2 last:mb-0"
              >
                <Space>
                  <Tag color={it.level === 'danger' ? 'red' : 'orange'}>
                    {it.level === 'danger' ? '高风险' : '注意'}
                  </Tag>
                  <span>{it.text}</span>
                </Space>
                <Button
                  type="link"
                  size="small"
                  onClick={() => navigate(it.to, { state: it.navState })}
                >
                  {it.actionLabel} <RightOutlined />
                </Button>
              </div>
            ))
          )}
          {skippedSources.length > 0 && (
            <div className="text-xs text-fog mt-2">
              因数据不可用而跳过的检查：{skippedSources.join('、')}
              （未命中不代表该项安全，请修复后刷新）
            </div>
          )}
        </Card>
        <Card
          className="glass-card mt-4"
          title={
            <Space>
              连接数 24h 趋势
              <span className="text-xs text-fog" style={{ fontWeight: 'normal' }}>
                每 5 分钟采样 · 最多 288 点
              </span>
            </Space>
          }
          extra={
            <Segmented
              size="small"
              value={trendMode}
              onChange={(v) => setTrendMode(v as TrendMode)}
              options={[
                { label: '并列', value: 'both' },
                { label: '仅连接数', value: 'sessions' },
                { label: '仅打开文件', value: 'openFiles' },
              ]}
            />
          }
        >
          {!hydrated ? (
            <Spin size="small" />
          ) : trendAllEmpty ? (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={
                <div>
                  <div>采样每 5 分钟记录一个点，趋势曲线需要服务运行一段时间</div>
                  <div className="text-xs text-fog mt-1">
                    当前还没有任何采样点（应用刚启动），这里不画 0 线冒充数据
                  </div>
                </div>
              }
            />
          ) : trendWindowEmpty ? (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={`最近 24 小时没有采样点（更早的历史点 ${dashboardHistory.length} 个已超出显示窗口）`}
            />
          ) : (
            <ReactECharts option={trendOption} notMerge style={{ height: 260 }} />
          )}
        </Card>
        <Card className="glass-card mt-4" title="磁盘水位">
          {diskQuery.isPending ? (
            <Spin size="small" />
          ) : diskQuery.isError ? (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="磁盘探测失败（Get-PSDrive 不可用）"
            />
          ) : diskRows.length === 0 ? (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="未发现本地磁盘卷" />
          ) : (
            <Row gutter={16}>
              {diskRows.map(({ d, level }) => {
                const meta = DISK_LEVEL_META[level]
                const usedPct = Math.max(0, Math.min(100, 100 - d.freePct))
                return (
                  <Col span={8} key={d.drive}>
                    <div className="mb-3">
                      <div className="flex items-center justify-between mb-1">
                        <span className="font-medium">{d.drive}</span>
                        <Tag color={meta.tagColor}>{meta.label}</Tag>
                      </div>
                      <Progress
                        percent={usedPct}
                        strokeColor={meta.color}
                        showInfo={false}
                        size="small"
                      />
                      <div className="text-xs text-fog mt-1">
                        剩余 {d.freeGB} GB / 共 {d.totalGB} GB（剩余 {d.freePct}%）
                      </div>
                    </div>
                  </Col>
                )
              })}
            </Row>
          )}
        </Card>
        <Card className="glass-card mt-4" title="协议分布">
          <Row gutter={16}>
            {(['smb', 'nfs', 'ftp', 'webdav'] as Protocol[]).map((p) => {
              const info = stats?.byProtocol[p]
              // 用 protocolCaps.installed 判断是否已安装，而非用共享/会话数判断
              // （协议可能已安装但尚未创建共享，此时不应显示"未装"）
              const installed = protocolCaps?.[p]?.installed ?? false
              return (
                <Col span={6} key={p}>
                  <div className="mb-2">
                    <Tag
                      color={PROTOCOL_TAG_COLOR[p]}
                      style={{ fontSize: 13, padding: '1px 10px' }}
                    >
                      {PROTOCOL_LABEL[p]}
                    </Tag>
                    {!installed && <span className="text-xs text-fog ml-1">未装</span>}
                  </div>
                  <Statistic
                    title="共享数"
                    value={info?.shares ?? 0}
                    styles={{ content: { color: PROTOCOL_COLOR[p] } }}
                  />
                  <div className="text-xs text-fog mt-1">会话 {info?.sessions ?? 0}</div>
                </Col>
              )
            })}
          </Row>
        </Card>
        <Card className="glass-card mt-4" title="热门共享连接数">
          {stats && stats.topShares.length > 0 ? (
            <ReactECharts ref={chartRef} option={chartOption} style={{ height: 280 }} />
          ) : (
            <Empty description="暂无共享连接数据" />
          )}
        </Card>
      </Spin>
    </div>
  )
}
