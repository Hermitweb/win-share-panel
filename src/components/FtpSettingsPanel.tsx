import { Collapse, Form, Input, InputNumber, Select, Spin, Switch } from 'antd'
import { api, call } from '../api'
import type { FtpServerConfig } from '../types'
import { useProtocolSettings } from '../hooks/useProtocolSettings'
import ProtocolCapabilityBanner from './ProtocolCapabilityBanner'
import ConfigPresetBar from './ConfigPresetBar'
import { ProtocolSettingsShell } from './ProtocolSettingsShell'
import { FTP_CONFIG_PRESETS } from '../utils/configPresets'

const SSL_POLICY_OPTIONS = [
  { label: '允许（不强制）', value: 'SslAllow' },
  { label: '要求', value: 'SslRequire' },
  { label: '要求证书', value: 'SslRequireCredentials' },
]

const ISOLATION_OPTIONS = [
  { label: '无隔离', value: 'None' },
  { label: '起始用户目录', value: 'StartInUsersDirectory' },
  { label: '隔离用户', value: 'IsolateUsers' },
  { label: '隔离（无 AD）', value: 'IsolateUsersWithoutAD' },
  { label: 'Active Directory', value: 'ActiveDirectory' },
]

const LOG_PERIOD_OPTIONS = [
  { label: '每小时', value: 'Hourly' },
  { label: '每天', value: 'Daily' },
  { label: '每周', value: 'Weekly' },
  { label: '每月', value: 'Monthly' },
  { label: '按大小', value: 'MaxSize' },
  { label: '从不', value: 'Never' },
]

// FTP 服务器级配置 + 服务控制
// 仅在已安装 FTP 角色服务时可用；未安装时显示降级提示
// 共享逻辑（能力门控/配置读取/保存/服务启停/恢复默认/刷新 tick）见 useProtocolSettings
export default function FtpSettingsPanel() {
  const settings = useProtocolSettings<FtpServerConfig>({
    protocol: 'ftp',
    api: {
      getConfig: api.ftp.getConfig,
      serviceStatus: api.ftp.serviceStatus,
      setConfig: api.ftp.setConfig,
      restoreDefault: api.ftp.restoreDefault,
      restart: api.ftp.restart,
      start: api.ftp.start,
      stop: api.ftp.stop,
    },
    texts: {
      saved: '已保存',
      restarted: 'FTP 服务已重启',
      started: 'FTP 服务已启动',
      stopped: 'FTP 服务已停止',
      restored: '已恢复默认配置',
    },
  })

  if (settings.installed === false) {
    return (
      <div className="glass-card p-4">
        <ProtocolCapabilityBanner protocol="ftp" />
      </div>
    )
  }

  if (settings.installed === null) {
    return (
      <div className="glass-card p-4">
        <Spin tip="正在检测 FTP 协议..." />
      </div>
    )
  }

  return (
    <ProtocolSettingsShell
      loading={settings.loading}
      service={settings.service}
      actions={{
        saving: settings.saving,
        save: settings.save,
        restoreDefault: settings.restoreDefault,
        restoreConfirmTitle: '确认恢复 FTP 默认配置？',
        restart: settings.restart,
        restartConfirmTitle: '重启 ftpsvc 服务？',
        start: settings.start,
        stop: settings.stop,
        stopConfirmTitle: '停止 ftpsvc 服务？',
        load: settings.load,
      }}
      header={
        <ConfigPresetBar
          presets={FTP_CONFIG_PRESETS}
          onApply={async (p) => {
            await call(() => api.ftp.setConfig(p.values as Partial<FtpServerConfig>))
            settings.load()
          }}
        />
      }
      footer="FTP 服务器级配置（IIS ftpServer/* 配置节）。站点级配置（端口/路径/授权）请在「共享管理」页对单个站点编辑。部分配置节可能因 IIS 锁定而写入失败。"
    >
      <Form form={settings.form} layout="vertical">
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
              ),
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
              ),
            },
          ]}
        />
      </Form>
    </ProtocolSettingsShell>
  )
}
