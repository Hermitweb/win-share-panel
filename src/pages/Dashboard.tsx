import { useEffect, useRef, useState } from 'react'
import { Card, Statistic, Row, Col, Tag, Spin, App, Button, Space, Empty } from 'antd'
import { ReloadOutlined, DownloadOutlined, FileImageOutlined } from '@ant-design/icons'
import ReactECharts from 'echarts-for-react'
import { api, call } from '../api'
import type { DashboardStats, Protocol } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'

// 协议配色（与共享管理页保持一致）
const PROTOCOL_COLOR: Record<Protocol, string> = {
  smb: '#7EC8F0',
  nfs: '#B37FEB',
  ftp: '#73D13D',
  webdav: '#FFA940'
}
const PROTOCOL_TAG_COLOR: Record<Protocol, string> = {
  smb: 'blue',
  nfs: 'purple',
  ftp: 'green',
  webdav: 'orange'
}
const PROTOCOL_LABEL: Record<Protocol, string> = {
  smb: 'SMB',
  nfs: 'NFS',
  ftp: 'FTP',
  webdav: 'WebDAV'
}

export default function Dashboard() {
  const { message } = App.useApp()
  const [stats, setStats] = useState<DashboardStats | null>(null)
  const [loading, setLoading] = useState(true)
  const chartRef = useRef<ReactECharts>(null)

  const refreshTick = useUiStore((s) => s.refreshTick)

  const load = async () => {
    setLoading(true)
    try {
      setStats(await call(api.system.dashboard))
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => {
    load()
  }, [])

  // hotkey F5 刷新
  useTickEffect(refreshTick, () => {
    load()
  })

  const chartOption = {
    tooltip: {
      trigger: 'axis',
      formatter: (params: Array<{ dataIndex: number; value: number }>) => {
        const p = params[0]
        if (!p || !stats) return ''
        const s = stats.topShares[p.dataIndex]
        return s ? `${PROTOCOL_LABEL[s.protocol]} · ${s.name}<br/>连接数：${p.value}` : ''
      }
    },
    xAxis: { type: 'category', data: stats?.topShares.map((s) => s.name) || [] },
    yAxis: { type: 'value' },
    series: [
      {
        type: 'bar',
        data:
          stats?.topShares.map((s) => ({
            value: s.connections,
            itemStyle: { color: PROTOCOL_COLOR[s.protocol], borderRadius: [6, 6, 0, 0] }
          })) || [],
        barMaxWidth: 40
      }
    ],
    grid: { left: 40, right: 20, top: 20, bottom: 30 }
  }

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
      backgroundColor: '#F4FAFD'
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
      ['服务状态', stats.serviceStatus]
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
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-semibold">仪表板</h1>
        <Space>
          <Button icon={<FileImageOutlined />} onClick={exportPng} disabled={!stats}>
            导出 PNG
          </Button>
          <Button icon={<DownloadOutlined />} onClick={exportCsv} disabled={!stats}>
            导出 CSV
          </Button>
          <Button icon={<ReloadOutlined />} onClick={load}>
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
        <Card className="glass-card mt-4" title="协议分布">
          <Row gutter={16}>
            {(['smb', 'nfs', 'ftp', 'webdav'] as Protocol[]).map((p) => {
              const info = stats?.byProtocol[p]
              const has = (info?.shares ?? 0) > 0 || (info?.sessions ?? 0) > 0
              return (
                <Col span={6} key={p}>
                  <div className="mb-2">
                    <Tag color={PROTOCOL_TAG_COLOR[p]} style={{ fontSize: 13, padding: '1px 10px' }}>
                      {PROTOCOL_LABEL[p]}
                    </Tag>
                    {!has && <span className="text-xs text-fog ml-1">未装</span>}
                  </div>
                  <Statistic title="共享数" value={info?.shares ?? 0} valueStyle={{ color: PROTOCOL_COLOR[p] }} />
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
