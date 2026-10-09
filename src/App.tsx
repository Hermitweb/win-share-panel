import { useEffect, useState } from 'react'
import { Routes, Route, Navigate } from 'react-router-dom'
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
import { useHotkeys } from './hooks/useHotkeys'
import { useUiStore } from './stores/uiStore'
import { useAppStore } from './stores/appStore'
import { api, call } from './api'
import { readFirstRunFlag, shouldShowGuide } from './utils/firstRun'

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
          <Route path="/shares" element={<SharesWithOps />} />
          <Route path="/users" element={<Users />} />
          <Route path="/sessions" element={<Sessions />} />
          <Route path="/settings" element={<Settings />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
        <CommandPalette open={paletteOpen} onClose={() => setPaletteOpen(false)} />
        <FirstRunMount />
        <StaleProcessNotice />
      </Layout>
    </QueryClientProvider>
  )
}
