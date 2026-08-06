import { useEffect, useMemo, useRef, useState } from 'react'
import { Modal, Input } from 'antd'
import { useNavigate } from 'react-router-dom'
import { api, call } from '../api'
import { useUiStore } from '../stores/uiStore'
import type { Share, LocalUser, SmbSession } from '../types'

interface Command {
  key: string
  label: string
  hint: string
  group: 'nav' | 'share' | 'user' | 'session' | 'action'
  action: () => void
}

const STATIC_COMMANDS: Command[] = [
  { key: '/', label: '前往：仪表板', hint: '概览共享与会话', group: 'nav', action: () => {} },
  { key: '/shares', label: '前往：共享管理', hint: '创建/编辑/删除共享', group: 'nav', action: () => {} },
  { key: '/users', label: '前往：用户权限', hint: '管理本地用户与权限', group: 'nav', action: () => {} },
  { key: '/sessions', label: '前往：会话监控', hint: '查看并断开会话', group: 'nav', action: () => {} },
  { key: '/settings', label: '前往：服务器配置', hint: 'SMB/NFS/FTP/WebDAV 配置', group: 'nav', action: () => {} }
]

const GROUP_LABEL: Record<Command['group'], string> = {
  nav: '页面导航',
  share: '共享',
  user: '用户',
  session: '会话',
  action: '操作'
}

const GROUP_ORDER: Command['group'][] = ['nav', 'share', 'user', 'session', 'action']

export default function CommandPalette({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [query, setQuery] = useState('')
  const [dynamic, setDynamic] = useState<Command[]>([])
  const [activeIndex, setActiveIndex] = useState(0)
  const navigate = useNavigate()
  const setSelectedShares = useUiStore((s) => s.setSelectedShares)
  const listRef = useRef<HTMLDivElement>(null)

  // 防抖跨域搜索：输入 ≥2 字符时 200ms 后拉
  useEffect(() => {
    const q = query.trim()
    if (q.length < 2) {
      setDynamic([])
      return
    }
    let cancelled = false
    const t = setTimeout(async () => {
      try {
        const [shares, users, sessions] = await Promise.all([
          call(() => api.adapter.list()).catch(() => [] as Share[]),
          call(api.user.list).catch(() => [] as LocalUser[]),
          call(api.session.list).catch(() => [] as SmbSession[])
        ])
        if (cancelled) return
        const ql = q.toLowerCase()
        const cmds: Command[] = []

        shares
          .filter(
            (s) =>
              s.name.toLowerCase().includes(ql) ||
              (s.description || '').toLowerCase().includes(ql) ||
              s.path.toLowerCase().includes(ql) ||
              s.protocol.toLowerCase().includes(ql)
          )
          .slice(0, 12)
          .forEach((s) => {
            cmds.push({
              key: `share:${s.protocol}:${s.name}`,
              label: `共享：${s.name}`,
              hint: `${s.protocol.toUpperCase()} · ${s.path}`,
              group: 'share',
              action: () => {
                navigate('/shares')
                setSelectedShares([`${s.protocol}:${s.name}`])
              }
            })
          })

        users
          .filter((u) => u.name.toLowerCase().includes(ql) || (u.fullName || '').toLowerCase().includes(ql))
          .slice(0, 10)
          .forEach((u) => {
            cmds.push({
              key: `user:${u.name}`,
              label: `用户：${u.name}`,
              hint: u.fullName || '—',
              group: 'user',
              action: () => navigate('/users')
            })
          })

        sessions
          .filter(
            (s) =>
              s.clientUserName.toLowerCase().includes(ql) ||
              s.clientComputerName.toLowerCase().includes(ql)
          )
          .slice(0, 10)
          .forEach((s) => {
            cmds.push({
              key: `session:${s.clientId}`,
              label: `会话：${s.clientUserName}`,
              hint: s.clientComputerName,
              group: 'session',
              action: () => navigate('/sessions')
            })
          })

        setDynamic(cmds)
        setActiveIndex(0)
      } catch {
        if (!cancelled) setDynamic([])
      }
    }, 200)
    return () => {
      cancelled = true
      clearTimeout(t)
    }
  }, [query, navigate, setSelectedShares])

  // 合并静态 + 动态，按 query 过滤静态 nav
  const merged = useMemo(() => {
    const ql = query.trim().toLowerCase()
    const nav = ql
      ? STATIC_COMMANDS.filter((c) => c.label.toLowerCase().includes(ql))
      : STATIC_COMMANDS
    return [...nav, ...dynamic]
  }, [query, dynamic])

  // 分组
  const grouped = useMemo(() => {
    const map = new Map<Command['group'], Command[]>()
    merged.forEach((c) => {
      if (!map.has(c.group)) map.set(c.group, [])
      map.get(c.group)!.push(c)
    })
    return GROUP_ORDER.filter((g) => map.has(g)).map((g) => ({ group: g, items: map.get(g)! }))
  }, [merged])

  // 平铺索引数组（用于键盘上下选择）
  const flat = useMemo(() => grouped.flatMap((g) => g.items), [grouped])

  // 输入变化或结果变化时重置 activeIndex
  useEffect(() => {
    setActiveIndex(0)
  }, [query])

  // 关闭时清空
  useEffect(() => {
    if (!open) {
      setQuery('')
      setDynamic([])
    }
  }, [open])

  // 键盘上下选择 + Enter 执行
  const onInputKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      setActiveIndex((i) => Math.min(i + 1, flat.length - 1))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setActiveIndex((i) => Math.max(i - 1, 0))
    } else if (e.key === 'Enter') {
      e.preventDefault()
      const cmd = flat[activeIndex]
      if (cmd) run(cmd)
    }
  }

  // 滚动活动项到可视区
  useEffect(() => {
    if (!open) return
    const el = listRef.current?.querySelector(`[data-idx="${activeIndex}"]`)
    el?.scrollIntoView({ block: 'nearest' })
  }, [activeIndex, open])

  const run = (cmd: Command) => {
    // nav 组的静态命令 action 为空（模块级常量无法调用 useNavigate），
    // key 即路由路径，直接 navigate；其他组走各自 action（内含 navigate/选中逻辑）
    if (cmd.group === 'nav') {
      navigate(cmd.key)
    } else {
      cmd.action()
    }
    onClose()
    setQuery('')
    setDynamic([])
  }

  // 高亮匹配字符
  const highlight = (text: string) => {
    const q = query.trim()
    if (!q) return text
    const idx = text.toLowerCase().indexOf(q.toLowerCase())
    if (idx < 0) return text
    return (
      <>
        {text.slice(0, idx)}
        <mark className="bg-primary/30 rounded px-0.5 text-ink">{text.slice(idx, idx + q.length)}</mark>
        {text.slice(idx + q.length)}
      </>
    )
  }

  let runningIndex = -1

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
      width={560}
      styles={{ body: { padding: 0 } }}
      style={{ top: 80 }}
    >
      <Input
        placeholder="输入页面名 / 共享 / 用户 / 会话进行搜索..."
        autoFocus
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        onKeyDown={onInputKeyDown}
        style={{ border: 'none', borderBottom: '1px solid rgba(126,200,240,0.3)', borderRadius: 0 }}
        size="large"
      />
      <div ref={listRef} className="py-2 max-h-80 overflow-auto">
        {flat.length === 0 && (
          <div className="px-4 py-6 text-center text-fog text-sm">
            {query.trim().length < 2 ? '输入至少 2 个字符开始跨域搜索' : '无匹配结果'}
          </div>
        )}
        {grouped.map((g) => (
          <div key={g.group}>
            <div className="px-4 py-1 text-xs text-fog bg-mist/40">{GROUP_LABEL[g.group]}</div>
            {g.items.map((c) => {
              runningIndex++
              const idx = runningIndex
              return (
                <div
                  key={c.key}
                  data-idx={idx}
                  onClick={() => run(c)}
                  onMouseEnter={() => setActiveIndex(idx)}
                  className={`px-4 py-2 cursor-pointer transition-all ${
                    idx === activeIndex ? 'bg-primary/15' : 'hover:bg-primary/10'
                  }`}
                >
                  <div className="text-sm text-ink">{highlight(c.label)}</div>
                  {c.hint && <div className="text-xs text-fog truncate">{c.hint}</div>}
                </div>
              )
            })}
          </div>
        ))}
      </div>
    </Modal>
  )
}
