import { useEffect, useMemo, useRef, useState } from 'react'
import { App, Input, Modal, Tag } from 'antd'
import { useNavigate } from 'react-router-dom'
import { api, call } from '../api'
import { useUiStore } from '../stores/uiStore'
import type { Share, LocalUser, SmbSession } from '../types'

interface Command {
  key: string
  label: string
  hint: string
  group: 'nav' | 'action' | 'share' | 'user' | 'session'
  /** nav 组 action 允许为空：run() 以 key 作为路由跳转 */
  action: () => void
}

const STATIC_NAV: Command[] = [
  { key: '/', label: '前往：仪表板', hint: '概览共享与会话', group: 'nav', action: () => {} },
  {
    key: '/shares',
    label: '前往：共享管理',
    hint: '创建/编辑/删除共享',
    group: 'nav',
    action: () => {},
  },
  {
    key: '/users',
    label: '前往：用户权限',
    hint: '管理本地用户与权限',
    group: 'nav',
    action: () => {},
  },
  {
    key: '/sessions',
    label: '前往：会话监控',
    hint: '查看并断开会话',
    group: 'nav',
    action: () => {},
  },
  {
    key: '/settings',
    label: '前往：服务器配置',
    hint: 'SMB/NFS/FTP/WebDAV 配置与预设',
    group: 'nav',
    action: () => {},
  },
]

const GROUP_LABEL: Record<Command['group'], string> = {
  nav: '页面导航',
  action: '操作',
  share: '共享',
  user: '用户',
  session: '会话',
}

const GROUP_ORDER: Command['group'][] = ['nav', 'action', 'share', 'user', 'session']

const copyText = async (text: string): Promise<boolean> => {
  try {
    await navigator.clipboard.writeText(text)
    return true
  } catch {
    return false
  }
}

export default function CommandPalette({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { message } = App.useApp()
  const [query, setQuery] = useState('')
  const [dynamic, setDynamic] = useState<Command[]>([])
  const [activeIndex, setActiveIndex] = useState(0)
  const navigate = useNavigate()
  const listRef = useRef<HTMLDivElement>(null)

  // 操作类命令（组件级：需要 navigate/message/store 动作）
  const actionCommands = useMemo<Command[]>(
    () => [
      {
        key: 'act-create',
        label: '新建共享',
        hint: '打开新建共享表单',
        group: 'action',
        action: () => {
          navigate('/shares')
          useUiStore.getState().setShareCreateOpen(true)
        },
      },
      {
        key: 'act-refresh',
        label: '强制刷新当前视图',
        hint: '重新拉取共享/会话/统计（F5）',
        group: 'action',
        action: () => useUiStore.getState().triggerRefresh(),
      },
      {
        key: 'act-diagnose',
        label: '一键诊断共享连通性',
        hint: '服务/端口/路径/NTFS/权限/防火墙逐项体检',
        group: 'action',
        action: () => {
          navigate('/shares')
          useUiStore.getState().requestOpenDiagnose()
        },
      },
      {
        key: 'act-journal',
        label: '打开操作回收站',
        hint: '撤销最近的删除 / 禁用 / 权限变更',
        group: 'action',
        action: () => {
          navigate('/shares')
          useUiStore.getState().requestOpenJournal()
        },
      },
      {
        key: 'act-redetect',
        label: '重新检测协议能力',
        hint: '刷新 SMB/NFS/FTP/WebDAV 安装状态',
        group: 'action',
        action: async () => {
          const setProtocolCaps = useUiStore.getState().setProtocolCaps
          setProtocolCaps(null)
          try {
            const result = await call(api.protocol.detect)
            setProtocolCaps(result)
            message.success('协议能力已刷新')
          } catch (e) {
            message.error((e as Error).message)
          }
        },
      },
      {
        key: 'act-health',
        label: '检查服务健康',
        hint: 'PowerShell SMB 模块可用性',
        group: 'action',
        action: async () => {
          try {
            const h = await call(api.system.health)
            if (h.ok) message.success(`健康检查通过：${h.detail}`)
            else message.warning(`健康检查未通过：${h.detail}`)
          } catch (e) {
            message.error((e as Error).message)
          }
        },
      },
      {
        key: 'act-logfolder',
        label: '打开日志文件夹',
        hint: 'app.log / audit.log 所在目录',
        group: 'action',
        action: async () => {
          try {
            const err = await api.system.openLogFolder()
            if (err) message.error(`打开失败：${err}`)
            else message.success('已在资源管理器打开日志文件夹')
          } catch (e) {
            message.error((e as Error).message)
          }
        },
      },
    ],
    [message, navigate],
  )

  // 防抖跨域搜索：输入 ≥2 字符时 200ms 后拉
  useEffect(() => {
    const q = query.trim()
    if (q.length < 2) {
      // 短查询不检索；结果由 merged 门控隐藏，避免 effect 内同步 setState（set-state-in-effect）
      return
    }
    let cancelled = false
    const t = setTimeout(async () => {
      try {
        const [shares, users, sessions] = await Promise.all([
          call(() => api.adapter.list()).catch(() => [] as Share[]),
          call(api.user.list).catch(() => [] as LocalUser[]),
          call(api.session.list).catch(() => [] as SmbSession[]),
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
              s.protocol.toLowerCase().includes(ql),
          )
          .slice(0, 8)
          .forEach((s) => {
            cmds.push({
              key: `share:${s.protocol}:${s.name}`,
              label: `共享详情：${s.name}`,
              hint: `${s.protocol.toUpperCase()} · ${s.path}`,
              group: 'share',
              action: () => {
                navigate('/shares')
                useUiStore.getState().requestShareDetail(s)
              },
            })
            cmds.push({
              key: `share-path:${s.protocol}:${s.name}`,
              label: `复制路径：${s.name}`,
              hint: s.path,
              group: 'share',
              action: async () => {
                if (await copyText(s.path)) message.success('本地路径已复制')
                else message.error('复制失败')
              },
            })
          })

        users
          .filter(
            (u) =>
              u.name.toLowerCase().includes(ql) || (u.fullName || '').toLowerCase().includes(ql),
          )
          .slice(0, 8)
          .forEach((u) => {
            cmds.push({
              key: `user:${u.name}`,
              label: `用户：${u.name}`,
              hint: u.fullName || '—',
              group: 'user',
              action: () => navigate('/users'),
            })
          })

        sessions
          .filter(
            (s) =>
              s.clientUserName.toLowerCase().includes(ql) ||
              s.clientComputerName.toLowerCase().includes(ql),
          )
          .slice(0, 8)
          .forEach((s) => {
            cmds.push({
              key: `session:${s.clientId}`,
              label: `会话：${s.clientUserName}`,
              hint: `跳转并选中 · ${s.clientComputerName}`,
              group: 'session',
              action: () => {
                navigate('/sessions')
                useUiStore.getState().setSelectedSessions([s.clientId])
              },
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
  }, [message, navigate, query])

  // 合并静态（nav+action 按 query 过滤）+ 动态（<2 字符门控隐藏，异步残留结果不展示）
  const merged = useMemo(() => {
    const ql = query.trim().toLowerCase()
    const filter = (list: Command[]) =>
      ql ? list.filter((c) => c.label.toLowerCase().includes(ql)) : list
    return [
      ...filter(STATIC_NAV),
      ...filter(actionCommands),
      ...(query.trim().length >= 2 ? dynamic : []),
    ]
  }, [actionCommands, dynamic, query])

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

  // 输入变化/关闭时重置选中项与输入——React 官方"渲染期调整 state"模式（规避级联 effect setState）
  const [prevQuery, setPrevQuery] = useState(query)
  if (prevQuery !== query) {
    setPrevQuery(query)
    setActiveIndex(0)
  }
  const [prevOpen, setPrevOpen] = useState(open)
  if (prevOpen !== open) {
    setPrevOpen(open)
    if (!open) {
      setQuery('')
      setDynamic([])
    }
  }

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
    // nav 组以 key 作为路由；action/share/user/session 组走各自闭包
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
        <mark className="bg-primary/30 rounded px-0.5 text-ink">
          {text.slice(idx, idx + q.length)}
        </mark>
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
        placeholder="搜索页面 / 操作 / 共享 / 用户 / 会话（≥2 字符跨域搜索）..."
        autoFocus
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        onKeyDown={onInputKeyDown}
        style={{ border: 'none', borderBottom: '1px solid rgba(126,200,240,0.3)', borderRadius: 0 }}
        size="large"
      />
      <div ref={listRef} className="py-2 max-h-96 overflow-auto">
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
      <div className="px-4 py-2 text-xs text-fog border-t border-black/5 flex gap-3">
        <Tag style={{ margin: 0 }}>↑↓ 选择</Tag>
        <Tag style={{ margin: 0 }}>Enter 执行</Tag>
        <Tag style={{ margin: 0 }}>Esc 关闭</Tag>
      </div>
    </Modal>
  )
}
