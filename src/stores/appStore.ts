import { create } from 'zustand'
import { api, call } from '../api'
import type { AppState, AppStatePatch, JournalEntry } from '../types'

// ===== 应用级持久状态的前端镜像 =====
// 主进程 appstate.json 是唯一真相源；这里做一次性水合 + 乐观写入（失败回滚并抛出）。
// 不放 React Query：这是本地偏好而非服务端缓存，无失效概念，且需要跨页面同步读。

const FALLBACK: AppState = {
  theme: 'light',
  advancedMode: true,
  pinned: [],
  alertRules: { idleAlertMinutes: null, smb1Alert: true, weakPasswordAlert: true, diskLowGb: 20 },
  autoStart: false,
  dashboardHistory: [],
  journal: [],
}

interface AppStore {
  state: AppState
  hydrated: boolean
  journal: JournalEntry[]
  hydrate: () => Promise<void>
  /** 乐观补丁：主进程校验失败即回滚镜像并抛错（调用方 message.error） */
  patch: (p: AppStatePatch) => Promise<void>
  togglePin: (key: string) => Promise<void>
  loadJournal: () => Promise<void>
  undoJournal: (id: string) => Promise<string>
  clearJournal: () => Promise<void>
}

export const useAppStore = create<AppStore>((set, get) => ({
  state: FALLBACK,
  hydrated: false,
  journal: [],

  hydrate: async () => {
    try {
      const s = await call(() => api.state.get())
      set({ state: s, hydrated: true })
    } catch {
      // 主进程不可用时保持默认值，UI 仍可渲染（不让偏好读取失败阻断首屏）
      set({ hydrated: true })
    }
  },

  patch: async (p) => {
    const prev = get().state
    // 先本地合并做乐观更新，避免切换主题/置顶这类操作出现可感知延迟
    set({
      state: {
        ...prev,
        ...p,
        alertRules: p.alertRules ? { ...prev.alertRules, ...p.alertRules } : prev.alertRules,
      },
    })
    try {
      const next = await call(() => api.state.patch(p))
      set({ state: next })
    } catch (e) {
      set({ state: prev })
      throw e
    }
  },

  togglePin: async (key) => {
    const pinned = get().state.pinned
    const next = pinned.includes(key) ? pinned.filter((k) => k !== key) : [...pinned, key]
    await get().patch({ pinned: next })
  },

  loadJournal: async () => {
    try {
      set({ journal: await call(() => api.state.journalList()) })
    } catch (e) {
      set({ journal: [] })
      throw e
    }
  },

  undoJournal: async (id) => {
    const msg = await call(() => api.state.journalUndo(id))
    await get()
      .loadJournal()
      .catch(() => undefined)
    return msg
  },

  clearJournal: async () => {
    await call(() => api.state.journalClear())
    set({ journal: [] })
  },
}))

export const shareKey = (protocol: string, name: string): string => `${protocol}:${name}`
