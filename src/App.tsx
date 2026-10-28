import { useEffect, useState } from 'react'
import type { ComponentType } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import Layout from './components/Layout'
import CommandPalette from './components/CommandPalette'
import RouteSync from './components/RouteSync'
import FirstRunGuide from './components/FirstRunGuide'
import StaleProcessNotice from './components/StaleProcessNotice'
import SharesWithOps from './components/SharesWithOps'
import Dashboard from './pages/Dashboard'
import Users from './pages/Users'
import Sessions from './pages/Sessions'
import Settings from './pages/Settings'
import AppSettings from './pages/AppSettings'
import { useHotkeys } from './hooks/useHotkeys'
import { useUiStore } from './stores/uiStore'
import { useAppStore } from './stores/appStore'
import { api, call } from './api'
import { readFirstRunFlag, shouldShowGuide } from './utils/firstRun'
import { applyThemeToDocument } from './utils/theme'

// B1 试点：页面数据层迁移 react-query（轮询/去重/失焦控制内建，替代手写 load+inflight）
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 0, // IPC 错误多为确定性（权限/非法参数），自动重试只会重复拉起 PowerShell
      refetchOnWindowFocus: false, // 桌面应用窗口频繁聚焦，自动重取由页面轮询策略显式表达
      // 页面已保活（见 AliveRoutes）：挂载只发生一次，staleTime 不必为"每次进来都重取"服务。
      // 3s 太短会让切页回来时几乎必重取，白白重付 IPC + PowerShell 派发成本。
      staleTime: 30_000,
    },
  },
})

// 保活路由的页面清单（顺序即侧栏顺序）
const PAGES = [
  { path: '/', component: Dashboard },
  { path: '/shares', component: SharesWithOps },
  { path: '/users', component: Users },
  { path: '/sessions', component: Sessions },
  { path: '/settings', component: Settings },
  { path: '/app-settings', component: AppSettings },
] as const

/**
 * 首启向导挂载点。
 * 判定交给 utils/firstRun 的纯函数：空共享 + 未标记 + 新手模式才弹；
 * shares 未加载完（undefined）不弹——加载中闪一下向导，恰恰会骚扰老用户。
 * 「以后再说」只关本次会话（不写标记），下次冷启动仍会提示；创建成功由组件写标记后不再打扰。
 */
function FirstRunMount() {
  const advancedMode = useAppStore((s) => s.state.advancedMode)
  const [flag, setFlag] = useState<unknown>(() => readFirstRunFlag())
  const [dismissed, setDismissed] = useState(false)
  const { data: shares } = useQuery({
    queryKey: ['shares', 'smb', 'first-run'],
    queryFn: () => call(() => api.adapter.list('smb')),
    staleTime: 30_000,
  })
  return (
    <FirstRunGuide
      open={!dismissed && shouldShowGuide(shares, advancedMode, flag)}
      onClose={() => {
        // 组件在创建成功时已写入标记；这里只按最新标记收尾并关掉本次会话
        setFlag(readFirstRunFlag())
        setDismissed(true)
      }}
    />
  )
}

/**
 * 保活路由：首次访问才挂载，之后用 display 隐藏而非卸载。
 *
 * 为什么不用 <Routes>：react-router 只渲染命中的那条，切页即卸载旧页 + 挂载新页。
 * 仪表盘的 ECharts 实例与 5~6 个 IPC 查询会被整份重建，切一次导航就要重付一次
 * 冷启动成本（图表 init + PowerShell 进程池派发）——这是"切导航卡"的主因。
 * 保活后切页只切换 display，页面状态与图表实例都还在。
 *
 * 首访才挂载（而非启动即全挂）是为了不让 6 个页面的副作用在冷启动时一起跑。
 */
export interface AlivePage {
  path: string
  component: ComponentType
}

export function AliveRoutes({ pages = PAGES }: { pages?: readonly AlivePage[] }) {
  const { pathname } = useLocation()
  const [alive, setAlive] = useState<string[]>(() => [pathname])

  // 把新访问过的路径并入保活集合。
  // 放在渲染期而非 effect：react-hooks/set-state-in-effect 会拦下 effect 里的直接 setState，
  // 且 effect 版本要多跑一轮 commit。React 允许组件在渲染期更新自己的 state
  // （"props 变化时调整 state" 的推荐写法），紧随其后立即重渲染。
  if (!alive.includes(pathname)) {
    setAlive((prev) => (prev.includes(pathname) ? prev : [...prev, pathname]))
  }

  return (
    <>
      {/* 未匹配路径回首页（对齐原 <Route path="*" element={<Navigate to="/" replace />} />）*/}
      {!pages.some((p) => p.path === pathname) && <Navigate to="/" replace />}
      {pages.map(({ path, component: Page }) =>
        alive.includes(path) ? (
          <div key={path} style={{ display: pathname === path ? 'block' : 'none' }}>
            <Page />
          </div>
        ) : null,
      )}
    </>
  )
}

/**
 * 后台进程版本自检：缺新通道就提示，若能重启（system:relaunch 存在）就给「立即重启」按钮。
 * 抽到 components/StaleProcessNotice 是为了可被 jsdom 单测覆盖（含"连重启通道都缺"的旧进程分支）。
 */
export default function App() {
  useHotkeys()
  // 应用级持久状态（主题/新手模式/置顶/告警规则）水合一次，供全站读取
  const hydrate = useAppStore((s) => s.hydrate)
  const theme = useAppStore((s) => s.state.theme)
  useEffect(() => {
    void hydrate()
  }, [hydrate])
  // 主题落到 <html>：自有 CSS 变量（index.css）与 tailwind 的 dark: 变体都据此切换。
  // antd 那一轨在 ThemeProvider（ConfigProvider 的 algorithm），三轨同源 appStore.state.theme。
  useEffect(() => {
    applyThemeToDocument(theme)
  }, [theme])
  const paletteOpen = useUiStore((s) => s.paletteOpen)
  const setPaletteOpen = useUiStore((s) => s.setPaletteOpen)

  return (
    <QueryClientProvider client={queryClient}>
      <Layout onCommand={() => setPaletteOpen(true)}>
        <RouteSync />
        <AliveRoutes />
        <CommandPalette open={paletteOpen} onClose={() => setPaletteOpen(false)} />
        <FirstRunMount />
        <StaleProcessNotice />
      </Layout>
    </QueryClientProvider>
  )
}
