import { useEffect, useState } from 'react'
import {
  Form,
  Switch,
  Button,
  Tag,
  Space,
  Popconfirm,
  Descriptions,
  App,
  Spin,
  InputNumber,
  Collapse
} from 'antd'
import {
  ReloadOutlined,
  PoweroffOutlined,
  UndoOutlined,
  CaretRightOutlined,
  PauseOutlined
} from '@ant-design/icons'
import { api, call } from '../api'
import type { WebdavServerConfig, ServiceStatus } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'
import ProtocolCapabilityBanner from './ProtocolCapabilityBanner'

// WebDAV 服务器级配置 + 服务控制
// 仅在已安装 IIS + WebDAV 角色时可用；未安装时显示降级提示
export default function WebdavSettingsPanel() {
  const { message } = App.useApp()
  const [config, setConfig] = useState<Partial<WebdavServerConfig>>({})
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
        call(api.webdav.getConfig) as Promise<WebdavServerConfig>,
        call(api.webdav.serviceStatus)
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

  // 协议探测：store 中无缓存时主动 detect
  useEffect(() => {
    if (protocolCaps) {
      setInstalled(!!protocolCaps.webdav?.installed)
      return
    }
    let cancelled = false
    ;(async () => {
      try {
        const result = await call(api.protocol.detect)
        if (!cancelled) setProtocolCaps(result)
      } catch {
        if (!cancelled) setInstalled(false)
      }
    })()
    return () => {
      cancelled = true
    }
  }, [protocolCaps, setProtocolCaps])

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
      await call(() => api.webdav.setConfig(v))
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
      await call(api.webdav.restart)
      message.success('WebDAV 服务已重启')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const startSvc = async () => {
    try {
      await call(api.webdav.start)
      message.success('WebDAV 服务已启动')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const stopSvc = async () => {
    try {
      await call(api.webdav.stop)
      message.success('WebDAV 服务已停止')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const restoreDefault = async () => {
    try {
      const def = (await call(api.webdav.restoreDefault)) as WebdavServerConfig
      message.success('已恢复默认配置')
      form.setFieldsValue(def)
      setConfig(def)
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  if (installed === false) {
    return (
      <div className="glass-card p-4">
        <ProtocolCapabilityBanner protocol="webdav" />
      </div>
    )
  }

  if (installed === null) {
    return (
      <div className="glass-card p-4">
        <Spin tip="正在检测 WebDAV 协议..." />
      </div>
    )
  }

  return (
    <Spin spinning={loading}>
      <div className="glass-card p-4">
        <Form form={form} layout="vertical">
          <div className="text-sm font-medium mb-2 text-fog">WebDAV Authoring</div>
          <div className="flex flex-wrap gap-6 mb-3">
            <Form.Item name="authoringEnabled" label="启用 Authoring" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item name="authoringMaxRequestBodySize" label="最大请求体(字节, 0=不限)">
              <InputNumber min={0} max={4294967295} />
            </Form.Item>
          </div>

          <div className="text-sm font-medium mb-2 text-fog">请求筛选</div>
          <div className="flex flex-wrap gap-6 mb-3">
            <Form.Item name="maxAllowedContentLength" label="最大内容长度(字节)">
              <InputNumber min={0} max={4294967295} />
            </Form.Item>
            <Form.Item name="allowDoubleEscaping" label="允许双重转义" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item name="verifyIntegration" label="验证集成" valuePropName="checked">
              <Switch />
            </Form.Item>
          </div>

          <div className="text-sm font-medium mb-2 text-fog">认证</div>
          <div className="flex flex-wrap gap-6 mb-3">
            <Form.Item name="anonymousEnabled" label="匿名认证" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item name="basicEnabled" label="基本认证" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item name="windowsEnabled" label="Windows 认证" valuePropName="checked">
              <Switch />
            </Form.Item>
          </div>

          <Collapse
            size="small"
            className="mb-3"
            items={[
              {
                key: 'limits',
                label: '请求限制',
                children: (
                  <div className="flex flex-wrap gap-6">
                    <Form.Item name="maxUrlLength" label="最大 URL 长度">
                      <InputNumber min={0} max={65535} />
                    </Form.Item>
                    <Form.Item name="maxQueryStringLength" label="最大查询字符串长度">
                      <InputNumber min={0} max={65535} />
                    </Form.Item>
                  </div>
                )
              },
              {
                key: 'readonly',
                label: '只读信息（服务器级状态）',
                children: (
                  <Descriptions
                    size="small"
                    column={2}
                    items={[
                      {
                        key: 'rules',
                        label: '全局 Authoring 规则数',
                        children: config.globalAuthoringRulesCount ?? 0
                      },
                      {
                        key: 'sc',
                        label: '静态压缩',
                        children: (
                          <Tag color={config.enableStaticCompression ? 'green' : 'default'}>
                            {config.enableStaticCompression ? '启用' : '禁用'}
                          </Tag>
                        )
                      },
                      {
                        key: 'dc',
                        label: '动态压缩',
                        children: (
                          <Tag color={config.enableDynamicCompression ? 'green' : 'default'}>
                            {config.enableDynamicCompression ? '启用' : '禁用'}
                          </Tag>
                        )
                      },
                      {
                        key: 'ssl',
                        label: '要求 SSL',
                        children: (
                          <Tag color={config.requireSSL ? 'orange' : 'default'}>
                            {config.requireSSL ? '是' : '否'}
                          </Tag>
                        )
                      }
                    ]}
                  />
                )
              }
            ]}
          />

          <Space className="mt-4 flex-wrap">
            <Button type="primary" loading={saving} onClick={save}>
              保存配置
            </Button>
            <Popconfirm
              title="确认恢复 WebDAV 默认配置？"
              okText="恢复默认"
              okType="danger"
              cancelText="取消"
              onConfirm={restoreDefault}
            >
              <Button icon={<UndoOutlined />} danger>
                恢复默认
              </Button>
            </Popconfirm>
            <Popconfirm title="重启 W3SVC 服务？" onConfirm={restart}>
              <Button icon={<PoweroffOutlined />}>重启服务</Button>
            </Popconfirm>
            {svc?.status === 'Stopped' ? (
              <Button icon={<CaretRightOutlined />} onClick={startSvc}>
                启动
              </Button>
            ) : (
              <Popconfirm title="停止 W3SVC 服务？" onConfirm={stopSvc}>
                <Button icon={<PauseOutlined />}>停止</Button>
              </Popconfirm>
            )}
            <Button icon={<ReloadOutlined />} onClick={load}>
              刷新
            </Button>
          </Space>
          {svc && (
            <Descriptions
              className="mt-4"
              size="small"
              column={3}
              items={[
                {
                  key: 'st',
                  label: '服务状态',
                  children: <Tag color={svc.status === 'Running' ? 'green' : 'red'}>{svc.status}</Tag>
                },
                { key: 'srt', label: '启动类型', children: svc.startType || '-' },
                { key: 'sn', label: '服务名', children: svc.name }
              ]}
            />
          )}
          <div className="mt-3 text-xs text-fog">
            WebDAV 服务器级配置（IIS system.webServer/* 配置节）。站点级 authoring 规则请在「共享管理」页对单个站点编辑。只读字段为服务器级状态，不可直接修改。
          </div>
        </Form>
      </div>
    </Spin>
  )
}
