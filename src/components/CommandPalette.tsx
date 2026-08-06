import { useState } from 'react'
import { Modal, Input } from 'antd'
import { useNavigate } from 'react-router-dom'

interface Command {
  key: string
  label: string
  hint: string
}

const commands: Command[] = [
  { key: '/', label: '前往：仪表板', hint: '概览共享与会话' },
  { key: '/shares', label: '前往：共享管理', hint: '创建/编辑/删除共享' },
  { key: '/users', label: '前往：用户权限', hint: '管理本地用户与权限' },
  { key: '/sessions', label: '前往：会话监控', hint: '查看并断开会话' },
  { key: '/settings', label: '前往：SMB 配置', hint: '服务器配置' }
]

export default function CommandPalette({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [query, setQuery] = useState('')
  const navigate = useNavigate()
  const filtered = commands.filter((c) => c.label.toLowerCase().includes(query.toLowerCase()))

  const run = (key: string) => {
    navigate(key)
    onClose()
    setQuery('')
  }

  return (
    <Modal
      open={open}
      onCancel={() => {
        onClose()
        setQuery('')
      }}
      footer={null}
      closable={false}
      title={null}
      width={520}
      styles={{ body: { padding: 0 } }}
      style={{ top: 80 }}
    >
      <Input
        placeholder="输入页面名或命令..."
        autoFocus
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        style={{ border: 'none', borderBottom: '1px solid rgba(126,200,240,0.3)', borderRadius: 0 }}
        size="large"
      />
      <div className="py-2 max-h-72 overflow-auto">
        {filtered.length === 0 && <div className="px-4 py-6 text-center text-fog text-sm">无匹配命令</div>}
        {filtered.map((c) => (
          <div
            key={c.key}
            onClick={() => run(c.key)}
            className="px-4 py-2 cursor-pointer hover:bg-primary/10 transition-all"
          >
            <div className="text-sm text-ink">{c.label}</div>
            <div className="text-xs text-fog">{c.hint}</div>
          </div>
        ))}
      </div>
    </Modal>
  )
}
