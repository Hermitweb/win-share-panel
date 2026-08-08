import { useEffect, useState } from 'react'
import { MinusOutlined, BorderOutlined, CloseOutlined } from '@ant-design/icons'
import { api } from '../api'

export default function TitleBar() {
  const [maximized, setMaximized] = useState(false)

  useEffect(() => {
    // 浏览器预览（无 Electron preload）时 window.winshare 不存在，跳过窗口控制
    if (!api) return
    api.window.isMaximized().then(setMaximized)
    api.window.onMaximizeChange(setMaximized)
  }, [])

  const btnStyle: React.CSSProperties = {
    width: 28,
    height: 28,
    border: 'none',
    background: 'transparent',
    borderRadius: 8,
    cursor: 'pointer',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: '#3A4A5C',
    transition: 'background 0.2s'
  }

  return (
    <div
      className="drag-region flex items-center justify-between h-10 px-4 select-none shrink-0"
      style={{ background: 'rgba(255,255,255,0.55)', backdropFilter: 'blur(16px)', borderBottom: '1px solid rgba(255,255,255,0.5)' }}
    >
      <div className="flex items-center gap-2">
        <img
          src="./logo.png"
          alt="WinShare Panel"
          className="w-5 h-5 rounded-md object-cover"
          draggable={false}
        />
        <span className="text-sm font-medium gradient-text">WinShare Panel</span>
      </div>
      <div className="no-drag flex items-center gap-1">
        <button
          style={btnStyle}
          onMouseEnter={(e) => (e.currentTarget.style.background = 'rgba(126,200,240,0.2)')}
          onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
          onClick={() => api.window.minimize()}
          title="最小化"
        >
          <MinusOutlined style={{ fontSize: 12 }} />
        </button>
        <button
          style={btnStyle}
          onMouseEnter={(e) => (e.currentTarget.style.background = 'rgba(126,200,240,0.2)')}
          onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
          onClick={() => api.window.toggleMaximize().then(setMaximized)}
          title={maximized ? '还原' : '最大化'}
        >
          <BorderOutlined style={{ fontSize: 11 }} />
        </button>
        <button
          style={btnStyle}
          onMouseEnter={(e) => (e.currentTarget.style.background = 'rgba(245,198,198,0.45)')}
          onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
          onClick={() => api.window.close()}
          title="关闭"
        >
          <CloseOutlined style={{ fontSize: 12 }} />
        </button>
      </div>
    </div>
  )
}
