import { theme as antdTheme } from 'antd'
import type { AppState } from '../types'

/** 主题取值（与 AppState.theme 同源，避免各处重复写联合类型） */
export type AppTheme = AppState['theme']

/**
 * 把主题落到 <html>：`data-theme` 供 index.css 的 `[data-theme='dark']` 变量段使用，
 * `.dark` class 供 tailwind 的 `dark:` 变体使用（`darkMode:'class'`）。
 *
 * 为什么抽成纯函数：暗色是**双轨**（自有 CSS + tailwind 变体）外加 antd 一轨，
 * 三者必须同时切换。上一批的教训是「只落 data-theme 等于没接线」——
 * 抽出来才能被 jsdom 直接钉住「两件事一起发生」，而不是靠人眼看。
 */
export function applyThemeToDocument(
  theme: AppTheme,
  root: HTMLElement = document.documentElement,
): void {
  root.dataset.theme = theme
  root.classList.toggle('dark', theme === 'dark')
}

/**
 * antd 算法映射：antd 那一轨真正生效的开关（ConfigProvider 的 algorithm）。
 * 暗色下必须换成 darkAlgorithm，否则 antd 组件的容器色/文字色仍是浅色主题。
 */
export function pickAlgorithm(theme: AppTheme): typeof antdTheme.defaultAlgorithm {
  return theme === 'dark' ? antdTheme.darkAlgorithm : antdTheme.defaultAlgorithm
}
