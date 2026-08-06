import { useEffect } from 'react'
import { useUiStore } from '../stores/uiStore'

/**
 * 全局快捷键中枢：
 * - Ctrl/Cmd+K  打开命令面板
 * - Esc         关闭命令面板与共享新建弹窗
 * - F5          触发当前页刷新（写入 refreshTick，各页面订阅）
 * - Ctrl+N      仅 /shares 路由：打开新建共享 Modal
 * - Delete      /shares 有选中：批量删除；/sessions 有选中：批量断开
 * - Space       /shares 有选中：批量启停切换
 *
 * 设计：热键只写 store 意图 tick，不直接调 API。
 * 对应页面在自身 App.useApp() 上下文订阅 tick 执行 modal.confirm + API，
 * 保证 antd 主题与消息上下文正确。
 */
export function useHotkeys(): void {
  const route = useUiStore((s) => s.route)
  const selectedShares = useUiStore((s) => s.selectedShares)
  const selectedSessions = useUiStore((s) => s.selectedSessions)
  const setPaletteOpen = useUiStore((s) => s.setPaletteOpen)
  const setShareCreateOpen = useUiStore((s) => s.setShareCreateOpen)
  const triggerRefresh = useUiStore((s) => s.triggerRefresh)
  const requestShareDelete = useUiStore((s) => s.requestShareDelete)
  const requestShareToggle = useUiStore((s) => s.requestShareToggle)
  const requestSessionClose = useUiStore((s) => s.requestSessionClose)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null
      const inField =
        !!t &&
        (t.tagName === 'INPUT' ||
          t.tagName === 'TEXTAREA' ||
          t.isContentEditable === true)

      // Ctrl/Cmd+K 始终生效
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setPaletteOpen(true)
        return
      }
      // Esc 关闭所有弹窗（无论焦点）
      if (e.key === 'Escape') {
        setPaletteOpen(false)
        setShareCreateOpen(false)
        return
      }

      // 以下热键在输入控件中屏蔽，避免误触
      if (inField) return

      if (e.key === 'F5') {
        e.preventDefault()
        triggerRefresh()
        return
      }

      if (route === '/shares') {
        if (e.ctrlKey && e.key.toLowerCase() === 'n') {
          e.preventDefault()
          setShareCreateOpen(true)
        } else if (e.key === 'Delete' && selectedShares.length > 0) {
          e.preventDefault()
          requestShareDelete()
        } else if (e.key === ' ' && selectedShares.length > 0) {
          e.preventDefault()
          requestShareToggle()
        }
      } else if (route === '/sessions') {
        if (e.key === 'Delete' && selectedSessions.length > 0) {
          e.preventDefault()
          requestSessionClose()
        }
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [
    route,
    selectedShares,
    selectedSessions,
    setPaletteOpen,
    setShareCreateOpen,
    triggerRefresh,
    requestShareDelete,
    requestShareToggle,
    requestSessionClose
  ])
}
