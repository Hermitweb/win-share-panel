import { useEffect, useRef } from 'react'
import { Alert } from 'antd'
import { api, call } from '../api'
import { useUiStore } from '../stores/uiStore'
import type { Protocol } from '../types'

/**
 * 顶栏健康告警条（多协议）：
 * - 每 30s 轮询 api.system.health() + api.smb.serviceStatus() + api.protocol.detect()
 * - 检测结果写入 uiStore.protocolCaps，供 ProtocolCapabilityBanner 复用
 * - 已安装但服务异常的协议（SMB/NFS/FTP/WebDAV）汇总为红色 banner
 * - 全部正常时不渲染（return null），避免挤压布局
 */
const POLL_INTERVAL = 30000

interface Issue {
  protocol: string
  detail: string
}

export default function HealthBar() {
  const health = useUiStore((s) => s.health)
  const setHealth = useUiStore((s) => s.setHealth)
  const setProtocolCaps = useUiStore((s) => s.setProtocolCaps)
  const protocolCaps = useUiStore((s) => s.protocolCaps)
  const mounted = useRef(true)

  useEffect(() => {
    mounted.current = true
    const tick = async () => {
      try {
        // 并行拉取 SMB 健康态 + 协议探测（含各协议服务状态）
        const [h, svc, caps] = await Promise.all([
          call(api.system.health),
          call(api.smb.serviceStatus),
          call(api.protocol.detect).catch(() => null)
        ])
        if (!mounted.current) return
        setHealth({
          ok: h.ok,
          detail: h.detail,
          serviceStatus: svc,
          checkedAt: Date.now()
        })
        if (caps) setProtocolCaps(caps)
      } catch {
        // 静默，下次轮询再试
      }
    }
    tick()
    const id = window.setInterval(tick, POLL_INTERVAL)
    return () => {
      mounted.current = false
      window.clearInterval(id)
    }
  }, [setHealth, setProtocolCaps])

  if (!health) return null

  const issues: Issue[] = []
  const svcRunning = health.serviceStatus?.status === 'Running'
  if (!svcRunning && health.serviceStatus) {
    issues.push({ protocol: 'SMB', detail: `LanmanServer 服务异常：${health.serviceStatus.status}` })
  } else if (!health.ok) {
    issues.push({ protocol: 'SMB', detail: health.detail || 'SMB 健康检查失败' })
  }

  // 已安装的非 SMB 协议若服务停止，纳入告警
  if (protocolCaps) {
    ;(['nfs', 'ftp', 'webdav'] as Protocol[]).forEach((p) => {
      const cap = protocolCaps[p]
      if (cap?.installed && cap.serviceStatus === 'Stopped') {
        issues.push({ protocol: p.toUpperCase(), detail: `${p.toUpperCase()} 服务未运行（${cap.serviceName}）` })
      }
    })
  }

  if (issues.length === 0) return null

  const message = issues.map((i) => `${i.protocol} 服务异常`).join(' · ')
  const description = issues.map((i) => i.detail).join('；')

  return (
    <Alert
      showIcon
      type="error"
      banner
      message={message}
      description={description}
      className="rounded-none border-x-0 border-t-0"
    />
  )
}
