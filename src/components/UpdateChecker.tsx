import { useState } from 'react'
import { App, Alert, Button, Space, Tag } from 'antd'
import { ExportOutlined, ReloadOutlined } from '@ant-design/icons'
import { api, call } from '../api'
import { RELEASES_URL as RELEASES_PAGE } from '../utils/appMeta'
import type { UpdateCheckResult } from '../types'

/**
 * 「检查更新」卡片 —— 只检查与提示，**不自动下载安装**。
 *
 * 为什么把失败路径当重点：本机实测 api.github.com 不可达（受限网络在国内很常见）。
 * 这里最不能犯的错，是把「检查失败」显示成「已是最新」——那是最有欺骗性的一种绿。
 * 失败时必须给：人话原因 + 重试 + 打开下载页（用户仍能自己去取新版）。
 *
 * 失败原因同时落在**卡片内**（Alert，不会消失）与 message（即时提示）：
 * 对齐本页既有约定「失败在卡片内 Alert 显示原因 + 重试，不静默降级」。
 *
 * 打开外链走主进程的 `system:openExternal`（只放行 http/https），
 * 不用 window.open：本窗口是 frame:false + sandbox 的受控窗口，
 * 且主进程没有 setWindowOpenHandler，外部导航不应由渲染层发起。
 */
export default function UpdateChecker() {
  const { message } = App.useApp()
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<UpdateCheckResult | null>(null)
  const [error, setError] = useState<string | null>(null)

  const check = async () => {
    setBusy(true)
    setError(null)
    try {
      const r = await call(() => api.update.check())
      setResult(r)
      if (r.unavailable) message.warning('检查更新失败（原因见卡片，可打开下载页手动查看）')
      else if (r.hasUpdate) message.success(`发现新版本 ${r.latest}`)
      else message.success('当前已是最新版本')
    } catch (e) {
      // 通道级异常（例如旧进程没有该通道）——照样落在卡片里，不只在瞬时 message 里
      setResult(null)
      setError((e as Error).message)
      message.error((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const openRelease = () => {
    setError(null)
    void call(() => api.system.openExternal(result?.releaseUrl ?? RELEASES_PAGE)).catch((e) => {
      setError((e as Error).message)
      message.error((e as Error).message)
    })
  }

  return (
    <div className="glass-card p-4 mb-3" data-testid="update-checker">
      <div className="flex items-center justify-between mb-2">
        <span className="text-sm font-medium">检查更新</span>
        <Space>
          <Button size="small" icon={<ExportOutlined />} onClick={openRelease}>
            打开下载页
          </Button>
          <Button
            size="small"
            type="primary"
            loading={busy}
            icon={<ReloadOutlined />}
            onClick={check}
          >
            检查更新
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

      {!error && result === null && (
        <div className="text-xs text-fog">
          尚未检查。点击「检查更新」查询 GitHub Releases 上的最新版本；只检查，不自动下载安装。
        </div>
      )}

      {result?.unavailable && (
        <Alert
          type="warning"
          showIcon
          message="检查更新失败"
          description={
            <div className="text-xs">
              <div className="break-all">{result.reason}</div>
              <div className="mt-1 text-fog">
                可直接打开下载页手动查看最新版本；或在能访问 GitHub 的网络/代理下重试。
              </div>
            </div>
          }
        />
      )}

      {result && !result.unavailable && (
        <div className="text-xs">
          <Space size="small" wrap>
            <span>
              当前版本 <b>{result.current}</b>
            </span>
            <span>
              最新版本 <b>{result.latest}</b>
            </span>
            {result.hasUpdate ? <Tag color="green">有新版本</Tag> : <Tag>已是最新</Tag>}
          </Space>
          {result.publishedAt && (
            <div className="mt-1 text-fog">
              发布时间：{new Date(result.publishedAt).toLocaleString()}
            </div>
          )}
          {result.hasUpdate && (
            <div className="mt-1 text-fog">
              本应用不会自动下载或安装更新，请从下载页获取安装包后手动覆盖安装。
            </div>
          )}
        </div>
      )}
    </div>
  )
}
