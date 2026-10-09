import type { WinShareApi } from './types'

declare global {
  interface Window {
    winshare: WinShareApi
  }
}

export const api = window.winshare

// 统一调用封装：抛出友好错误，并透传主进程 AppError 的 code/category（R-6）。
// Electron 新版 IPC 会保留 Error 自定义属性；若运行环境不支持，属性为 undefined，
// 行为与原实现等价（调用方按可选字段防御式读取）。
export interface CallError extends Error {
  code?: string
  category?: string
}

// 升级后仍在跑的旧进程：渲染层已热更新到新界面，但 preload/主进程还是上一版，
// 新增通道整组缺失（api.diagnose === undefined）。原始报错是
// "Cannot read properties of undefined (reading 'run')"——对用户完全不可执行。
// 这里统一换成"怎么做"的提示，并保留原始错误便于排查。
const STALE_CHANNEL_RE = /Cannot read propert(?:y|ies) of (?:undefined|null) \(reading '([^']+)'\)/

function humanize(raw: string): string {
  const m = STALE_CHANNEL_RE.exec(raw)
  if (!m) return raw
  return (
    `界面与后台版本不一致：当前运行的仍是升级前的进程，缺少「${m[1]}」通道。` +
    `请从右下角托盘图标选择「退出」后重新启动（只关窗口不够，程序会留在托盘继续跑旧版本）。` +
    `原始错误：${raw}`
  )
}

export async function call<T>(fn: () => Promise<T>): Promise<T> {
  try {
    return await fn()
  } catch (e) {
    const src = e as Partial<CallError>
    const wrapped: CallError = new Error(humanize(src?.message || '操作失败'))
    if (typeof src?.code === 'string') wrapped.code = src.code
    if (typeof src?.category === 'string') wrapped.category = src.category
    throw wrapped
  }
}
