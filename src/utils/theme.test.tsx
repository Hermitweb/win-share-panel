import { describe, it, expect, afterEach } from 'vitest'
import { theme as antdTheme } from 'antd'
import { applyThemeToDocument, pickAlgorithm } from './theme'

// 本文件用 .tsx 后缀是有意的：vitest.config.ts 的 dom project 只收集 `src/**/*.test.tsx`，
// 而这里既要操作 DOM、又要读 antd 的运行时对象；放成 .test.ts 会被 node project 收走而拿不到 DOM。

describe('applyThemeToDocument：暗色的两轨必须一起切换', () => {
  afterEach(() => {
    delete document.documentElement.dataset.theme
    document.documentElement.classList.remove('dark')
  })

  it('暗色：data-theme（自有 CSS 变量轨）与 .dark class（tailwind dark: 轨）同时落到 <html>', () => {
    applyThemeToDocument('dark')
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('浅色：两者同时撤回，不留 .dark 残留', () => {
    applyThemeToDocument('dark')
    applyThemeToDocument('light')
    expect(document.documentElement.dataset.theme).toBe('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('两轨保持一致：data-theme==="dark" 与 has("dark") 恒等（只落一半会被这里抓住）', () => {
    for (const t of ['light', 'dark', 'light'] as const) {
      applyThemeToDocument(t)
      expect(document.documentElement.dataset.theme === 'dark').toBe(
        document.documentElement.classList.contains('dark'),
      )
    }
  })
})

describe('pickAlgorithm：antd 轨的开关', () => {
  it('暗色取 darkAlgorithm，浅色取 defaultAlgorithm', () => {
    expect(pickAlgorithm('dark')).toBe(antdTheme.darkAlgorithm)
    expect(pickAlgorithm('light')).toBe(antdTheme.defaultAlgorithm)
  })
})
