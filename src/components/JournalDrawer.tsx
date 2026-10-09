import { useEffect, useMemo, useState } from 'react'
import {
  App,
  Button,
  Drawer,
  Empty,
  Input,
  Popconfirm,
  Select,
  Space,
  Spin,
  Switch,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd'
import type { TableColumnsType } from 'antd'
import {
  DeleteOutlined,
  EditOutlined,
  PauseCircleOutlined,
  PlusCircleOutlined,
  ReloadOutlined,
  SearchOutlined,
  UndoOutlined,
} from '@ant-design/icons'
import dayjs from 'dayjs'
import type { JournalEntry, Protocol } from '../types'
import { useAppStore } from '../stores/appStore'

interface Props {
  open: boolean
  onClose: () => void
}

// 协议 Tag 配色（与 Dashboard / Shares 一致）
const PROTOCOL_TAG_COLOR: Record<Protocol, string> = {
  smb: 'blue',
  nfs: 'purple',
  ftp: 'green',
  webdav: 'orange',
}

/**
 * 操作类型的可视化语义：颜色 + 图标。
 * 回收站的主要用法是"扫一眼找到刚才那个操作"，纯文字标签在 200 条里扫不动，
 * 所以用颜色区分危险度（删除红 / 禁用橙 / 权限蓝 / 创建灰）。
 */
const ACTION_META: Record<
  JournalEntry['action'],
  { label: string; color: string; icon: React.ReactNode }
> = {
  create: { label: '创建', color: 'default', icon: <PlusCircleOutlined /> },
  delete: { label: '删除', color: 'error', icon: <DeleteOutlined /> },
  'toggle-off': { label: '禁用', color: 'warning', icon: <PauseCircleOutlined /> },
  permset: { label: '权限变更', color: 'processing', icon: <EditOutlined /> },
}

const ACTION_OPTIONS = [
  { value: 'all', label: '全部类型' },
  { value: 'delete', label: '删除' },
  { value: 'toggle-off', label: '禁用' },
  { value: 'permset', label: '权限变更' },
  { value: 'create', label: '创建' },
]

/**
 * 回收站的时间语义是"多久以前"，不是"哪一天几点"：
 * 用户找的是"我刚才删的那个"。精确时间戳保留在 title 里（审计场景可悬停取证），
 * 超过一周则直接显示绝对时间——"37 天前"这种反而要心算。
 */
export function relTime(ts: number, now: number = Date.now()): string {
  const full = dayjs(ts).format('YYYY-MM-DD HH:mm')
  const diff = now - ts
  if (diff < 0) return full // 时钟回拨/未来时间：不硬凹相对值
  const min = Math.floor(diff / 60_000)
  if (min < 1) return '刚刚'
  if (min < 60) return `${min} 分钟前`
  const hour = Math.floor(min / 60)
  if (hour < 24) return `${hour} 小时前`
  const day = Math.floor(hour / 24)
  if (day < 7) return `${day} 天前`
  return full
}

/** 不可撤销的原因：把重复长句收进 tooltip，按记录给出真实理由而不是一句通用话术 */
export function archiveReason(e: JournalEntry): string {
  if (e.action === 'create') {
    return '创建操作不提供一键撤销（如需移除，请在共享列表手工删除该共享）'
  }
  if (e.protocol !== 'smb') {
    return `${e.protocol.toUpperCase()} 的删除没有可还原的快照，仅留档供手工重建`
  }
  return '该操作未留下可还原的快照，仅留档'
}

export default function JournalDrawer({ open, onClose }: Props) {
  const { message } = App.useApp()
  const journal = useAppStore((s) => s.journal)
  const loadJournal = useAppStore((s) => s.loadJournal)
  const undoJournal = useAppStore((s) => s.undoJournal)
  const clearJournal = useAppStore((s) => s.clearJournal)

  const [loading, setLoading] = useState(false)
  const [clearing, setClearing] = useState(false)
  const [undoingId, setUndoingId] = useState<string | null>(null)
  // 筛选：200 条里找"刚才删的那个共享"，靠肉眼扫是不现实的
  const [keyword, setKeyword] = useState('')
  const [actionFilter, setActionFilter] = useState<'all' | JournalEntry['action']>('all')
  const [onlyUndoable, setOnlyUndoable] = useState(false)

  const fetchList = (showSpinner: boolean) => {
    if (showSpinner) setLoading(true)
    loadJournal()
      .catch((e: unknown) => {
        message.error((e as Error).message)
      })
      .finally(() => setLoading(false))
  }

  // 打开即拉取；loadJournal 稳定，纳入依赖不会循环
  useEffect(() => {
    if (!open) return
    // 已有数据时不闪 loading（stale-while-revalidate）：抽屉打开应立刻可读，
    // 而不是先白一下再出内容
    fetchList(useAppStore.getState().journal.length === 0)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- 有意只在打开瞬间拉一次
  }, [open, loadJournal, message])

  // 兜底按新→旧（主进程已 reverse，此处防御时间乱序）
  const sorted = useMemo(() => [...journal].sort((a, b) => b.ts - a.ts), [journal])

  const rows = useMemo(() => {
    const kw = keyword.trim().toLowerCase()
    return sorted.filter((e) => {
      if (onlyUndoable && !e.undoable) return false
      if (actionFilter !== 'all' && e.action !== actionFilter) return false
      if (!kw) return true
      return (
        e.name.toLowerCase().includes(kw) ||
        (e.detail ?? '').toLowerCase().includes(kw) ||
        e.protocol.toLowerCase().includes(kw)
      )
    })
  }, [sorted, keyword, actionFilter, onlyUndoable])

  const undoableCount = useMemo(() => sorted.filter((e) => e.undoable).length, [sorted])
  const filtered = keyword.trim() !== '' || actionFilter !== 'all' || onlyUndoable

  const handleUndo = async (id: string) => {
    setUndoingId(id)
    try {
      // 成功：store 已返回主进程结果文案，并在内部刷新列表
      const msg = await undoJournal(id)
      message.success(msg || '已撤销该操作')
    } catch (e) {
      // 失败：透传原始错误，绝不吞错 / 假成功（store 抛错时未触发刷新，列表保持原样）
      message.error((e as Error).message)
    } finally {
      setUndoingId(null)
    }
  }

  const handleClear = async () => {
    setClearing(true)
    try {
      await clearJournal()
      message.success('操作记录已清空')
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setClearing(false)
    }
  }

  const resetFilters = () => {
    setKeyword('')
    setActionFilter('all')
    setOnlyUndoable(false)
  }

  const columns: TableColumnsType<JournalEntry> = [
    {
      title: '时间',
      dataIndex: 'ts',
      // 绝对时间（超过一周时）比相对时间宽，104px 会把 "2026-08-30 20:08" 折成两行、行高参差
      width: 124,
      render: (ts: number) => {
        const full = dayjs(ts).format('YYYY-MM-DD HH:mm:ss')
        return (
          <Tooltip title={full}>
            <span title={full} style={{ whiteSpace: 'nowrap' }}>
              {relTime(ts)}
            </span>
          </Tooltip>
        )
      },
    },
    {
      title: '类型',
      dataIndex: 'action',
      width: 104,
      render: (action: JournalEntry['action']) => {
        const meta = ACTION_META[action]
        if (!meta) return <span className="text-fog">{action}</span>
        return (
          <Tag color={meta.color} icon={meta.icon} style={{ marginInlineEnd: 0 }}>
            {meta.label}
          </Tag>
        )
      },
    },
    {
      title: '协议',
      dataIndex: 'protocol',
      width: 78,
      render: (p: Protocol) => (
        <Tag color={PROTOCOL_TAG_COLOR[p] ?? 'default'} style={{ marginInlineEnd: 0 }}>
          {p?.toUpperCase() ?? p}
        </Tag>
      ),
    },
    {
      title: '共享名',
      dataIndex: 'name',
      width: 150,
      ellipsis: true,
      render: (name: string) => (
        <Tooltip title={name}>
          <Typography.Text strong style={{ fontSize: 12 }}>
            {name}
          </Typography.Text>
        </Tooltip>
      ),
    },
    {
      title: '详情',
      dataIndex: 'detail',
      // 单行截断 + tooltip：原来 break-all 会把行高撑成两三层，表格节奏全乱
      ellipsis: true,
      render: (detail?: string) =>
        detail ? (
          <Tooltip title={detail}>
            <span className="text-fog text-xs">{detail}</span>
          </Tooltip>
        ) : (
          <span className="text-fog">—</span>
        ),
    },
    {
      title: '可撤销',
      key: 'undo',
      width: 96,
      render: (_: unknown, r: JournalEntry) =>
        r.undoable ? (
          <Popconfirm
            title={`撤销这次${ACTION_META[r.action]?.label ?? '操作'}？`}
            description={
              <div style={{ maxWidth: 260 }}>
                按留档快照恢复「{r.name}」。
                {r.detail ? <div className="text-fog text-xs mt-1">{r.detail}</div> : null}
              </div>
            }
            okText="确认撤销"
            cancelText="取消"
            okButtonProps={{ danger: true, loading: undoingId === r.id }}
            onConfirm={() => handleUndo(r.id)}
          >
            <Button
              size="small"
              icon={<UndoOutlined />}
              loading={undoingId === r.id}
              aria-label={`撤销 ${r.name} 的${ACTION_META[r.action]?.label ?? '操作'}`}
            >
              撤销
            </Button>
          </Popconfirm>
        ) : (
          <Tooltip title={archiveReason(r)}>
            <Tag style={{ marginInlineEnd: 0 }}>仅留档</Tag>
          </Tooltip>
        ),
    },
  ]

  const emptyText = filtered ? (
    <Empty description="没有匹配的记录">
      <Button size="small" onClick={resetFilters}>
        清除筛选
      </Button>
    </Empty>
  ) : (
    <Empty description="还没有可撤销的操作">
      <span className="text-xs text-fog">
        删除或禁用共享、修改共享权限后，会在这里留下可撤销的记录
      </span>
    </Empty>
  )

  return (
    <Drawer
      title="操作回收站"
      open={open}
      onClose={onClose}
      // antd v6：width 已弃用（运行时会打 deprecation 警告），size 接受数字且语义相同
      size={760}
      destroyOnClose
      styles={{
        body: {
          background: 'rgba(255,255,255,0.75)',
          backdropFilter: 'blur(16px)',
        },
      }}
    >
      {/* 摘要：一眼知道有多少可撤销、最近一次是什么时候 */}
      <div className="mb-2 text-xs text-fog">
        共 <b>{sorted.length}</b> 条记录（最多 200 条），其中 <b>{undoableCount}</b> 条可一键撤销
        {sorted.length > 0 && <> · 最近一次 {relTime(sorted[0].ts)}</>}
      </div>

      <div className="mb-3 flex flex-wrap items-center gap-2">
        <Input
          allowClear
          size="small"
          placeholder="搜索共享名 / 路径 / 详情"
          prefix={<SearchOutlined className="text-fog" />}
          value={keyword}
          onChange={(e) => setKeyword(e.target.value)}
          style={{ width: 220 }}
          aria-label="搜索操作记录"
        />
        <Select
          size="small"
          value={actionFilter}
          onChange={(v) => setActionFilter(v)}
          options={ACTION_OPTIONS}
          style={{ width: 120 }}
          aria-label="按操作类型筛选"
        />
        <Space size={6}>
          <Switch
            size="small"
            checked={onlyUndoable}
            onChange={setOnlyUndoable}
            aria-label="只看可撤销"
          />
          <span className="text-xs text-fog">只看可撤销</span>
        </Space>
        <span className="ml-auto" />
        <Button size="small" icon={<ReloadOutlined />} onClick={() => fetchList(true)}>
          刷新
        </Button>
        {sorted.length > 0 && (
          <Popconfirm
            title={`确认清空全部 ${sorted.length} 条操作记录？`}
            description="清空的是留档与快照记录，已恢复的共享不受影响；清空后无法再撤销"
            okText="确认清空"
            cancelText="取消"
            okButtonProps={{ danger: true, loading: clearing }}
            onConfirm={handleClear}
          >
            <Button size="small" danger icon={<DeleteOutlined />} loading={clearing}>
              清空记录
            </Button>
          </Popconfirm>
        )}
      </div>

      <Spin spinning={loading && sorted.length === 0}>
        <Table
          dataSource={rows}
          rowKey="id"
          columns={columns}
          size="small"
          pagination={false}
          scroll={{ y: 'calc(100vh - 260px)' }}
          locale={{ emptyText }}
        />
      </Spin>
    </Drawer>
  )
}
