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
