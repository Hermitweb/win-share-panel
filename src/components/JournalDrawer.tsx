import { useEffect, useMemo, useState } from 'react'
import { App, Button, Drawer, Empty, Popconfirm, Space, Spin, Table, Tag, Typography } from 'antd'
import type { TableColumnsType } from 'antd'
import { DeleteOutlined, ReloadOutlined, UndoOutlined } from '@ant-design/icons'
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

// 操作类型中文标签
const ACTION_LABEL: Record<JournalEntry['action'], string> = {
  create: '创建',
  delete: '删除',
  'toggle-off': '禁用',
  permset: '权限变更',
}

// 非 SMB / create 仅留档：如实说明，不给坏按钮
const ARCHIVE_NOTE = '仅留档、不支持一键撤销'

export default function JournalDrawer({ open, onClose }: Props) {
  const { message } = App.useApp()
  const journal = useAppStore((s) => s.journal)
  const loadJournal = useAppStore((s) => s.loadJournal)
  const undoJournal = useAppStore((s) => s.undoJournal)
  const clearJournal = useAppStore((s) => s.clearJournal)

  const [loading, setLoading] = useState(false)
  const [clearing, setClearing] = useState(false)
  const [undoingId, setUndoingId] = useState<string | null>(null)

  // 打开即拉取；loadJournal 稳定，纳入依赖不会循环
  useEffect(() => {
    if (!open) return
    setLoading(true)
    loadJournal()
      .catch((e: unknown) => {
        message.error((e as Error).message)
      })
      .finally(() => setLoading(false))
  }, [open, loadJournal, message])

  // 兜底按新→旧（主进程已 reverse，此处防御时间乱序）
  const rows = useMemo(() => [...journal].sort((a, b) => b.ts - a.ts), [journal])

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

  const reload = () => {
    setLoading(true)
    loadJournal()
      .catch((e: unknown) => {
        message.error((e as Error).message)
      })
      .finally(() => setLoading(false))
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

  const columns: TableColumnsType<JournalEntry> = [
    {
      title: '时间',
      dataIndex: 'ts',
      width: 160,
      render: (ts: number) => dayjs(ts).format('YYYY-MM-DD HH:mm:ss'),
    },
    {
      title: '操作',
      dataIndex: 'action',
      width: 90,
      render: (action: JournalEntry['action']) => ACTION_LABEL[action] ?? action,
    },
    {
      title: '协议',
      dataIndex: 'protocol',
      width: 90,
      render: (p: Protocol) => (
        <Tag color={PROTOCOL_TAG_COLOR[p] ?? 'default'}>{p?.toUpperCase() ?? p}</Tag>
      ),
    },
    {
      title: '共享名',
      dataIndex: 'name',
      width: 140,
      ellipsis: true,
      render: (name: string) => <Typography.Text style={{ fontSize: 12 }}>{name}</Typography.Text>,
    },
    {
      title: '详情',
      dataIndex: 'detail',
      render: (detail?: string) =>
        detail ? (
          <span className="text-fog text-xs break-all">{detail}</span>
        ) : (
          <span className="text-fog">—</span>
        ),
    },
    {
      title: '操作',
      key: 'undo',
      width: 150,
      render: (_: unknown, r: JournalEntry) =>
        r.undoable ? (
          <Popconfirm
            title="撤销该操作？"
            description="将按留档快照尝试恢复"
            okText="确认撤销"
            cancelText="取消"
            okButtonProps={{ danger: true, loading: undoingId === r.id }}
            onConfirm={() => handleUndo(r.id)}
          >
            <Button size="small" danger icon={<UndoOutlined />} loading={undoingId === r.id}>
              撤销
            </Button>
          </Popconfirm>
        ) : (
          <span className="text-fog text-xs">{ARCHIVE_NOTE}</span>
        ),
    },
  ]

  return (
    <Drawer
      title="操作回收站"
      open={open}
      onClose={onClose}
      width={720}
      destroyOnClose
      styles={{
        body: {
          background: 'rgba(255,255,255,0.75)',
          backdropFilter: 'blur(16px)',
        },
      }}
    >
      <div className="mb-3 flex items-center justify-between">
        <span className="text-xs text-fog">
          最近操作（最多 200 条）。可撤销项点击「撤销」按快照恢复；仅留档项需手工重建。
        </span>
        <Space>
          <Button size="small" icon={<ReloadOutlined />} onClick={reload}>
            刷新
          </Button>
          {rows.length > 0 && (
            <Popconfirm
              title="确认清空全部操作记录？"
              description="清空后无法恢复留档快照"
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
        </Space>
      </div>
      <Spin spinning={loading}>
        <Table
          dataSource={rows}
          rowKey="id"
          columns={columns}
          size="small"
          pagination={false}
          scroll={{ y: 'calc(100vh - 220px)' }}
          locale={{ emptyText: <Empty description="暂无操作记录" /> }}
        />
      </Spin>
    </Drawer>
  )
}
