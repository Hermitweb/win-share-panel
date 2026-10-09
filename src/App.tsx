import { useEffect } from 'react'
import { Routes, Route, Navigate } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import Layout from './components/Layout'
import CommandPalette from './components/CommandPalette'
import RouteSync from './components/RouteSync'
import Dashboard from './pages/Dashboard'
import Shares from './pages/Shares'
import Users from './pages/Users'
import Sessions from './pages/Sessions'
import Settings from './pages/Settings'
import { useHotkeys } from './hooks/useHotkeys'
import { useUiStore } from './stores/uiStore'
import { useAppStore } from './stores/appStore'

// B1 试点：页面数据层迁移 react-query（轮询/去重/失焦控制内建，替代手写 load+inflight）
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 0, // IPC 错误多为确定性（权限/非法参数），自动重试只会重复拉起 PowerShell
      refetchOnWindowFocus: false, // 桌面应用窗口频繁聚焦，自动重取由页面轮询策略显式表达
      staleTime: 3_000,
    },
  },
})

export default function App() {
  useHotkeys()
  // 应用级持久状态（主题/新手模式/置顶/告警规则）水合一次，供全站读取
  const hydrate = useAppStore((s) => s.hydrate)
  const theme = useAppStore((s) => s.state.theme)
  useEffect(() => {
    void hydrate()
  }, [hydrate])
  // 主题落到 <html>，CSS 变量与 antd 算法都据此切换（见 index.css / Layout）
  useEffect(() => {
    document.documentElement.dataset.theme = theme
    document.documentElement.classList.toggle('dark', theme === 'dark')
  }, [theme])
  const paletteOpen = useUiStore((s) => s.paletteOpen)
  const setPaletteOpen = useUiStore((s) => s.setPaletteOpen)

  return (
    <QueryClientProvider client={queryClient}>
      <Layout onCommand={() => setPaletteOpen(true)}>
        <RouteSync />
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/shares" element={<Shares />} />
          <Route path="/users" element={<Users />} />
          <Route path="/sessions" element={<Sessions />} />
          <Route path="/settings" element={<Settings />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
        <CommandPalette open={paletteOpen} onClose={() => setPaletteOpen(false)} />
      </Layout>
    </QueryClientProvider>
  )
}
