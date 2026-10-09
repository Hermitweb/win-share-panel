import { Collapse, Descriptions, Form, InputNumber, Spin, Switch, Tag } from 'antd'
import { api, call } from '../api'
import type { WebdavServerConfig } from '../types'
import { useProtocolSettings } from '../hooks/useProtocolSettings'
import ProtocolCapabilityBanner from './ProtocolCapabilityBanner'
import ConfigPresetBar from './ConfigPresetBar'
import { ProtocolSettingsShell } from './ProtocolSettingsShell'
import { WEBDAV_CONFIG_PRESETS } from '../utils/configPresets'

// WebDAV 服务器级配置 + 服务控制
// 仅在已安装 IIS + WebDAV 角色时可用；未安装时显示降级提示
// 共享逻辑（能力门控/配置读取/保存/服务启停/恢复默认/刷新 tick）见 useProtocolSettings
export default function WebdavSettingsPanel() {
  const settings = useProtocolSettings<WebdavServerConfig>({
    protocol: 'webdav',
    api: {
      getConfig: api.webdav.getConfig,
      serviceStatus: api.webdav.serviceStatus,
      setConfig: api.webdav.setConfig,
      restoreDefault: api.webdav.restoreDefault,
      restart: api.webdav.restart,
      start: api.webdav.start,
      stop: api.webdav.stop,
    },
    texts: {
      saved: '已保存',
      restarted: 'WebDAV 服务已重启',
      started: 'WebDAV 服务已启动',
      stopped: 'WebDAV 服务已停止',
      restored: '已恢复默认配置',
    },
  })
  // 只读字段（服务器级状态）派生值：未取到配置时按空对象渲染
  const config: Partial<WebdavServerConfig> = settings.config ?? {}

  if (settings.installed === false) {
    return (
      <div className="glass-card p-4">
        <ProtocolCapabilityBanner protocol="webdav" />
      </div>
    )
  }

  if (settings.installed === null) {
    return (
      <div className="glass-card p-4">
        <Spin tip="正在检测 WebDAV 协议..." />
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
        restoreConfirmTitle: '确认恢复 WebDAV 默认配置？',
        restart: settings.restart,
        restartConfirmTitle: '重启 W3SVC 服务？',
        start: settings.start,
        stop: settings.stop,
        stopConfirmTitle: '停止 W3SVC 服务？',
        load: settings.load,
      }}
      header={
        <ConfigPresetBar
          presets={WEBDAV_CONFIG_PRESETS}
          onApply={async (p) => {
            await call(() => api.webdav.setConfig(p.values as Partial<WebdavServerConfig>))
            settings.load() // 失效重取后 config 派生值随服务器状态刷新
          }}
        />
      }
      footer="WebDAV 服务器级配置（IIS system.webServer/* 配置节）。站点级 authoring 规则请在「共享管理」页对单个站点编辑。只读字段为服务器级状态，不可直接修改。"
    >
      <Form form={settings.form} layout="vertical">
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
              ),
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
                      children: config.globalAuthoringRulesCount ?? 0,
                    },
                    {
                      key: 'sc',
                      label: '静态压缩',
                      children: (
                        <Tag color={config.enableStaticCompression ? 'green' : 'default'}>
                          {config.enableStaticCompression ? '启用' : '禁用'}
                        </Tag>
                      ),
                    },
                    {
                      key: 'dc',
                      label: '动态压缩',
                      children: (
                        <Tag color={config.enableDynamicCompression ? 'green' : 'default'}>
                          {config.enableDynamicCompression ? '启用' : '禁用'}
                        </Tag>
                      ),
                    },
                    {
                      key: 'ssl',
                      label: '要求 SSL',
                      children: (
                        <Tag color={config.requireSSL ? 'orange' : 'default'}>
                          {config.requireSSL ? '是' : '否'}
                        </Tag>
                      ),
                    },
                  ]}
                />
              ),
            },
          ]}
        />
      </Form>
    </ProtocolSettingsShell>
  )
}
