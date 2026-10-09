import { describe, it, expect, vi, afterEach } from 'vitest'
import { render, screen, cleanup } from '@testing-library/react'
import type { ReactElement } from 'react'

// globals:false 下需手动卸载，避免 DOM 跨用例累积
afterEach(cleanup)

// api.ts 模块加载时读取 window.winshare —— 在 import 前注入 stub（jsdom 环境已有 window）
const { write } = vi.hoisted(() => {
  const w = vi.fn()
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = { log: { write: w } }
  return { write: w }
})

import ErrorBoundary from './ErrorBoundary'

function Bomb({ boom }: { boom: boolean }): ReactElement {
  if (boom) throw new Error('组件爆炸测试')
  return <div>正常内容</div>
}

describe('ErrorBoundary（E6）', () => {
  it('子组件抛错：渲染可见错误面板并转发日志', () => {
    // React 会把渲染错误同时打到 console.error（A3 会转发），此处静音断言噪音
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {})
    render(
      <ErrorBoundary>
        <Bomb boom />
      </ErrorBoundary>,
    )
    expect(screen.getByText('界面渲染出错')).toBeInTheDocument()
    expect(screen.getByText(/组件爆炸测试/)).toBeInTheDocument()
    expect(write).toHaveBeenCalledWith('error', expect.stringContaining('[ErrorBoundary]'))
    spy.mockRestore()
  })

  it('正常子组件透传渲染且不触发日志', () => {
    write.mockClear()
    render(
      <ErrorBoundary>
        <Bomb boom={false} />
      </ErrorBoundary>,
    )
    expect(screen.getByText('正常内容')).toBeInTheDocument()
    expect(write).not.toHaveBeenCalled()
  })
})
