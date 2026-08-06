import { create } from 'zustand'
import type { ServiceStatus, Protocol, ProtocolDetectionResult } from '../types'

export interface HealthState {
  ok: boolean
  detail: string
  serviceStatus?: ServiceStatus
  checkedAt: number
}

interface UiState {
  // 路由与全局弹窗
  route: string
  paletteOpen: boolean
  shareCreateOpen: boolean
  // 选中态（共享与会话批量操作共享，格式为 ${protocol}:${name} 复合 key）
  selectedShares: string[]
  selectedSessions: string[]
  // 意图 tick：hotkey 写入，对应页面订阅后执行实际操作
  refreshTick: number
  shareDeleteTick: number
  shareToggleTick: number
  sessionCloseTick: number
  // 健康态
  health: HealthState | null
  // 多协议扩展
  activeProtocol: Protocol | 'all'
  protocolCaps: ProtocolDetectionResult | null

  setRoute: (r: string) => void
  setPaletteOpen: (v: boolean) => void
  setShareCreateOpen: (v: boolean) => void
  setSelectedShares: (ids: string[]) => void
  setSelectedSessions: (ids: string[]) => void
  triggerRefresh: () => void
  requestShareDelete: () => void
  requestShareToggle: () => void
  requestSessionClose: () => void
  setHealth: (h: HealthState | null) => void
  setActiveProtocol: (p: Protocol | 'all') => void
  setProtocolCaps: (c: ProtocolDetectionResult | null) => void
}

export const useUiStore = create<UiState>((set) => ({
  route: '/',
  paletteOpen: false,
  shareCreateOpen: false,
  selectedShares: [],
  selectedSessions: [],
  refreshTick: 0,
  shareDeleteTick: 0,
  shareToggleTick: 0,
  sessionCloseTick: 0,
  health: null,
  activeProtocol: 'all',
  protocolCaps: null,

  setRoute: (r) => set({ route: r }),
  setPaletteOpen: (v) => set({ paletteOpen: v }),
  setShareCreateOpen: (v) => set({ shareCreateOpen: v }),
  setSelectedShares: (ids) => set({ selectedShares: ids }),
  setSelectedSessions: (ids) => set({ selectedSessions: ids }),
  triggerRefresh: () => set((s) => ({ refreshTick: s.refreshTick + 1 })),
  requestShareDelete: () => set((s) => ({ shareDeleteTick: s.shareDeleteTick + 1 })),
  requestShareToggle: () => set((s) => ({ shareToggleTick: s.shareToggleTick + 1 })),
  requestSessionClose: () => set((s) => ({ sessionCloseTick: s.sessionCloseTick + 1 })),
  setHealth: (h) => set({ health: h }),
  setActiveProtocol: (p) => set({ activeProtocol: p }),
  setProtocolCaps: (c) => set({ protocolCaps: c })
}))
