import { useState } from 'react'
import { App, Alert, Button, Space, Upload } from 'antd'
import { DownloadOutlined, UploadOutlined } from '@ant-design/icons'
import type { UploadProps } from 'antd'
import { api, call } from '../api'
import { useAppStore } from '../stores/appStore'

/**
 * 「设置整体导入导出」卡片。
 *
 * 导出的**边界必须让用户看见**，否则他会以为共享配置也在里面：
 * 只含应用偏好与运维规则（主题 / 新手模式 / 置顶 / 告警规则 / 自启意图）；
 * 不含共享配置、权限模板、操作台账与趋势历史——那几类各有自己的导出入口。
 *
 * 导入后必须 `hydrate()`：设置的真源在主进程 `appstate.json`，渲染层 appStore 只是镜像。
 * 不重新水合，界面会继续显示导入前的旧值——「导入了但没变化」的经典假象。
 */
export default function SettingsTransferCard() {
  const { message } = App.useApp()
  const hydrate = useAppStore((s) => s.hydrate)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [applied, setApplied] = useState<string[] | null>(null)

  const doExport = async () => {
    setBusy(true)
    setError(null)
    try {
      const json = await call(() => api.state.exportAll())
      const blob = new Blob([json], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `winshare-settings-${new Date().toISOString().slice(0, 10)}.json`
      a.click()
      URL.revokeObjectURL(url)
      message.success('设置已导出')
    } catch (e) {
      setError((e as Error).message)
      message.error((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const doImport = async (text: string) => {
    setBusy(true)
    setError(null)
    setApplied(null)
    try {
      const r = await call(() => api.state.importAll(text))
      // 真源在主进程：必须重新水合，否则界面还是旧值
      await hydrate()
      setApplied(r.applied)
      message.success(`已应用 ${r.applied.length} 项设置`)
    } catch (e) {
      setError((e as Error).message)
      message.error((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const uploadProps: UploadProps = {
    accept: '.json,application/json',
    showUploadList: false,
    beforeUpload: async (file) => {
      const text = await file.text()
      await doImport(text)
      // 这是本地文件，不经网络：阻止 antd 自己的上传动作
      return false
    },
  }

  return (
    <div className="glass-card p-4 mb-3" data-testid="settings-transfer">
      <div className="flex items-center justify-between mb-2">
        <span className="text-sm font-medium">设置整体导入导出</span>
        <Space>
          <Upload {...uploadProps}>
            <Button size="small" loading={busy} icon={<UploadOutlined />}>
              导入设置
            </Button>
          </Upload>
          <Button
            size="small"
            type="primary"
            loading={busy}
            icon={<DownloadOutlined />}
            onClick={doExport}
          >
            导出设置
          </Button>
        </Space>
      </div>

      {error && (
        <Alert
          type="error"
          showIcon
          message="操作失败"
          description={<div className="text-xs break-all">{error}</div>}
        />
      )}

      {applied && (
        <Alert
          type="success"
          showIcon
          message={`已应用 ${applied.length} 项设置`}
          description={<div className="text-xs break-all">生效字段：{applied.join('、')}</div>}
        />
      )}

      <div className="text-xs text-fog">
        导出内容：主题、新手/专家模式、置顶共享、告警规则、开机自启意图。
        <br />
        <b>不含</b>共享配置、权限模板、操作台账与连接趋势——那几类请在各自页面单独导出。
        导入会覆盖上述偏好项。
      </div>
    </div>
  )
}
