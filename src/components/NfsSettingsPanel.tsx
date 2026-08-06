import { useEffect, useState } from 'react'
import { Form, Switch, Button, Tag, Space, Popconfirm, Descriptions, App, Empty, Spin } from 'antd'
import { ReloadOutlined, PoweroffOutlined } from '@ant-design/icons'
import { api, call } from '../api'
import type { NfsServerConfig, ServiceStatus } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'

// NFS 服务器配置 + 服务控制
// 仅在已安装 NFS 角色时可用；未安装时显示降级提示
export default function NfsSettingsPanel() {
  const { message } = App.useApp()
  const [config, setConfig] = useState<Partial<NfsServerConfig>>({})
  const [svc, setSvc] = useState<ServiceStatus | null>(null)
  const [saving, setSaving] = useState(false)
  const [loading, setLoading] = useState(false)
  const [installed, setInstalled] = useState<boolean | null>(null)
  const [form] = Form.useForm()

  const refreshTick = useUiStore((s) => s.refreshTick)
  const protocolCaps = useUiStore((s) => s.protocolCaps)
  const setProtocolCaps = useUiStore((s) => s.setProtocolCaps)

  const load = async () => {
    setLoading(true)
    try {
      const [c, s] = await Promise.all([
        call(api.nfs.getConfig),
        call(api.nfs.serviceStatus)
      ])
      setConfig(c)
      setSvc(s)
      form.setFieldsValue(c)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  // 协议探测：store 中无缓存时主动 detect（避免依赖 Shares 页面懒加载）
  useEffect(() => {
    if (protocolCaps) {
      setInstalled(!!protocolCaps.nfs?.installed)
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
  }, [protocolCaps, setProtocolCaps])

  // 仅在明确已装时加载配置，避免未装时触发 nfs:getConfig 错误
  useEffect(() => {
    if (installed !== true) return
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [installed])

  useTickEffect(refreshTick, () => {
    if (installed === true) load()
  })

  const save = async () => {
    const v = await form.validateFields()
    setSaving(true)
    try {
      await call(() => api.nfs.setConfig(v))
      message.success('已保存')
      load()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const restart = async () => {
    try {
      await call(api.nfs.restart)
      message.success('NFS 服务已重启')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  if (installed === false) {
    return (
      <div className="glass-card p-4">
        <Empty description="NFS 协议未安装，无法配置服务器。请在「共享管理」页面切换到 NFS Tab 按引导安装。" />
      </div>
    )
  }

  if (installed === null) {
    // 协议能力检测中
    return (
      <div className="glass-card p-4">
        <Spin tip="正在检测 NFS 协议..." />
      </div>
    )
  }

  return (
    <Spin spinning={loading}>
      <div className="glass-card p-4">
        <Form form={form} layout="vertical">
          <Space wrap size="large">
            <Form.Item name="gracefulUnmount" label="优雅卸载" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item name="logActivity" label="记录活动日志" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item name="enableUnmappedAccess" label="未映射用户访问" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item
              name="enableAuthenticationRenegotiation"
              label="认证重协商"
              valuePropName="checked"
            >
              <Switch />
            </Form.Item>
          </Space>
        </Form>
        <Space className="mt-4">
          <Button type="primary" loading={saving} onClick={save}>
            保存配置
          </Button>
          <Popconfirm title="重启 NfsService 服务？" onConfirm={restart}>
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
              { key: 'srt', label: '启动类型', children: svc.startType || '-' }
            ]}
          />
        )}
        <div className="mt-3 text-xs text-fog">
          NFS 服务器配置修改后通常即时生效，部分参数需重启服务。客户端能力检测与共享管理见「共享管理」页。
        </div>
      </div>
    </Spin>
  )
}
