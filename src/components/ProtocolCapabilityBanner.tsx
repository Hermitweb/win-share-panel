import { useEffect, useState } from 'react'
import { Alert, Button, App } from 'antd'
import { api, call } from '../api'
import { useUiStore } from '../stores/uiStore'
import type { Protocol } from '../types'

// 协议能力探测 + 未安装引导
// 挂载时检测一次（如果 store 中未缓存），并在当前选中协议未安装时显示安装引导
export default function ProtocolCapabilityBanner() {
  const { message } = App.useApp()
  const activeProtocol = useUiStore((s) => s.activeProtocol)
  const protocolCaps = useUiStore((s) => s.protocolCaps)
  const setProtocolCaps = useUiStore((s) => s.setProtocolCaps)
  const [installing, setInstalling] = useState(false)

  useEffect(() => {
    if (protocolCaps) return
    let cancelled = false
    ;(async () => {
      try {
        const result = await call(api.protocol.detect)
        if (!cancelled) setProtocolCaps(result)
      } catch {
        // 检测失败静默，不影响主流程
      }
    })()
    return () => {
      cancelled = true
    }
  }, [protocolCaps, setProtocolCaps])

  // 仅在选中具体协议（非 all）且该协议未安装时显示
  if (activeProtocol === 'all' || !protocolCaps) return null
  const cap = protocolCaps[activeProtocol as Protocol]
  if (!cap || cap.installed) return null

  const handleInstall = async () => {
    setInstalling(true)
    try {
      await call(() => api.protocol.install(activeProtocol as Protocol))
      message.success('安装成功，可能需要重启系统')
      // 重新检测
      const result = await call(api.protocol.detect)
      setProtocolCaps(result)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setInstalling(false)
    }
  }

  return (
    <Alert
      type="info"
      showIcon
      className="mb-3"
      message={`${activeProtocol.toUpperCase()} 协议未安装`}
      description={cap.installHint}
      action={
        cap.installCommand ? (
          <Button size="small" type="primary" loading={installing} onClick={handleInstall}>
            安装
          </Button>
        ) : undefined
      }
    />
  )
}
