import { describe, it, expect, beforeEach } from 'vitest'
import { useUiStore } from './uiStore'

// 模块级快照初始 state（在测试改动前捕获），用于测试间重置单例 store
const initialState = useUiStore.getState()

describe('uiStore', () => {
  beforeEach(() => {
    useUiStore.setState(initialState)
  })

  it('初始状态符合约定', () => {
    const s = useUiStore.getState()
    expect(s.route).toBe('/')
    expect(s.paletteOpen).toBe(false)
    expect(s.shareCreateOpen).toBe(false)
    expect(s.selectedShares).toEqual([])
    expect(s.selectedSessions).toEqual([])
    expect(s.refreshTick).toBe(0)
    expect(s.health).toBeNull()
    expect(s.activeProtocol).toBe('all')
    expect(s.protocolCaps).toBeNull()
  })

  it('路由与弹窗开关 setter 生效', () => {
    const { setRoute, setPaletteOpen, setShareCreateOpen } = useUiStore.getState()
    setRoute('/shares')
    setPaletteOpen(true)
    setShareCreateOpen(true)
    const s = useUiStore.getState()
    expect(s.route).toBe('/shares')
    expect(s.paletteOpen).toBe(true)
    expect(s.shareCreateOpen).toBe(true)
  })

  it('选中集合为整体替换语义', () => {
    useUiStore.getState().setSelectedShares(['smb:a', 'ftp:b'])
    expect(useUiStore.getState().selectedShares).toEqual(['smb:a', 'ftp:b'])
    useUiStore.getState().setSelectedShares([])
    expect(useUiStore.getState().selectedShares).toEqual([])
  })

  it('tick 意图为单调递增', () => {
    const { triggerRefresh, requestShareDelete, requestShareToggle, requestSessionClose } =
      useUiStore.getState()
    triggerRefresh()
    triggerRefresh()
    requestShareDelete()
    requestShareToggle()
    requestSessionClose()
    requestSessionClose()
    requestSessionClose()
    const s = useUiStore.getState()
    expect(s.refreshTick).toBe(2)
    expect(s.shareDeleteTick).toBe(1)
    expect(s.shareToggleTick).toBe(1)
    expect(s.sessionCloseTick).toBe(3)
  })

  it('健康态/协议能力 setter 可写可清空', () => {
    const health = { ok: true, detail: 'PowerShell SMB 模块可用', checkedAt: 1 }
    useUiStore.getState().setHealth(health)
    expect(useUiStore.getState().health).toEqual(health)
    useUiStore.getState().setHealth(null)
    expect(useUiStore.getState().health).toBeNull()

    useUiStore.getState().setActiveProtocol('nfs')
    expect(useUiStore.getState().activeProtocol).toBe('nfs')
    useUiStore.getState().setActiveProtocol('all')
    expect(useUiStore.getState().activeProtocol).toBe('all')
  })
})
