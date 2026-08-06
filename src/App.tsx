import { useEffect, useState } from 'react'
import { Routes, Route, Navigate } from 'react-router-dom'
import Layout from './components/Layout'
import CommandPalette from './components/CommandPalette'
import Dashboard from './pages/Dashboard'
import Shares from './pages/Shares'
import Users from './pages/Users'
import Sessions from './pages/Sessions'
import Settings from './pages/Settings'

export default function App() {
  const [paletteOpen, setPaletteOpen] = useState(false)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setPaletteOpen((v) => !v)
      }
      if (e.key === 'Escape') setPaletteOpen(false)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  return (
    <Layout onCommand={() => setPaletteOpen(true)}>
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
