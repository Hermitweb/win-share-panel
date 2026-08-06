import type { ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import {
  DashboardOutlined,
  FolderOpenOutlined,
  TeamOutlined,
  ApiOutlined,
  SettingOutlined,
  ThunderboltOutlined
} from '@ant-design/icons'
import TitleBar from './TitleBar'

interface MenuItem {
  path: string
  label: string
  icon: ReactNode
}

const menus: MenuItem[] = [
  { path: '/', label: '仪表板', icon: <DashboardOutlined /> },
  { path: '/shares', label: '共享管理', icon: <FolderOpenOutlined /> },
  { path: '/users', label: '用户权限', icon: <TeamOutlined /> },
  { path: '/sessions', label: '会话监控', icon: <ApiOutlined /> },
  { path: '/settings', label: 'SMB 配置', icon: <SettingOutlined /> }
]

export default function Layout({ children, onCommand }: { children: ReactNode; onCommand: () => void }) {
  return (
    <div className="flex flex-col h-screen">
      <TitleBar />
      <div className="flex flex-1 overflow-hidden">
        <aside className="w-56 p-3 flex flex-col gap-1 shrink-0">
          {menus.map((m) => (
            <NavLink
              key={m.path}
              to={m.path}
              end={m.path === '/'}
              className={({ isActive }) =>
                `flex items-center gap-3 px-3 py-2 rounded-btn text-sm transition-all ${
                  isActive ? 'bg-white/70 text-primary shadow-card' : 'text-ink/70 hover:bg-white/40'
                }`
              }
            >
              <span style={{ fontSize: 16 }}>{m.icon}</span>
              {m.label}
            </NavLink>
          ))}
          <button
            onClick={onCommand}
            className="mt-2 flex items-center gap-3 px-3 py-2 rounded-btn text-sm text-ink/60 hover:bg-white/40 transition-all no-drag"
          >
            <ThunderboltOutlined />
            命令面板
            <span className="ml-auto text-xs text-fog">Ctrl+K</span>
          </button>
        </aside>
        <main className="flex-1 p-5 overflow-auto">{children}</main>
      </div>
    </div>
  )
}
