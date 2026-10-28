import { useState } from 'react'
import type { ReactNode } from 'react'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, useNavigate } from 'react-router-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { AliveRoutes } from './App'

// globals:false 下 RTL auto-cleanup 不注册，手动逐用例卸载，避免 DOM 跨用例累积
afterEach(cleanup)

/** 内部可自增的页面：用来验证"切走再切回"时组件没被卸载重建（state 保留）。 */
function CounterPage() {
  const [n, setN] = useState(0)
  return <button onClick={() => setN((v) => v + 1)}>count:{n}</button>
}

function PlainPage({ label }: { label: string }) {
  return <div>{label}</div>
}

function Go({ to, children }: { to: string; children: ReactNode }) {
  const nav = useNavigate()
  return <button onClick={() => nav(to)}>{children}</button>
}

const PAGES = [
  { path: '/a', component: CounterPage },
  { path: '/b', component: () => <PlainPage label="B" /> },
  { path: '/c', component: () => <PlainPage label="C" /> },
]

describe('AliveRoutes 保活路由', () => {
  it('首访才挂载：没访问过的页面不进 DOM', () => {
    render(
      <MemoryRouter initialEntries={['/a']}>
        <AliveRoutes pages={PAGES} />
      </MemoryRouter>,
    )
    expect(screen.getByText('count:0')).toBeInTheDocument()
    expect(screen.queryByText('B')).not.toBeInTheDocument()
    expect(screen.queryByText('C')).not.toBeInTheDocument()
  })

  it('切页后旧页保活：隐藏但不卸载，内部状态保留', () => {
    render(
      <MemoryRouter initialEntries={['/a']}>
        <AliveRoutes pages={PAGES} />
        <Go to="/b">go-b</Go>
        <Go to="/a">go-a</Go>
      </MemoryRouter>,
    )

    // 初始只有 A，且 A 内部计数为 0
    expect(screen.getByText('count:0')).toBeInTheDocument()
    fireEvent.click(screen.getByText('count:0'))
    expect(screen.getByText('count:1')).toBeInTheDocument()

    // 切到 B：A 仍在 DOM 里（只是被隐藏），B 出现
    fireEvent.click(screen.getByText('go-b'))
    expect(screen.getByText('B')).toBeInTheDocument()
    expect(screen.getByText('count:1')).toBeInTheDocument()

    // 切回 A：计数仍是 1 —— 证明 A 没有走"卸载 + 重挂载"，否则会回到 0
    fireEvent.click(screen.getByText('go-a'))
    expect(screen.getByText('count:1')).toBeInTheDocument()
    expect(screen.queryByText('B')).toBeInTheDocument()
  })
})
