import type { WinShareApi } from './types'

declare global {
  interface Window {
    winshare: WinShareApi
  }
}

export const api = window.winshare

// 统一调用封装：抛出友好错误
export async function call<T>(fn: () => Promise<T>): Promise<T> {
  try {
    return await fn()
  } catch (e) {
    throw new Error((e as Error).message || '操作失败')
  }
}
