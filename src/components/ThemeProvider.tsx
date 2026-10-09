import type { ReactNode } from 'react'
import { ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { useAppStore } from '../stores/appStore'
import { pickAlgorithm } from '../utils/theme'

/**
 * 主题接线层（暗色双轨的 antd 一轨）。
 *
 * 为什么需要单独一层：`ConfigProvider` 原先直接写在 main.tsx 里、位于 <App/> **之上**，
 * 而主题状态在 appStore（由 App.tsx 内部 hydrate）。上层读不到下层 store，
 * 于是 `algorithm` 无从切换——darkAlgorithm 接不上的根因就在这里。抽成本组件后，
 * `ConfigProvider` 才真正跟随 `appStore.state.theme`。
 *
 * 其余两轨：自有 CSS 变量在 index.css 的 `[data-theme='dark']`；
 * tailwind 的 `dark:` 变体由 `darkMode:'class'` + App.tsx 经 applyThemeToDocument
 * 落在 <html> 的 `.dark` 驱动。三者的唯一真源都是 `appStore.state.theme`。
 *
 * 已知取舍（如实记录，不隐藏）：appStore.hydrate 是异步的（读主进程 appstate.json），
 * 因此冷启动首帧会以 FALLBACK 的 'light' 渲染一次、hydrate 后再切到用户设置——
 * 即**有一次浅色闪烁**。这是「主题真源在主进程」的固有代价；要消除需把主题镜像到
 * localStorage 并在 index.html 内联脚本里预置 class，属另一项改动，本批次未做。
 */
export default function ThemeProvider({ children }: { children: ReactNode }) {
  const appTheme = useAppStore((s) => s.state.theme)
  const isDark = appTheme === 'dark'

  return (
    <ConfigProvider
      locale={zhCN}
      theme={{
        algorithm: pickAlgorithm(appTheme),
        token: {
          colorPrimary: '#7EC8F0',
          borderRadius: 12,
          // 仅浅色下用半透明容器色（配合背后的玻璃拟态渐变）。
          // 暗色下交给 darkAlgorithm 的默认容器色——否则半透明白会与 --glass-bg 叠加发灰。
          ...(isDark ? {} : { colorBgContainer: 'rgba(255,255,255,0.7)' }),
        },
      }}
    >
      {children}
    </ConfigProvider>
  )
}
