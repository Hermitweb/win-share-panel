import { describe, it, expect, vi } from 'vitest'

// api.ts 在模块加载时读取 window.winshare（Electron contextBridge 注入）；
// node 测试环境用 vi.hoisted 在 import 前放置最小 stub，仅验证 call() 的错误包装语义。
vi.hoisted(() => {
  ;(globalThis as Record<string, unknown>).window = { winshare: { _stub: true } }
})

import { api, call } from './api'

describe('api 模块', () => {
  it('api 透传 window.winshare 注入', () => {
    expect(api).toEqual({ _stub: true })
  })
})

describe('call 错误包装', () => {
  it('resolve 值原样透传', async () => {
    await expect(call(async () => 42)).resolves.toBe(42)
  })

  it('Error 消息原样透传', async () => {
    await expect(
      call(async () => {
        throw new Error('共享名非法')
      }),
    ).rejects.toThrow('共享名非法')
  })

  it('非 Error 载荷（空消息）回退为通用文案', async () => {
    await expect(
      call(async () => {
        throw { message: '' }
      }),
    ).rejects.toThrow('操作失败')
  })

  it('R-6：透传 AppError 的 code/category 元数据', async () => {
    const appErr = Object.assign(new Error('权限不足'), {
      code: 'NOT_ADMIN',
      category: 'permission',
    })
    await expect(
      call(async () => {
        throw appErr
      }),
    ).rejects.toMatchObject({ message: '权限不足', code: 'NOT_ADMIN', category: 'permission' })
  })

  it('R-6：源错误无元数据时，包装错误不携带 code/category', async () => {
    let caught: unknown
    try {
      await call(async () => {
        throw new Error('boom')
      })
    } catch (e) {
      caught = e
    }
    const err = caught as { code?: string; category?: string }
    expect(err.code).toBeUndefined()
    expect(err.category).toBeUndefined()
  })
})
