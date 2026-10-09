import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { render, screen, cleanup } from '@testing-library/react'
import { theme as antdTheme } from 'antd'
import ThemeProvider from './ThemeProvider'
import { useAppStore } from '../stores/appStore'

/**
 * 集成证据：证明 antd 那一轨**真的接上了**，而不只是「写了 algorithm 字段」。
 *
 * 判据取自 token：浅色分支我们显式注入 `colorBgContainer: rgba(255,255,255,0.7)`；
 * 暗色分支刻意不注入该 token、交给 darkAlgorithm 的默认值。
 * 于是「暗色下的容器色 ≠ 浅色下的容器色」就等价于「algorithm 确实换了」——
 * 若哪天有人把 algorithm 写死成浅色，本用例会红。
 */
const LIGHT_CONTAINER = 'rgba(255,255,255,0.7)'

function TokenProbe() {
  const { token } = antdTheme.useToken()
  return <span data-testid="bg">{token.colorBgContainer}</span>
}

function setTheme(t: 'light' | 'dark') {
  useAppStore.setState({ state: { ...useAppStore.getState().state, theme: t } })
}

function bg() {
  return screen.getByTestId('bg').textContent
}

describe('ThemeProvider：antd 暗色轨接线', () => {
  beforeEach(() => setTheme('light'))
  afterEach(() => {
    cleanup()
    setTheme('light')
  })

  it('浅色：容器色等于我们显式注入的半透明值', () => {
    render(
      <ThemeProvider>
        <TokenProbe />
      </ThemeProvider>,
    )
    expect(bg()).toBe(LIGHT_CONTAINER)
  })

  it('暗色：容器色不再是我们注入的浅色值（说明 darkAlgorithm 生效）', () => {
    setTheme('dark')
    render(
      <ThemeProvider>
        <TokenProbe />
      </ThemeProvider>,
    )
    expect(bg()).not.toBe(LIGHT_CONTAINER)
    expect(bg()).toBeTruthy()
  })

  it('跟随 store：同一次挂载内切主题也会重算 token（不是首帧一次性读取）', () => {
    const view = render(
      <ThemeProvider>
        <TokenProbe />
      </ThemeProvider>,
    )
    expect(bg()).toBe(LIGHT_CONTAINER)
    setTheme('dark')
    view.rerender(
      <ThemeProvider>
        <TokenProbe />
      </ThemeProvider>,
    )
    expect(bg()).not.toBe(LIGHT_CONTAINER)
  })
})
