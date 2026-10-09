import { Collapse, Form, Input, InputNumber, Spin, Switch } from 'antd'
import { api, call } from '../api'
import type { NfsServerConfig } from '../types'
import { useProtocolSettings } from '../hooks/useProtocolSettings'
import ProtocolCapabilityBanner from './ProtocolCapabilityBanner'
import ConfigPresetBar from './ConfigPresetBar'
import { ProtocolSettingsShell } from './ProtocolSettingsShell'
import { NFS_CONFIG_PRESETS } from '../utils/configPresets'

// NFS 服务器配置 + 服务控制
// 仅在已安装 NFS 角色时可用；未安装时显示降级提示
// 共享逻辑（能力门控/配置读取/保存/服务启停/恢复默认/刷新 tick）见 useProtocolSettings
export default function NfsSettingsPanel() {
  const settings = useProtocolSettings<NfsServerConfig>({
    protocol: 'nfs',
    api: {
      getConfig: api.nfs.getConfig,
      serviceStatus: api.nfs.serviceStatus,
      setConfig: api.nfs.setConfig,
      restoreDefault: api.nfs.restoreDefault,
      restart: api.nfs.restart,
      start: api.nfs.start,
      stop: api.nfs.stop,
    },
    texts: {
      saved: '已保存',
      restarted: 'NFS 服务已重启',
      started: 'NFS 服务已启动',
      stopped: 'NFS 服务已停止',
      restored: '已恢复默认配置',
    },
  })

  if (settings.installed === false) {
    return (
      <div className="glass-card p-4">
        <ProtocolCapabilityBanner protocol="nfs" />
      </div>
    )
  }

  if (settings.installed === null) {
    // 协议能力检测中
    return (
      <div className="glass-card p-4">
        <Spin tip="正在检测 NFS 协议..." />
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
        restoreConfirmTitle: '确认恢复 NFS 默认配置？',
        restart: settings.restart,
        restartConfirmTitle: '重启 NfsService 服务？',
        start: settings.start,
        stop: settings.stop,
        stopConfirmTitle: '停止 NfsService 服务？',
        load: settings.load,
      }}
      header={
        <ConfigPresetBar
          presets={NFS_CONFIG_PRESETS}
          onApply={async (p) => {
            await call(() => api.nfs.setConfig(p.values as Partial<NfsServerConfig>))
            settings.load()
          }}
        />
      }
      footer="NFS 服务器配置修改后通常即时生效，部分参数需重启服务。客户端能力检测与共享管理见「共享管理」页。"
    >
      <Form form={settings.form} layout="vertical">
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
      </Form>
    </ProtocolSettingsShell>
  )
}
