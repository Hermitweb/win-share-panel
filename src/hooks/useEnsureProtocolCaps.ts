import { useEffect } from 'react'
import { api, call } from '../api'
import { useUiStore } from '../stores/uiStore'

/**
 * R-4：协议能力探测（protocol:detect）的统一入口。
 * 原 Dashboard / ProtocolCapabilityBanner / 三个 SettingsPanel 各有一份同构逻辑，
 * 且并发挂载时依赖主进程 detectProtocols 的 in-flight 去重——此处收敛为单一实现。
 *
 * - store 已有缓存则跳过探测；refreshOnMount=true 强制挂载时重探一次
 *   （用于 Banner 需要最新细化安装状态的场景）。
 * - 仅挂载时执行：protocolCaps 快照取挂载时刻，纳入依赖会因缓存写入引发循环重探。
 * - 探测失败默认静默；可传 onDetectFailure 回调（面板用于按"未装"降级渲染）。
 *   回调按挂载时闭包使用——传入引用 useState setter 等稳定函数即可（勿传每轮重建且
 *   含外部可变状态的复杂闭包）。
 */
export function useEnsureProtocolCaps(
  opts: { refreshOnMount?: boolean; onDetectFailure?: () => void } = {},
): void {
  const protocolCaps = useUiStore((s) => s.protocolCaps)
  const setProtocolCaps = useUiStore((s) => s.setProtocolCaps)
  const refreshOnMount = opts.refreshOnMount === true
  const onDetectFailure = opts.onDetectFailure

  useEffect(() => {
    if (protocolCaps && !refreshOnMount) return
    let cancelled = false
    ;(async () => {
      try {
        const result = await call(api.protocol.detect)
        if (!cancelled) setProtocolCaps(result)
      } catch {
        if (!cancelled) onDetectFailure?.()
      }
    })()
    return () => {
      cancelled = true
    }
    // 有意仅挂载时执行一次：缓存快照与强制刷新标志在挂载时定型，
    // protocolCaps/setProtocolCaps/onDetectFailure 纳入依赖会造成循环探测
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
}
