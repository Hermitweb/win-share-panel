import { useEffect, useState } from 'react'
import { Alert, Button, App, Space, Tag } from 'antd'
import { api, call } from '../api'
import { useUiStore } from '../stores/uiStore'
import type { Protocol, ProtocolFeatureState } from '../types'

interface Props {
  // 强制显示指定协议的安装引导；不传则按 uiStore.activeProtocol 自动判断
  protocol?: Protocol
  // 挂载时强制重新检测一次（即使 store 中已有缓存），用于共享管理页等需要展示最新细化检测结果的场景
  refreshOnMount?: boolean
}

// 协议能力探测 + 未安装引导
// 挂载时检测一次（如果 store 中未缓存），并在当前选中协议未安装时显示安装引导
// 当选中「全部」时，列出所有未安装协议的安装引导
// 传 protocol prop 时，强制显示该协议的引导（用于 Settings 面板等无 activeProtocol 场景）
// 传 refreshOnMount 时，即使已有缓存也会重新检测，确保显示最新的细化安装状态
export default function ProtocolCapabilityBanner({ protocol, refreshOnMount = false }: Props = {}) {
  const { message } = App.useApp()
  const activeProtocol = useUiStore((s) => s.activeProtocol)
  const protocolCaps = useUiStore((s) => s.protocolCaps)
  const setProtocolCaps = useUiStore((s) => s.setProtocolCaps)
  const [installing, setInstalling] = useState<Protocol | null>(null)

  useEffect(() => {
    // refreshOnMount=true 时强制重新检测；否则仅在无缓存时检测
    if (protocolCaps && !refreshOnMount) return
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
    // 仅在挂载时执行：refreshOnMount 触发一次刷新，避免 protocolCaps 变化导致循环
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  if (!protocolCaps) return null

  const handleInstall = async (proto: Protocol) => {
    setInstalling(proto)
    try {
      await call(() => api.protocol.install(proto))
      message.success('安装成功，可能需要重启系统')
      const result = await call(api.protocol.detect)
      setProtocolCaps(result)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setInstalling(null)
    }
  }

  // 渲染协议检测状态徽标（IIS/角色/服务细分状态）
  const renderStatusBadges = (cap: ProtocolFeatureState) => {
    if (cap.installed) return null
    const badges: { label: string; color: string }[] = []
    // installType 为 iis-role 的协议（FTP/WebDAV）显示 IIS 依赖状态
    if (cap.installType === 'iis-role') {
      // 通过 installHint 内容推断细分状态（detect.ts 已生成细化提示）
      const hint = cap.installHint || ''
      if (/IIS.*已安装|IIS 基础.*已安装/.test(hint)) {
        badges.push({ label: 'IIS 已装', color: 'green' })
        badges.push({ label: `${cap.protocol.toUpperCase()} 角色未装`, color: 'orange' })
      } else if (/IIS.*未安装|IIS.*均未安装/.test(hint)) {
        badges.push({ label: 'IIS 未装', color: 'red' })
      }
      if (/服务未运行|ftpsvc.*未运行|W3SVC.*未运行/.test(hint)) {
        badges.push({ label: '服务已停', color: 'volcano' })
      }
    }
    // 服务状态
    if (cap.serviceStatus === 'Stopped' && cap.installed) {
      badges.push({ label: '服务已停', color: 'volcano' })
    }
    if (badges.length === 0) return null
    return (
      <div className="mt-1">
        <Space size={4} wrap>
          {badges.map((b, i) => (
            <Tag key={i} color={b.color} style={{ margin: 0 }}>
              {b.label}
            </Tag>
          ))}
        </Space>
      </div>
    )
  }

  const renderSingle = (proto: Protocol) => {
    const cap = protocolCaps![proto]
    if (!cap || cap.installed) return null
    return (
      <Alert
        type="info"
        showIcon
        className="mb-3"
        message={`${proto.toUpperCase()} 协议未安装`}
        description={
          <div>
            <div className="text-sm">{cap.installHint}</div>
            {renderStatusBadges(cap)}
          </div>
        }
        action={
          cap.installCommand ? (
            <Button
              size="small"
              type="primary"
              loading={installing === proto}
              onClick={() => handleInstall(proto)}
            >
              安装
            </Button>
          ) : undefined
        }
      />
    )
  }

  // 强制指定协议：直接显示该协议引导
  if (protocol) {
    return renderSingle(protocol)
  }

  // 选中具体协议（非 all）且未安装时显示单条引导
  if (activeProtocol !== 'all') {
    return renderSingle(activeProtocol as Protocol)
  }

  // 选中「全部」时，列出所有未安装协议
  const uninstalled = (['nfs', 'ftp', 'webdav'] as Protocol[]).filter(
    (p) => !protocolCaps[p]?.installed
  )
  if (uninstalled.length === 0) return null

  return (
    <Alert
      type="info"
      showIcon
      className="mb-3"
      message={`检测到 ${uninstalled.length} 个协议未安装`}
      description={
        <Space direction="vertical" size={6}>
          {uninstalled.map((p) => {
            const cap = protocolCaps[p]
            return (
              <div key={p} className="flex items-start gap-2">
                <span className="font-medium min-w-[60px]">{p.toUpperCase()}</span>
                <div className="flex-1">
                  <div className="text-xs text-fog">{cap?.installHint}</div>
                  {cap && renderStatusBadges(cap)}
                </div>
                {cap?.installCommand && (
                  <Button
                    size="small"
                    type="primary"
                    loading={installing === p}
                    onClick={() => handleInstall(p)}
                  >
                    安装
                  </Button>
                )}
              </div>
            )
          })}
        </Space>
      }
    />
  )
}

