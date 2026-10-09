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
  Input,
  InputNumber,
  Collapse,
} from 'antd'
import {
  ReloadOutlined,
  PoweroffOutlined,
  UndoOutlined,
  CaretRightOutlined,
  PauseOutlined,
} from '@ant-design/icons'
import { api, call } from '../api'
import type { NfsServerConfig } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'
import { useEnsureProtocolCaps } from '../hooks/useEnsureProtocolCaps'
import ProtocolCapabilityBanner from './ProtocolCapabilityBanner'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import ConfigPresetBar from './ConfigPresetBar'
import { NFS_CONFIG_PRESETS } from '../utils/configPresets'

// NFS 服务器配置 + 服务控制
// 仅在已安装 NFS 角色时可用；未安装时显示降级提示
export default function NfsSettingsPanel() {
  const { message } = App.useApp()
  const [saving, setSaving] = useState(false)
  const [detectFailed, setDetectFailed] = useState(false)
  const [form] = Form.useForm()

  const refreshTick = useUiStore((s) => s.refreshTick)
  const protocolCaps = useUiStore((s) => s.protocolCaps)

  // B1：数据层 react-query；安装状态纯推导；load=invalidate
  const queryClient = useQueryClient()
  // 协议探测（统一入口）：store 无缓存时挂载探测（避免依赖 Shares 页懒加载），失败经 detectFailed 降级
  useEnsureProtocolCaps({ onDetectFailure: () => setDetectFailed(true) })
  const installed = detectFailed ? false : protocolCaps ? !!protocolCaps.nfs?.installed : null

  const { data, isFetching, error } = useQuery({
    queryKey: ['nfs-settings'],
    queryFn: async () => {
      const [c, s] = await Promise.all([api.nfs.getConfig(), api.nfs.serviceStatus()])
      return { c, s }
    },
    // 仅在确认已安装时拉取，避免未装时触发 nfs:getConfig 错误
    enabled: installed === true,
  })
  const svc = data?.s ?? null
  const loading = isFetching
  const load = () => {
    void queryClient.invalidateQueries({ queryKey: ['nfs-settings'] }).catch(() => {})
  }

  useEffect(() => {
    if (error && !data && installed === true) message.error((error as Error).message)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- error/data 引用稳定于单次状态迁移
  }, [error, data, installed])

  useEffect(() => {
    if (data) form.setFieldsValue(data.c)
  }, [data, form])

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

  const startSvc = async () => {
    try {
      await call(api.nfs.start)
      message.success('NFS 服务已启动')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const stopSvc = async () => {
    try {
      await call(api.nfs.stop)
      message.success('NFS 服务已停止')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const restoreDefault = async () => {
    try {
      const def = (await call(api.nfs.restoreDefault)) as NfsServerConfig
      message.success('已恢复默认配置')
      form.setFieldsValue(def)
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  if (installed === false) {
    return (
      <div className="glass-card p-4">
        <ProtocolCapabilityBanner protocol="nfs" />
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
        <ConfigPresetBar
          presets={NFS_CONFIG_PRESETS}
          onApply={async (p) => {
            await call(() => api.nfs.setConfig(p.values as Partial<NfsServerConfig>))
            load()
          }}
        />
        <Form form={form} layout="vertical">
          <div className="text-sm font-medium mb-2 text-fog">基础配置</div>
          <div className="flex flex-wrap gap-6 mb-3">
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
          </div>

          <Collapse
            size="small"
            className="mb-3"
            items={[
              {
                key: 'conn',
                label: '连接与超时',
                children: (
                  <div className="flex flex-wrap gap-6">
                    <Form.Item name="tcpConnectionTimeout" label="TCP 连接超时(秒)">
                      <InputNumber min={0} max={65535} />
                    </Form.Item>
                    <Form.Item name="udpConnectionTimeout" label="UDP 连接超时(秒)">
                      <InputNumber min={0} max={65535} />
                    </Form.Item>
                    <Form.Item name="restartConnectionTimeout" label="重启连接超时(秒)">
                      <InputNumber min={0} max={65535} />
                    </Form.Item>
                    <Form.Item
                      name="maxConcurrentConnectionsPerUser"
                      label="每用户最大并发连接(0=不限)"
                    >
                      <InputNumber min={0} max={65535} />
                    </Form.Item>
                    <Form.Item name="directoryCacheExpiry" label="目录缓存过期(秒)">
                      <InputNumber min={0} max={65535} />
                    </Form.Item>
                  </div>
                ),
              },
              {
                key: 'readonly',
                label: '身份映射 / 网关信息（只读）',
                children: (
                  <div className="flex flex-wrap gap-6">
                    <Form.Item name="anonymousUid" label="匿名 UID">
                      <InputNumber disabled style={{ width: 160 }} />
                    </Form.Item>
                    <Form.Item name="anonymousGid" label="匿名 GID">
                      <InputNumber disabled style={{ width: 160 }} />
                    </Form.Item>
                    <Form.Item name="gatewayCharacterSet" label="网关字符集">
                      <Input disabled style={{ width: 160 }} />
                    </Form.Item>
                    <Form.Item name="protocolVersion" label="协议版本">
                      <Input disabled style={{ width: 160 }} />
                    </Form.Item>
                  </div>
                ),
              },
            ]}
          />

          <Space className="mt-4 flex-wrap">
            <Button type="primary" loading={saving} onClick={save}>
              保存配置
            </Button>
            <Popconfirm
              title="确认恢复 NFS 默认配置？"
              okText="恢复默认"
              okType="danger"
              cancelText="取消"
              onConfirm={restoreDefault}
            >
              <Button icon={<UndoOutlined />} danger>
                恢复默认
              </Button>
            </Popconfirm>
            <Popconfirm title="重启 NfsService 服务？" onConfirm={restart}>
              <Button icon={<PoweroffOutlined />}>重启服务</Button>
            </Popconfirm>
            {svc?.status === 'Stopped' ? (
              <Button icon={<CaretRightOutlined />} onClick={startSvc}>
                启动
              </Button>
            ) : (
              <Popconfirm title="停止 NfsService 服务？" onConfirm={stopSvc}>
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
                  children: (
                    <Tag color={svc.status === 'Running' ? 'green' : 'red'}>{svc.status}</Tag>
                  ),
                },
                { key: 'srt', label: '启动类型', children: svc.startType || '-' },
                { key: 'sn', label: '服务名', children: svc.name },
              ]}
            />
          )}
          <div className="mt-3 text-xs text-fog">
            NFS
            服务器配置修改后通常即时生效，部分参数需重启服务。客户端能力检测与共享管理见「共享管理」页。
          </div>
        </Form>
      </div>
    </Spin>
  )
}
