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

export async function call<T>(fn: () => Promise<T>): Promise<T> {
  try {
    return await fn()
  } catch (e) {
    const src = e as Partial<CallError>
    const wrapped: CallError = new Error(src?.message || '操作失败')
    if (typeof src?.code === 'string') wrapped.code = src.code
    if (typeof src?.category === 'string') wrapped.category = src.category
    throw wrapped
  }
}
