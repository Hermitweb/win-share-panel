import { useEffect, useState } from 'react'
import { Card, Statistic, Row, Col, Tag, Spin, App, Button } from 'antd'
import { ReloadOutlined } from '@ant-design/icons'
import ReactECharts from 'echarts-for-react'
import { api, call } from '../api'
import type { DashboardStats } from '../types'

export default function Dashboard() {
  const { message } = App.useApp()
  const [stats, setStats] = useState<DashboardStats | null>(null)
  const [loading, setLoading] = useState(true)

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

  const chartOption = {
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'category', data: stats?.topShares.map((s) => s.name) || [] },
    yAxis: { type: 'value' },
    series: [
      {
        type: 'bar',
        data: stats?.topShares.map((s) => s.connections) || [],
        itemStyle: { color: '#7EC8F0', borderRadius: [6, 6, 0, 0] },
        barMaxWidth: 40
      }
    ],
    grid: { left: 40, right: 20, top: 20, bottom: 30 }
  }

  const svc = stats?.serviceStatus
  const svcText = svc === 'Running' ? '运行中' : svc === 'Stopped' ? '已停止' : '未知'
  const svcColor = svc === 'Running' ? 'green' : 'red'

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-semibold">仪表板</h1>
        <Button icon={<ReloadOutlined />} onClick={load}>
          刷新
        </Button>
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
        <Card className="glass-card mt-4" title="热门共享连接数">
          <ReactECharts option={chartOption} style={{ height: 280 }} />
        </Card>
      </Spin>
    </div>
  )
}
