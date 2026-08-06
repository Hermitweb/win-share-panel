import { useEffect, useState } from 'react'
import { Button, Tag, Space, Popconfirm, Descriptions, App, Empty, Spin } from 'antd'
import { ReloadOutlined, PoweroffOutlined } from '@ant-design/icons'
import { api, call } from '../api'
import type { ServiceStatus, Protocol } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'

interface Props {
  protocol: 'ftp' | 'webdav'
}

// IIS 服务控制面板（FTP/WebDAV 复用）
// 仅服务状态 + 重启；站点级配置（端口/SSL/授权）在「共享管理」页编辑
const SERVICE_NAME: Record<Props['protocol'], string> = {
  ftp: 'ftpsvc',
  webdav: 'W3SVC'
}
const PROTOCOL_LABEL: Record<Props['protocol'], string> = {
  ftp: 'FTP',
  webdav: 'WebDAV'
}

export default function IisServicePanel({ protocol }: Props) {
  const { message } = App.useApp()
  const [svc, setSvc] = useState<ServiceStatus | null>(null)
  const [loading, setLoading] = useState(false)
  const [installed, setInstalled] = useState<boolean | null>(null)

  const refreshTick = useUiStore((s) => s.refreshTick)
  const protocolCaps = useUiStore((s) => s.protocolCaps)
  const setProtocolCaps = useUiStore((s) => s.setProtocolCaps)

  const fetchStatus = protocol === 'ftp' ? api.ftp.serviceStatus : api.webdav.serviceStatus
  const restartFn = protocol === 'ftp' ? api.ftp.restart : api.webdav.restart

  const load = async () => {
    setLoading(true)
    try {
      setSvc(await call(fetchStatus))
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  // 协议探测：store 中无缓存时主动 detect（避免依赖 Shares 页面懒加载）
  useEffect(() => {
    if (protocolCaps) {
      setInstalled(!!protocolCaps[protocol as Protocol]?.installed)
      return
    }
    let cancelled = false
    ;(async () => {
      try {
        const result = await call(api.protocol.detect)
        if (!cancelled) setProtocolCaps(result)
      } catch {
        // 检测失败：当作未装处理
        if (!cancelled) setInstalled(false)
      }
    })()
    return () => {
      cancelled = true
    }
  }, [protocolCaps, setProtocolCaps, protocol])

  // 仅在明确已装时加载服务状态，避免未装时触发错误
  useEffect(() => {
    if (installed !== true) return
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [installed, protocol])

  useTickEffect(refreshTick, () => {
    if (installed === true) load()
  })

  const restart = async () => {
    try {
      await call(restartFn)
      message.success(`${PROTOCOL_LABEL[protocol]} 服务已重启`)
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  if (installed === false) {
    return (
      <div className="glass-card p-4">
        <Empty
          description={`${PROTOCOL_LABEL[protocol]} 协议未安装。请在「共享管理」页面切换到 ${PROTOCOL_LABEL[protocol]} Tab 按引导安装 IIS 角色。`}
        />
      </div>
    )
  }

  if (installed === null) {
    // 协议能力检测中
    return (
      <div className="glass-card p-4">
        <Spin tip={`正在检测 ${PROTOCOL_LABEL[protocol]} 协议...`} />
      </div>
    )
  }

  return (
    <Spin spinning={loading}>
      <div className="glass-card p-4">
        <Space>
          <Popconfirm title={`重启 ${SERVICE_NAME[protocol]} 服务？`} onConfirm={restart}>
            <Button icon={<PoweroffOutlined />}>重启服务</Button>
          </Popconfirm>
          <Button icon={<ReloadOutlined />} onClick={load}>
            刷新
          </Button>
        </Space>
        {svc && (
          <Descriptions
            className="mt-4"
            size="small"
            column={2}
            items={[
              {
                key: 'st',
                label: '服务状态',
                children: <Tag color={svc.status === 'Running' ? 'green' : 'red'}>{svc.status}</Tag>
              },
              { key: 'srt', label: '启动类型', children: svc.startType || '-' },
              { key: 'sn', label: '服务名', children: SERVICE_NAME[protocol] }
            ]}
          />
        )}
        <div className="mt-3 text-xs text-fog">
          {PROTOCOL_LABEL[protocol]} 基于 IIS，站点级配置（端口/SSL/认证/授权规则）请在「共享管理」页对单个站点编辑。
        </div>
      </div>
    </Spin>
  )
}
