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
  Select,
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
import type { FtpServerConfig, ServiceStatus } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'
import ProtocolCapabilityBanner from './ProtocolCapabilityBanner'

const SSL_POLICY_OPTIONS = [
  { label: '允许（不强制）', value: 'SslAllow' },
  { label: '要求', value: 'SslRequire' },
  { label: '要求证书', value: 'SslRequireCredentials' }
]

const ISOLATION_OPTIONS = [
  { label: '无隔离', value: 'None' },
  { label: '起始用户目录', value: 'StartInUsersDirectory' },
  { label: '隔离用户', value: 'IsolateUsers' },
  { label: '隔离（无 AD）', value: 'IsolateUsersWithoutAD' },
  { label: 'Active Directory', value: 'ActiveDirectory' }
]

const LOG_PERIOD_OPTIONS = [
  { label: '每小时', value: 'Hourly' },
  { label: '每天', value: 'Daily' },
  { label: '每周', value: 'Weekly' },
  { label: '每月', value: 'Monthly' },
  { label: '按大小', value: 'MaxSize' },
  { label: '从不', value: 'Never' }
]

// FTP 服务器级配置 + 服务控制
// 仅在已安装 FTP 角色服务时可用；未安装时显示降级提示
export default function FtpSettingsPanel() {
  const { message } = App.useApp()
  const [config, setConfig] = useState<Partial<FtpServerConfig>>({})
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
        call(api.ftp.getConfig) as Promise<FtpServerConfig>,
        call(api.ftp.serviceStatus)
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
      setInstalled(!!protocolCaps.ftp?.installed)
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
      await call(() => api.ftp.setConfig(v))
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
      await call(api.ftp.restart)
      message.success('FTP 服务已重启')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const startSvc = async () => {
    try {
      await call(api.ftp.start)
      message.success('FTP 服务已启动')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const stopSvc = async () => {
    try {
      await call(api.ftp.stop)
      message.success('FTP 服务已停止')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const restoreDefault = async () => {
    try {
      const def = (await call(api.ftp.restoreDefault)) as FtpServerConfig
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
        <ProtocolCapabilityBanner protocol="ftp" />
      </div>
    )
  }

  if (installed === null) {
    return (
      <div className="glass-card p-4">
        <Spin tip="正在检测 FTP 协议..." />
      </div>
    )
  }

  return (
    <Spin spinning={loading}>
      <div className="glass-card p-4">
        <Form form={form} layout="vertical">
          <div className="text-sm font-medium mb-2 text-fog">SSL / 安全</div>
          <div className="flex flex-wrap gap-6 mb-3">
            <Form.Item name="sslControlChannelPolicy" label="控制通道 SSL">
              <Select style={{ width: 180 }} options={SSL_POLICY_OPTIONS} />
            </Form.Item>
            <Form.Item name="sslDataChannelPolicy" label="数据通道 SSL">
              <Select style={{ width: 180 }} options={SSL_POLICY_OPTIONS} />
            </Form.Item>
            <Form.Item name="sslServerCertHash" label="SSL 证书哈希(SHA-1)">
              <Input style={{ width: 320 }} placeholder="留空表示未配置" />
            </Form.Item>
            <Form.Item name="sslClientCertRequired" label="要求客户端证书" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item name="ssl128" label="强制 128 位 SSL" valuePropName="checked">
              <Switch />
            </Form.Item>
          </div>

          <div className="text-sm font-medium mb-2 text-fog">认证</div>
          <div className="flex flex-wrap gap-6 mb-3">
            <Form.Item name="anonymousEnabled" label="匿名认证" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item name="anonymousUserName" label="匿名用户名">
              <Input style={{ width: 200 }} />
            </Form.Item>
            <Form.Item name="basicEnabled" label="基本认证" valuePropName="checked">
              <Switch />
            </Form.Item>
          </div>

          <div className="text-sm font-medium mb-2 text-fog">被动模式端口范围（防火墙支持）</div>
          <div className="flex flex-wrap gap-6 mb-3">
            <Form.Item name="firewallLowDataChannelPort" label="起始端口(0=未配置)">
              <InputNumber min={0} max={65535} />
            </Form.Item>
            <Form.Item name="firewallHighDataChannelPort" label="结束端口(0=未配置)">
              <InputNumber min={0} max={65535} />
            </Form.Item>
          </div>

          <Collapse
            size="small"
            className="mb-3"
            items={[
              {
                key: 'messages',
                label: '消息与目录浏览',
                children: (
                  <div className="flex flex-wrap gap-6">
                    <Form.Item name="greetingMessage" label="欢迎消息" style={{ minWidth: 280 }}>
                      <Input.TextArea rows={2} />
                    </Form.Item>
                    <Form.Item name="bannerMessage" label="横幅消息" style={{ minWidth: 280 }}>
                      <Input.TextArea rows={2} />
                    </Form.Item>
                    <Form.Item name="exitMessage" label="退出消息" style={{ minWidth: 280 }}>
                      <Input.TextArea rows={2} />
                    </Form.Item>
                    <Form.Item name="maxClientsMessage" label="超限消息" style={{ minWidth: 280 }}>
                      <Input.TextArea rows={2} />
                    </Form.Item>
                    <Form.Item
                      name="suppressDefaultMessages"
                      label="抑制默认消息"
                      valuePropName="checked"
                    >
                      <Switch />
                    </Form.Item>
                    <Form.Item name="showVirtualDirs" label="显示虚拟目录" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                  </div>
                )
              },
              {
                key: 'isolation',
                label: '用户隔离 / 超时 / 文件处理 / 日志',
                children: (
                  <div className="flex flex-wrap gap-6">
                    <Form.Item name="userIsolationMode" label="用户隔离模式">
                      <Select style={{ width: 200 }} options={ISOLATION_OPTIONS} />
                    </Form.Item>
                    <Form.Item name="unauthenticatedTimeout" label="未认证超时(秒)">
                      <InputNumber min={0} max={65535} />
                    </Form.Item>
                    <Form.Item name="controlConnectionTimeout" label="控制连接超时(秒)">
                      <InputNumber min={0} max={65535} />
                    </Form.Item>
                    <Form.Item name="dataChannelConnectionTimeout" label="数据通道超时(秒)">
                      <InputNumber min={0} max={65535} />
                    </Form.Item>
                    <Form.Item name="keepPartialUploads" label="保留部分上传" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                    <Form.Item
                      name="allowReplaceOnRename"
                      label="重命名时覆盖"
                      valuePropName="checked"
                    >
                      <Switch />
                    </Form.Item>
                    <Form.Item name="logFileDirectory" label="日志目录">
                      <Input style={{ width: 320 }} />
                    </Form.Item>
                    <Form.Item name="logFilePeriod" label="日志周期">
                      <Select style={{ width: 120 }} options={LOG_PERIOD_OPTIONS} />
                    </Form.Item>
                  </div>
                )
              }
            ]}
          />

          <Space className="mt-4 flex-wrap">
            <Button type="primary" loading={saving} onClick={save}>
              保存配置
            </Button>
            <Popconfirm
              title="确认恢复 FTP 默认配置？"
              okText="恢复默认"
              okType="danger"
              cancelText="取消"
              onConfirm={restoreDefault}
            >
              <Button icon={<UndoOutlined />} danger>
                恢复默认
              </Button>
            </Popconfirm>
            <Popconfirm title="重启 ftpsvc 服务？" onConfirm={restart}>
              <Button icon={<PoweroffOutlined />}>重启服务</Button>
            </Popconfirm>
            {svc?.status === 'Stopped' ? (
              <Button icon={<CaretRightOutlined />} onClick={startSvc}>
                启动
              </Button>
            ) : (
              <Popconfirm title="停止 ftpsvc 服务？" onConfirm={stopSvc}>
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
            FTP 服务器级配置（IIS ftpServer/* 配置节）。站点级配置（端口/路径/授权）请在「共享管理」页对单个站点编辑。部分配置节可能因 IIS 锁定而写入失败。
          </div>
        </Form>
      </div>
    </Spin>
  )
}
