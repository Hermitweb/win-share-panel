import { Routes, Route, Navigate } from 'react-router-dom'
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

export default function App() {
  useHotkeys()
  const paletteOpen = useUiStore((s) => s.paletteOpen)
  const setPaletteOpen = useUiStore((s) => s.setPaletteOpen)

  return (
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
  )
}
