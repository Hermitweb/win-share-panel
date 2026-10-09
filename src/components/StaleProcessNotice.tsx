import { useEffect } from 'react'
import { App as AntdApp, Button } from 'antd'

/**
 * 后台进程版本自检 + 升级后重启入口。
 *
 * 问题背景：本程序常驻托盘（关窗口不退）且用 requestSingleInstanceLock 防多开，
 * 于是覆盖安装 / 开发态热更新之后，旧进程会一直活着——渲染层已经是新界面，但
 * preload 与主进程还是上一版，新增 IPC 通道整组缺失。表现是点新功能报
 * "Cannot read properties of undefined (reading 'run')"，而用户以为"重启过了"。
 *
 * 这里在启动时一次性点出缺哪些通道并给出动作。关键取舍：
 * `system:relaunch` 本身就是新通道——**真正需要重启的那些旧进程连它都没有**。
 * 所以按钮只在探测到该通道时出现；探测不到就退回"托盘退出后重开"的手动指引，
 * 绝不给一个点了没反应的按钮。
 */

/** 批1 引入的通道组：缺失即说明后台是被升级落下的旧进程 */
const REQUIRED_GROUPS = ['state', 'disk', 'security', 'firewall', 'diagnose'] as const

export interface StaleProcessInfo {
  missing: string[]
  canRelaunch: boolean
}

/** 纯函数，便于单测：从注入的桥对象判定版本是否落后 */
export function inspectBridge(host: Record<string, unknown> | undefined): StaleProcessInfo {
  const missing = REQUIRED_GROUPS.filter((k) => !host?.[k])
  const system = host?.system as { relaunch?: unknown } | undefined
  return { missing, canRelaunch: typeof system?.relaunch === 'function' }
}

export interface StaleProcessNoticeProps {
  /** 覆盖桥对象（测试注入）；默认取 window.winshare */
  bridge?: Record<string, unknown> | undefined
}

export default function StaleProcessNotice({ bridge }: StaleProcessNoticeProps = {}) {
  const { message } = AntdApp.useApp()
  useEffect(() => {
    const host =
      bridge ?? (globalThis.window?.winshare as unknown as Record<string, unknown> | undefined)
    const { missing, canRelaunch } = inspectBridge(host)
    if (!missing.length) return
    message.warning({
      key: 'stale-process',
      duration: 0,
      content: (
        <div style={{ maxWidth: 420 }}>
          <div>
            后台进程仍是升级前的版本（缺少通道：{missing.join('、')}），
            一键诊断、操作回收站等新功能不可用。
          </div>
          {canRelaunch ? (
            <div style={{ marginTop: 8 }}>
              <Button
                size="small"
                type="primary"
                onClick={() => {
                  void (host?.system as { relaunch: () => Promise<void> }).relaunch()
                }}
              >
                立即重启
              </Button>
              <span style={{ marginLeft: 8 }}>退出后会自动重新打开最新版本</span>
            </div>
          ) : (
            <div style={{ marginTop: 8 }}>
              请从右下角托盘图标选择「退出」后重新启动——只关闭窗口不够，
              程序会留在托盘继续跑旧版本。
            </div>
          )}
        </div>
      ),
    })
  }, [bridge, message])
  return null
}
