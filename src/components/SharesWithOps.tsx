import { lazy, Suspense, useState } from 'react'
import { Button, Space, Tooltip } from 'antd'
import { AimOutlined, HistoryOutlined } from '@ant-design/icons'
import Shares from '../pages/Shares'
import JournalDrawer from './JournalDrawer'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'
import type { Share } from '../types'

/**
 * 诊断向导：懒加载。
 *
 * 必须与 pages/Settings.tsx 一样走**动态** import —— 两处都是动态 import 时，Vite 才能把
 * DiagnoseModal 真正切成独立 chunk。若这里改成静态 import，构建会报
 * "dynamically imported by Settings.tsx but also statically imported by SharesWithOps.tsx"，
 * 动态导入被静默失效，模块被拖进首屏主 chunk（实测主 chunk 6.28 MB）。
 */
const DiagnoseModal = lazy(() => import('./DiagnoseModal'))

/**
 * 共享页的批1 入口集成层（captain 挂载）。
 *
 * 为什么单独一层：Shares.tsx 通过 toolbarSlots / rowActions 暴露插槽，自己不反向依赖
 * 回收站与诊断实现——这样页面可被 props 驱动、也能单测；入口与抽屉的生命周期收敛在这里。
 * 命令面板的意图走 uiStore 的 tick（与既有 hotkey 意图同一模式），避免全局单例挂弹窗。
 */
export default function SharesWithOps() {
  const [journalOpen, setJournalOpen] = useState(false)
  const [diagnoseOpen, setDiagnoseOpen] = useState(false)
  // 诊断可带目标共享：从行内「诊断此共享」进入时预选该共享
  const [diagnoseShare, setDiagnoseShare] = useState<string | undefined>(undefined)

  useTickEffect(
    useUiStore((s) => s.journalTick),
    () => setJournalOpen(true),
  )
  useTickEffect(
    useUiStore((s) => s.diagnoseTick),
    () => {
      setDiagnoseShare(undefined) // 命令面板是全局体检语义，不带共享
      setDiagnoseOpen(true)
    },
  )

  const openDiagnoseFor = (s: Share) => {
    setDiagnoseShare(s.name)
    setDiagnoseOpen(true)
  }

  return (
    <>
      <Shares
        toolbarSlots={
          <Space>
            <Tooltip title="按删除前快照恢复共享 / 回滚权限变更">
              <Button icon={<HistoryOutlined />} onClick={() => setJournalOpen(true)}>
                操作回收站
              </Button>
            </Tooltip>
            <Tooltip title="排查「别人连不上我的共享」：服务/端口/路径/NTFS/权限/防火墙逐项体检">
              <Button
                icon={<AimOutlined />}
                onClick={() => {
                  setDiagnoseShare(undefined)
                  setDiagnoseOpen(true)
                }}
              >
                一键诊断
              </Button>
            </Tooltip>
          </Space>
        }
        rowActions={(s: Share) => (
          <Tooltip title="诊断此共享">
            <Button
              size="small"
              aria-label={`诊断 ${s.name}`}
              icon={<AimOutlined />}
              onClick={() => openDiagnoseFor(s)}
            />
          </Tooltip>
        )}
      />
      <JournalDrawer open={journalOpen} onClose={() => setJournalOpen(false)} />
      {/* Suspense 边界常驻：组件保持"常驻挂载 + open 受控"语义（关闭动画、open 转换时的状态
          重置均由 DiagnoseModal 内部负责），被延迟的只是模块字节本身。 */}
      <Suspense fallback={null}>
        <DiagnoseModal
          open={diagnoseOpen}
          onClose={() => setDiagnoseOpen(false)}
          initialShareName={diagnoseShare}
        />
      </Suspense>
    </>
  )
}
