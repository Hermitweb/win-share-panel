import type { ReactElement, ReactNode } from 'react'
import { Button, Descriptions, Popconfirm, Space, Spin, Tag } from 'antd'
import {
  CaretRightOutlined,
  PauseOutlined,
  PoweroffOutlined,
  ReloadOutlined,
  UndoOutlined,
} from '@ant-design/icons'
import type { ServiceStatus } from '../types'

/**
 * R-5：三协议设置面板（Nfs / Ftp / Webdav SettingsPanel）共用的展示外壳。
 * 逐字复现抽象前的 DOM：Spin 包裹 → glass-card → 头部插槽（预设条）→ 协议字段表单 →
 * 动作按钮组 → 服务状态 Descriptions → 底部说明。
 * 协议特有的降级分支（未安装/探测中）由面板自己早返回，不进入本外壳。
 */
export interface ProtocolSettingsActions {
  saving: boolean
  save: () => void
  restoreDefault: () => void
  /** '确认恢复 NFS 默认配置？' 等 */
  restoreConfirmTitle: string
  restart: () => void
  /** '重启 NfsService 服务？' / '重启 ftpsvc 服务？' 等 */
  restartConfirmTitle: string
  start: () => void
  stop: () => void
  /** '停止 NfsService 服务？' 等 */
  stopConfirmTitle: string
  load: () => void
}

export interface ProtocolSettingsShellProps {
  loading: boolean
  service: ServiceStatus | null
  actions: ProtocolSettingsActions
  /** ConfigPresetBar 等头部插槽（协议特有 presets） */
  header?: ReactNode
  /** 面板自己的 <Form form={settings.form} layout="vertical">…协议特有字段…</Form> */
  children: ReactNode
  /** 底部说明（协议特有） */
  footer: ReactNode
}

export function ProtocolSettingsShell(p: ProtocolSettingsShellProps): ReactElement {
  const a = p.actions
  return (
    <Spin spinning={p.loading}>
      <div className="glass-card p-4">
        {p.header}
        {p.children}
        <Space className="mt-4 flex-wrap">
          <Button type="primary" loading={a.saving} onClick={a.save}>
            保存配置
          </Button>
          <Popconfirm
            title={a.restoreConfirmTitle}
            okText="恢复默认"
            okType="danger"
            cancelText="取消"
            onConfirm={a.restoreDefault}
          >
            <Button icon={<UndoOutlined />} danger>
              恢复默认
            </Button>
          </Popconfirm>
          <Popconfirm title={a.restartConfirmTitle} onConfirm={a.restart}>
            <Button icon={<PoweroffOutlined />}>重启服务</Button>
          </Popconfirm>
          {p.service?.status === 'Stopped' ? (
            <Button icon={<CaretRightOutlined />} onClick={a.start}>
              启动
            </Button>
          ) : (
            <Popconfirm title={a.stopConfirmTitle} onConfirm={a.stop}>
              <Button icon={<PauseOutlined />}>停止</Button>
            </Popconfirm>
          )}
          <Button icon={<ReloadOutlined />} onClick={a.load}>
            刷新
          </Button>
        </Space>
        {p.service && (
          <Descriptions
            className="mt-4"
            size="small"
            column={3}
            items={[
              {
                key: 'st',
                label: '服务状态',
                children: (
                  <Tag color={p.service.status === 'Running' ? 'green' : 'red'}>
                    {p.service.status}
                  </Tag>
                ),
              },
              { key: 'srt', label: '启动类型', children: p.service.startType || '-' },
              { key: 'sn', label: '服务名', children: p.service.name },
            ]}
          />
        )}
        <div className="mt-3 text-xs text-fog">{p.footer}</div>
      </div>
    </Spin>
  )
}
